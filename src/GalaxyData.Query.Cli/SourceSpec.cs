using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Connectors;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Language;

namespace GalaxyData.Query.Cli;

/// <summary>
/// A source given on the command line as <c>alias=kind:target</c>, of a kind a connector takes on the command line.
/// What the target is depends on the kind (<see cref="CommandLineTargets"/>): a database file (opened read-only,
/// unless written), a <c>.sql</c> script (run into a fresh in-memory database), <c>:memory:</c>, or a connection
/// string (anything with an <c>=</c> in it); a connection string; or a folder.
/// </summary>
internal sealed record SourceSpec(string Alias, string Kind, string Target)
{
   /// <summary>The connectors the command line takes sources of, as it lists them: files, then connection strings, then folders.</summary>
   public static IReadOnlyList<Connector> Kinds(ConnectorSet connectors) =>
      connectors.All.Where(c => c.CommandLine != null).OrderBy(c => c.CommandLine!.Targets).ToList();

   /// <summary>The kinds for <c>--source</c>'s help: <c>sqlite, duckdb, postgres and sqlserver (a connection string), excel (a folder of .xlsx workbooks)</c>.</summary>
   public static string Describe(ConnectorSet connectors) =>
      string.Join(", ", Kinds(connectors).GroupBy(c => c.CommandLine!.Target).Select(g =>
      {
         List<string> ids = g.Select(c => c.Id).ToList();
         string listed = ids.Count == 1 ? ids[0] : string.Join(", ", ids.Take(ids.Count - 1)) + " and " + ids[^1];
         return g.Key == null ? listed : $"{listed} ({g.Key})";
      }));

   public static SourceSpec Parse(string text, ConnectorSet connectors)
   {
      ArgumentNullException.ThrowIfNull(text);
      int equals = text.IndexOf('=', StringComparison.Ordinal);
      int colon = equals < 0 ? -1 : text.IndexOf(':', equals + 1);
      if (equals <= 0 || colon < 0)
      {
         throw new FormatException($"'{text}' is not a source; write alias=kind:target, e.g. shop=sqlite:shop.db");
      }
      string alias = text[..equals].Trim();
      string kind = text[(equals + 1)..colon].Trim().ToLowerInvariant();
      string target = text[(colon + 1)..].Trim();
      if (!QueryText.IsBareIdentifier(alias)) { throw new FormatException($"'{alias}' can't be a source alias; use letters, digits and underscores"); }
      IReadOnlyList<Connector> kinds = Kinds(connectors);
      Connector connector = kinds.FirstOrDefault(c => c.Id == kind)
         ?? throw new FormatException($"'{kind}' is not a source kind; use {string.Join(", ", kinds.Take(kinds.Count - 1).Select(c => c.Id))} or {kinds[^1].Id}");
      if (target.Length == 0) { throw new FormatException($"The source '{alias}' needs a target after '{kind}:'"); }
      if (connector.CommandLine!.Targets == CommandLineTargets.ConnectionString && !target.Contains('=', StringComparison.Ordinal))
      {
         throw new FormatException($"The source '{alias}' needs a connection string after '{kind}:', e.g. {connector.CommandLine.Example}");
      }
      return new SourceSpec(alias, kind, target);
   }
}

/// <summary>
/// The command line's sources, opened by their connectors: each database keeps a first connection open, which
/// in-memory databases need; sources a connector opens itself (folders of workbooks) are attached to it. Sources are
/// read-only, and their files opened so, but those named writable.
/// </summary>
internal sealed class CliSources(ConnectorSet connectors, IReadOnlySet<string> writable) : IConnectionFactory, IAsyncDisposable
{
   private readonly Dictionary<string, OpenedSource> opened = new(StringComparer.Ordinal);
   private readonly CatalogBuilder builder = new();

   /// <summary>What the sources' schemas left out (workbooks that can't be read).</summary>
   public List<string> Warnings { get; } = [];

   public static async Task<CliSources> OpenAsync(IReadOnlyList<SourceSpec> specs, ConnectorSet connectors, IReadOnlySet<string> writable,
                                                  CancellationToken cancellationToken)
   {
      if (writable.FirstOrDefault(w => !specs.Any(s => s.Alias == w)) is { } unknown) { throw new FormatException($"'{unknown}' is no source, so it can't be written"); }
      CliSources sources = new(connectors, writable);
      try
      {
         foreach (SourceSpec spec in specs) { await sources.AddAsync(spec, cancellationToken); }
         return sources;
      }
      catch
      {
         await sources.DisposeAsync();
         throw;
      }
   }

   public QueryCatalog BuildCatalog(CatalogOverlay overlay) => builder.WithOverlay(overlay).Build();

   private async Task AddAsync(SourceSpec spec, CancellationToken cancellationToken)
   {
      if (opened.ContainsKey(spec.Alias)) { throw new FormatException($"The source alias '{spec.Alias}' is given twice"); }
      Connector connector = connectors.Find(spec.Kind) ?? throw new FormatException($"'{spec.Kind}' is not a source kind");
      OpenedSource source = await connector.OpenAsync(new CommandLineSource(spec.Alias, spec.Kind, spec.Target, writable.Contains(spec.Alias)), cancellationToken);
      opened.Add(spec.Alias, source);
      builder.AddSource(source.Info, source.Schema);
      foreach (string warning in source.Warnings) { Warnings.Add($"{spec.Alias}: {warning}"); }
   }

   public async ValueTask<DbConnection> OpenAsync(SourceInfo source, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      Func<DbConnection> open = opened[source.Alias].Open ?? throw new InvalidOperationException($"{source.Alias} is opened by its provider");
      DbConnection connection = open();
      if (connection.State != System.Data.ConnectionState.Open) { await connection.OpenAsync(cancellationToken); }
      return connection;
   }

   public async ValueTask DisposeAsync()
   {
      foreach (OpenedSource source in opened.Values) { await source.DisposeAsync(); }
      opened.Clear();
   }
}
