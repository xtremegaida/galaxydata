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
using GalaxyData.Query.Planning;

namespace GalaxyData.Query.Cli;

/// <summary>What to show for each query; the REPL changes it as it goes.</summary>
internal sealed class Settings
{
   public OutputFormat Format { get; set; } = OutputFormat.Table;

   public bool ShowSql { get; set; }

   public bool ShowPlan { get; set; }

   public int MaxRows { get; set; } = 1000;
}

/// <summary>An engine over the command line's sources, with its parameters.</summary>
internal sealed class Session : IAsyncDisposable
{
   private Session(CliSources sources, QueryEngine engine, QueryParameters parameters)
   {
      Sources = sources;
      Engine = engine;
      Parameters = parameters;
   }

   public CliSources Sources { get; }

   public QueryEngine Engine { get; }

   public QueryParameters Parameters { get; }

   public static async Task<Session> OpenAsync(IEnumerable<string> sources, FileInfo? overlayFile, IEnumerable<string> parameters, CancellationToken cancellationToken)
   {
      QueryParameters values = new();
      foreach (string parameter in parameters) { ParameterSpec.AddTo(values, parameter); }
      CatalogOverlay overlay = overlayFile == null
         ? CatalogOverlay.Empty
         : CatalogOverlay.FromJson(await File.ReadAllTextAsync(overlayFile.FullName, cancellationToken));
      CliSources opened = await CliSources.OpenAsync(sources.Select(SourceSpec.Parse).ToList(), cancellationToken);
      QueryEngine engine = new(opened.BuildCatalog(overlay), opened, [Sqlite.SqliteSourceProvider.Instance, DuckDb.DuckDbSourceProvider.Instance]);
      return new Session(opened, engine, values);
   }

   public ValueTask DisposeAsync() => Sources.DisposeAsync();
}

/// <summary>The <c>gdq</c> command line: run, sql, schema and repl.</summary>
internal static class GdqApp
{
   public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
   {
      Option<string[]> sources = new("--source", "-s")
      {
         Description = "A source as alias=kind:target, e.g. shop=sqlite:shop.db. Kinds: sqlite, duckdb. A .sql target is run into a new in-memory database; files open read-only.",
         Required = true,
      };
      Option<FileInfo?> overlay = new("--overlay") { Description = "A catalog overlay (JSON): relations across sources, virtual entities, renames." };
      Option<string[]> parameters = new("--param", "-p") { Description = "A query parameter as name=value, used as $name." };
      Option<OutputFormat> format = new("--format") { Description = "How to print rows: table, csv or json.", DefaultValueFactory = _ => OutputFormat.Table };
      Option<bool> sql = new("--sql") { Description = "Print the SQL each source runs." };
      Option<int> maxRows = new("--max-rows") { Description = "Print at most this many rows.", DefaultValueFactory = _ => 1000 };
      Option<FileInfo?> file = new("--file", "-f") { Description = "Read the query from a file." };
      Argument<string?> query = new("query") { Description = "The query.", Arity = ArgumentArity.ZeroOrOne };
      Argument<string?> filter = new("filter") { Description = "Only entities whose names contain this.", Arity = ArgumentArity.ZeroOrOne };

      Command run = new("run", "Run a query and print its rows.") { sources, overlay, parameters, format, sql, maxRows, file, query };
      Command explain = new("sql", "Print a query's plan, lineage and SQL without running it.") { sources, overlay, parameters, file, query };
      Command schema = new("schema", "List the entities of the catalog with their columns and navigations.") { sources, overlay, filter };
      Command repl = new("repl", "Run queries interactively.") { sources, overlay, parameters, format, maxRows };
      RootCommand root = new("gdq: query SQLite and DuckDB databases with the GalaxyData query language.") { run, explain, schema, repl };

      run.SetAction((parse, token) => Guarded(error, async () =>
      {
         string text = await QueryText(parse.GetValue(query), parse.GetValue(file), token);
         await using Session session = await Session.OpenAsync(parse.GetValue(sources)!, parse.GetValue(overlay), parse.GetValue(parameters) ?? [], token);
         Settings settings = new() { Format = parse.GetValue(format), ShowSql = parse.GetValue(sql), MaxRows = parse.GetValue(maxRows) };
         return await ExecuteAsync(session, text, settings, output, error, token);
      }));
      explain.SetAction((parse, token) => Guarded(error, async () =>
      {
         string text = await QueryText(parse.GetValue(query), parse.GetValue(file), token);
         await using Session session = await Session.OpenAsync(parse.GetValue(sources)!, parse.GetValue(overlay), parse.GetValue(parameters) ?? [], token);
         return Explain(session, text, output, error);
      }));
      schema.SetAction((parse, token) => Guarded(error, async () =>
      {
         await using Session session = await Session.OpenAsync(parse.GetValue(sources)!, parse.GetValue(overlay), [], token);
         Output.Catalog(output, session.Engine.Catalog, parse.GetValue(filter));
         return 0;
      }));
      repl.SetAction((parse, token) => Guarded(error, async () =>
      {
         await using Session session = await Session.OpenAsync(parse.GetValue(sources)!, parse.GetValue(overlay), parse.GetValue(parameters) ?? [], token);
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
      if (settings.ShowPlan) { PrintPlan(output, prepared); }
      if (settings.ShowSql) { Output.Fragments(output, prepared); }
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

   public static int Explain(Session session, string text, TextWriter output, TextWriter error)
   {
      PreparedQuery prepared = session.Engine.Prepare(text, session.Parameters);
      Output.Diagnostics(error, text, prepared.Diagnostics);
      if (!prepared.Success) { return 1; }
      PrintPlan(output, prepared);
      Output.Fragments(output, prepared);
      return 0;
   }

   private static void PrintPlan(TextWriter output, PreparedQuery prepared)
   {
      output.WriteLine("-- plan");
      foreach (string line in PlanPrinter.Print(prepared.Plan!.Root).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
      {
         output.WriteLine("-- " + line);
      }
      output.WriteLine("-- lineage");
      Output.Lineage(output, prepared.Schema!);
   }
}

/// <summary>A mistake in how gdq was called.</summary>
internal sealed class UsageException(string message) : Exception(message);
