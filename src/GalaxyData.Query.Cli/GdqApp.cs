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

   /// <summary>Opens the sources and builds the engine; what their schemas left out is written to <paramref name="error"/>.</summary>
   public static async Task<Session> OpenAsync(IEnumerable<string> sources, FileInfo? overlayFile, IEnumerable<string> parameters, string? mergeMemory,
                                               TextWriter error, CancellationToken cancellationToken)
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
         opened = await CliSources.OpenAsync(specs, excel, cancellationToken);
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

/// <summary>The <c>gdq</c> command line: run, sql, schema and repl.</summary>
internal static class GdqApp
{
   public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
   {
      Option<string[]> sources = new("--source", "-s")
      {
         Description = "A source as alias=kind:target, e.g. shop=sqlite:shop.db. Kinds: sqlite, duckdb, postgres and sqlserver (a connection string), excel (a folder of .xlsx workbooks). A .sql target is run into a new in-memory database; files open read-only.",
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

      Command run = new("run", "Run a query and print its rows.") { sources, overlay, parameters, format, sql, maxRows, mergeMemory, file, query };
      Command sqlCommand = new("sql", "Print the SQL each source runs for a query, without running it.") { sources, overlay, parameters, file, query };
      Command explain = new("explain", "Explain a query without running it: plan, columns with lineage, links and edit targets, and SQL.")
      {
         sources, overlay, parameters, file, verbose, query,
      };
      Command schema = new("schema", "List the entities of the catalog with their columns and navigations.") { sources, overlay, filter };
      Command repl = new("repl", "Run queries interactively.") { sources, overlay, parameters, format, maxRows, mergeMemory };
      RootCommand root = new("gdq: query SQLite and DuckDB databases and folders of Excel workbooks with the GalaxyData query language.") { run, sqlCommand, explain, schema, repl };

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
