using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Explain;

namespace GalaxyData.Query.Cli;

/// <summary>What to show for each query; the REPL changes it as it goes.</summary>
internal sealed class Settings
{
   public OutputFormat Format { get; set; } = OutputFormat.Table;

   public bool ShowSql { get; set; }

   /// <summary>Print the explain (plan, columns with lineage and links, SQL) before the rows.</summary>
   public bool ShowExplain { get; set; }

   public int MaxRows { get; set; } = 1000;
}

/// <summary>
/// An engine over the command line's sources, with its parameters and a merge engine for queries that combine
/// sources (and to keep the sheets of Excel folders in).
/// </summary>
internal sealed class Session : IAsyncDisposable
{
   private readonly DuckDb.DuckDbMergeEngine merge;
   private readonly Excel.ExcelSourceProvider excel;

   private Session(CliSources sources, QueryEngine engine, DuckDb.DuckDbMergeEngine merge, Excel.ExcelSourceProvider excel, QueryParameters parameters)
   {
      Sources = sources;
      Engine = engine;
      this.merge = merge;
      this.excel = excel;
      Parameters = parameters;
   }

   public CliSources Sources { get; }

   public QueryEngine Engine { get; }

   public QueryParameters Parameters { get; }

   /// <summary>
   /// Opens the sources (read-only, but those named in <paramref name="writable"/>) and builds the engine; what their
   /// schemas left out is written to <paramref name="error"/>.
   /// </summary>
   public static async Task<Session> OpenAsync(IEnumerable<string> sources, FileInfo? overlayFile, IEnumerable<string> parameters, string? mergeMemory,
                                               TextWriter error, CancellationToken cancellationToken, IEnumerable<string>? writable = null)
   {
      QueryParameters values = new();
      foreach (string parameter in parameters) { ParameterSpec.AddTo(values, parameter); }
      CatalogOverlay overlay = overlayFile == null
         ? CatalogOverlay.Empty
         : CatalogOverlay.FromJson(await File.ReadAllTextAsync(overlayFile.FullName, cancellationToken));
      List<SourceSpec> specs = sources.Select(SourceSpec.Parse).ToList();
      DuckDb.DuckDbMergeEngine merge = new(new DuckDb.DuckDbMergeOptions { MemoryLimit = mergeMemory });
      Excel.ExcelSourceProvider excel = new(merge);
      CliSources opened;
      try
      {
         opened = await CliSources.OpenAsync(specs, excel, (writable ?? []).ToHashSet(StringComparer.Ordinal), cancellationToken);
      }
      catch
      {
         excel.Dispose();
         merge.Dispose();
         throw;
      }
      foreach (string warning in opened.Warnings) { await error.WriteLineAsync("gdq: warning: " + warning); }
      QueryEngine engine = new(opened.BuildCatalog(overlay), opened,
         [Sqlite.SqliteSourceProvider.Instance, DuckDb.DuckDbSourceProvider.Instance, PostgreSql.PostgreSqlSourceProvider.Instance,
          SqlServer.SqlServerSourceProvider.Instance, excel], merge);
      return new Session(opened, engine, merge, excel, values);
   }

   public async ValueTask DisposeAsync()
   {
      await Sources.DisposeAsync();
      excel.Dispose();
      merge.Dispose();
   }
}

/// <summary>The <c>gdq</c> command line: run, sql, explain, schema, repl, and changes and script to write data.</summary>
internal static class GdqApp
{
   public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
   {
      Option<string[]> sources = new("--source", "-s")
      {
         Description = "A source as alias=kind:target, e.g. shop=sqlite:shop.db. Kinds: sqlite, duckdb, postgres and sqlserver (a connection string), excel (a folder of .xlsx workbooks). A .sql target is run into a new in-memory database; files open read-only, unless written (--write).",
         Required = true,
      };
      Option<FileInfo?> overlay = new("--overlay") { Description = "A catalog overlay (JSON): relations across sources, virtual entities, renames." };
      Option<string[]> parameters = new("--param", "-p") { Description = "A query parameter as name=value, used as $name." };
      Option<OutputFormat> format = new("--format") { Description = "How to print rows: table, csv or json.", DefaultValueFactory = _ => OutputFormat.Table };
      Option<bool> sql = new("--sql") { Description = "Print the SQL each source runs." };
      Option<int> maxRows = new("--max-rows") { Description = "Print at most this many rows.", DefaultValueFactory = _ => 1000 };
      Option<FileInfo?> file = new("--file", "-f") { Description = "Read the query from a file." };
      Option<bool> verbose = new("--verbose", "-v") { Description = "Also print the plan as lowered and after each optimizer phase." };
      Option<string?> mergeMemory = new("--merge-memory")
      {
         Description = "How much memory the merge engine may use for queries that combine sources, e.g. 2GB; past it, data goes to temp files.",
      };
      Argument<string?> query = new("query") { Description = "The query.", Arity = ArgumentArity.ZeroOrOne };
      Argument<string?> filter = new("filter") { Description = "Only entities whose names contain this.", Arity = ArgumentArity.ZeroOrOne };
      Option<string[]> writable = new("--write", "-w")
      {
         Description = "A source that may be written, by its alias (its files open read-write); the others are read-only.",
      };
      Option<bool> commit = new("--commit") { Description = "Write the changes, in a transaction on each connection; without it, they are only shown." };
      Option<bool> anyStatement = new("--any-statement") { Description = "Let the script run statements that don't change data, as an administrator may." };
      Argument<string> target = new("source") { Description = "The alias of the source the script runs on." };

      Command run = new("run", "Run a query and print its rows.") { sources, overlay, parameters, format, sql, maxRows, mergeMemory, file, query };
      Command sqlCommand = new("sql", "Print the SQL each source runs for a query, without running it.") { sources, overlay, parameters, file, query };
      Command explain = new("explain", "Explain a query without running it: plan, columns with lineage, links and edit targets, and SQL.")
      {
         sources, overlay, parameters, file, verbose, query,
      };
      Command schema = new("schema", "List the entities of the catalog with their columns and navigations.") { sources, overlay, filter };
      Command repl = new("repl", "Run queries interactively.") { sources, overlay, parameters, format, maxRows, mergeMemory };
      Command changes = new("changes", "Show the statements that make the changes to rows in a JSON file (--file), and with --commit write them.")
      {
         sources, overlay, writable, file, commit,
      };
      Command script = new("script", "Check a script of data changes (--file) for one source, and with --commit run it in a transaction.")
      {
         sources, writable, file, anyStatement, commit, target,
      };
      RootCommand root = new("gdq: query databases and folders of Excel workbooks with the GalaxyData query language, and change their rows.")
      {
         run, sqlCommand, explain, schema, repl, changes, script,
      };

      run.SetAction((parse, token) => Guarded(error, async () =>
      {
         string text = await QueryText(parse.GetValue(query), parse.GetValue(file), token);
         await using Session session = await Session.OpenAsync(parse.GetValue(sources)!, parse.GetValue(overlay), parse.GetValue(parameters) ?? [],
                                                              parse.GetValue(mergeMemory), error, token);
         Settings settings = new() { Format = parse.GetValue(format), ShowSql = parse.GetValue(sql), MaxRows = parse.GetValue(maxRows) };
         return await ExecuteAsync(session, text, settings, output, error, token);
      }));
      sqlCommand.SetAction((parse, token) => Guarded(error, async () =>
      {
         string text = await QueryText(parse.GetValue(query), parse.GetValue(file), token);
         await using Session session = await Session.OpenAsync(parse.GetValue(sources)!, parse.GetValue(overlay), parse.GetValue(parameters) ?? [], null, error, token);
         PreparedQuery prepared = session.Engine.Prepare(text, session.Parameters);
         Output.Diagnostics(error, text, prepared.Diagnostics);
         if (!prepared.Success) { return 1; }
         Output.Fragments(output, prepared);
         return 0;
      }));
      explain.SetAction((parse, token) => Guarded(error, async () =>
      {
         string text = await QueryText(parse.GetValue(query), parse.GetValue(file), token);
         await using Session session = await Session.OpenAsync(parse.GetValue(sources)!, parse.GetValue(overlay), parse.GetValue(parameters) ?? [], null, error, token);
         return Explain(session, text, parse.GetValue(verbose), output);
      }));
      schema.SetAction((parse, token) => Guarded(error, async () =>
      {
         await using Session session = await Session.OpenAsync(parse.GetValue(sources)!, parse.GetValue(overlay), [], null, error, token);
         Output.Catalog(output, session.Engine.Catalog, parse.GetValue(filter));
         return 0;
      }));
      repl.SetAction((parse, token) => Guarded(error, async () =>
      {
         await using Session session = await Session.OpenAsync(parse.GetValue(sources)!, parse.GetValue(overlay), parse.GetValue(parameters) ?? [],
                                                              parse.GetValue(mergeMemory), error, token);
         Settings settings = new() { Format = parse.GetValue(format), MaxRows = parse.GetValue(maxRows) };
         return await new Repl(session, input, output, error, settings).RunAsync(token);
      }));

      changes.SetAction((parse, token) => Guarded(error, async () =>
      {
         FileInfo changeFile = parse.GetValue(file) ?? throw new UsageException("Give the changes in a JSON file, with --file");
         string json = await File.ReadAllTextAsync(changeFile.FullName, token);
         await using Session session = await Session.OpenAsync(parse.GetValue(sources)!, parse.GetValue(overlay), [], null, error, token, parse.GetValue(writable));
         DmlPlan plan = session.Engine.PlanChanges(ChangeFile.Parse(json, session.Engine.Catalog));
         await output.WriteAsync(plan.ToDisplayText());
         foreach (DmlIssue issue in plan.Issues) { await error.WriteLineAsync($"gdq: change {issue.ChangeIndex}: {issue.Message}"); }
         if (!plan.Success) { return 1; }
         if (plan.IsMultiConnection)
         {
            await error.WriteLineAsync($"gdq: warning: the changes are written on {plan.Scripts.Count} connections, each committing after the other: " +
                                       "should one fail to commit after another has, they are left partly written");
         }
         return parse.GetValue(commit) ? await CommitAsync(session.Engine, plan.Scripts, output, token) : 0;
      }));
      script.SetAction((parse, token) => Guarded(error, async () =>
      {
         FileInfo scriptFile = parse.GetValue(file) ?? throw new UsageException("Give the script in a file, with --file");
         string text = await File.ReadAllTextAsync(scriptFile.FullName, token);
         await using Session session = await Session.OpenAsync(parse.GetValue(sources)!, null, [], null, error, token, parse.GetValue(writable));
         string alias = parse.GetValue(target)!;
         SourceInfo source = session.Engine.Catalog.FindSource(alias) ?? throw new UsageException($"'{alias}' is no source");
         DmlScript parsed;
         try
         {
            parsed = session.Engine.ParseScript(source, text, parse.GetValue(anyStatement));
         }
         catch (DmlScriptException e)
         {
            foreach (ScriptProblem problem in e.Problems) { await error.WriteLineAsync($"gdq: line {problem.Line}: {problem.Message}"); }
            return 1;
         }
         await output.WriteAsync(parsed.ToDisplayText());
         return parse.GetValue(commit) ? await CommitAsync(session.Engine, [parsed], output, token) : 0;
      }));

      InvocationConfiguration configuration = new() { Output = output, Error = error };
      return await root.Parse(args).InvokeAsync(configuration, cancellationToken);
   }

   private static async Task<string> QueryText(string? query, FileInfo? file, CancellationToken cancellationToken)
   {
      if (file != null) { return await File.ReadAllTextAsync(file.FullName, cancellationToken); }
      return string.IsNullOrWhiteSpace(query) ? throw new UsageException("Give a query, or a file with --file") : query;
   }

   /// <summary>Runs a command, turning the failures a user can cause into a message and exit code 2.</summary>
   private static async Task<int> Guarded(TextWriter error, Func<Task<int>> action)
   {
      try
      {
         return await action();
      }
      catch (Exception e) when (e is UsageException or FormatException or IOException or DbException or JsonException or QueryExecutionException)
      {
         await error.WriteLineAsync("gdq: " + e.Message);
         return 2;
      }
      catch (Exception e) when (e is not OperationCanceledException)
      {
         await error.WriteLineAsync($"gdq: something went wrong inside gdq ({e.GetType().Name}): {e.Message}");
         return 3;
      }
   }

   /// <summary>Prepares and runs one query, printing its rows; 1 when it doesn't bind or plan, 2 when it fails while running.</summary>
   public static async Task<int> ExecuteAsync(Session session, string text, Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
   {
      PreparedQuery prepared = session.Engine.Prepare(text, session.Parameters);
      Output.Diagnostics(error, text, prepared.Diagnostics);
      if (!prepared.Success) { return 1; }
      if (settings.ShowExplain) { output.WriteLine(ExplainTextRenderer.Render(prepared.Explain())); }
      else if (settings.ShowSql) { Output.Fragments(output, prepared); }
      try
      {
         await using QueryResult result = await prepared.ExecuteAsync(cancellationToken);
         List<object?[]> rows = [];
         bool more = false;
         while (await result.ReadAsync(cancellationToken))
         {
            if (rows.Count == settings.MaxRows)
            {
               more = true;
               break;
            }
            rows.Add(result.Current);
         }
         Output.Rows(output, result.Schema, rows, settings.Format);
         if (settings.Format == OutputFormat.Table)
         {
            string count = rows.Count == 1 ? "1 row" : $"{rows.Count} rows";
            output.WriteLine($"({count}{(more ? ", more not shown" : string.Empty)}; {result.Stats.Elapsed.TotalMilliseconds:0} ms)");
         }
         return 0;
      }
      catch (QueryExecutionException e)
      {
         await error.WriteLineAsync("gdq: " + e.Message);
         return 2;
      }
   }

   /// <summary>Runs scripts as one change and says what came of it: 0 when every connection committed, 2 otherwise.</summary>
   private static async Task<int> CommitAsync(QueryEngine engine, IReadOnlyList<DmlScript> scripts, TextWriter output, CancellationToken cancellationToken)
   {
      DmlResult result = await engine.CommitAsync(scripts, cancellationToken);
      await output.WriteLineAsync();
      await output.WriteLineAsync(result.Outcome switch
      {
         DmlOutcome.Committed => "Committed.",
         DmlOutcome.RolledBack => "Nothing was written: " + result.Failure!.Message,
         _ => "Partly written: " + result.Failure!.Message,
      });
      foreach (DmlScriptResult script in result.Scripts)
      {
         if (script.Status != DmlScriptStatus.Committed)
         {
            await output.WriteLineAsync($"{script.Script.Source.Alias}: {(script.Status == DmlScriptStatus.RolledBack ? "rolled back" : "failed to commit")}");
            continue;
         }
         long changed = script.Statements.Where(s => s.RowsChanged > 0).Sum(s => s.RowsChanged);
         await output.WriteLineAsync($"{script.Script.Source.Alias}: committed, {changed} {(changed == 1 ? "row" : "rows")} changed");
         foreach (DmlStatementResult statement in script.Statements.Where(s => s.Row != null))
         {
            IEnumerable<string> values = statement.Statement.ReturnedColumns.Select((c, i) => $"{c.Name} = {Output.Text(statement.Row![i]) ?? "null"}");
            await output.WriteLineAsync($"  {statement.Statement.Description}: {string.Join(", ", values)}");
         }
      }
      return result.Success ? 0 : 2;
   }

   /// <summary>Explains a query; 1 when it doesn't bind or plan (the explain says why).</summary>
   public static int Explain(Session session, string text, bool verbose, TextWriter output)
   {
      PreparedQuery prepared = session.Engine.Prepare(text, session.Parameters);
      output.Write(ExplainTextRenderer.Render(prepared.Explain(verbose)));
      return prepared.Success ? 0 : 1;
   }
}

/// <summary>A mistake in how gdq was called.</summary>
internal sealed class UsageException(string message) : Exception(message);
