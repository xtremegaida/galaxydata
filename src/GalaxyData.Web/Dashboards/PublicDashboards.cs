using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Dashboards;

/// <summary>A public dashboard as its link finds it: its published copy, and the sites it names that may frame it.</summary>
public sealed record PublicDashboard(int Id, string Name, string? Description, DashboardDefinition Definition, string Hash, IReadOnlyList<string> Origins);

/// <summary>
/// A public dashboard as anyone with its link sees it: its name, and its published copy without what is the
/// server's alone: no sources, links, conditions, fields or hidden filters (labels and display settings stay, as
/// the widgets show them). A refresh interval is never shorter than the time public answers are kept.
/// </summary>
public sealed record PublicDashboardDto(string Name, string? Description, DashboardDefinition Definition);

/// <summary>
/// Dashboards by their public links: found while public dashboards are allowed, something is published, and whoever
/// made it public still may (enabled, a data manager or an administrator); otherwise there is no such dashboard.
/// Also how many of a dashboard's queries run at once, for all its viewers (answers kept don't count), and which
/// sites may frame it.
/// </summary>
public sealed partial class PublicDashboards(IOptions<GalaxyDataOptions> options) : IDisposable
{
   /// <summary>How long a viewer's request waits for one of the dashboard's queries to end before it is refused (429).</summary>
   public static readonly TimeSpan GateWait = TimeSpan.FromSeconds(10);

   private readonly MemoryCache definitions = new(new MemoryCacheOptions { SizeLimit = 500 });
   private readonly ConcurrentDictionary<int, SemaphoreSlim> gates = new();

   private DashboardSettings Settings => options.Value.Dashboards;

   /// <summary>Tokens as they are made: 22 base64url characters.</summary>
   [GeneratedRegex("^[A-Za-z0-9_-]{22}$")]
   private static partial Regex TokenPattern();

   public async Task<PublicDashboard?> FindAsync(MetadataDb db, string? token, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(db);
      if (!Settings.AllowPublic || token == null || !TokenPattern().IsMatch(token)) { return null; }
      var row = await db.Dashboards.AsNoTracking()
         .Where(d => d.PublicToken == token && d.PublishedJson != null && d.PublishedHash != null)
         .Select(d => new { d.Id, d.Name, d.Description, d.PublishedJson, d.PublishedHash, d.EmbedOrigins, d.PublicEnabledById })
         .SingleOrDefaultAsync(cancellationToken);
      if (row?.PublicEnabledById is not int by) { return null; }
      bool may = await db.Users.AnyAsync(u => u.Id == by && !u.IsDisabled && (u.Role == UserRole.DataManager || u.Role == UserRole.Admin), cancellationToken);
      if (!may) { return null; }
      DashboardDefinition definition = definitions.GetOrCreate(row.PublishedHash!, entry =>
      {
         entry.Size = 1;
         entry.SlidingExpiration = TimeSpan.FromMinutes(10);
         return DefinitionJson.Read(row.PublishedJson!);
      })!;
      return new PublicDashboard(row.Id, row.Name, row.Description, definition, row.PublishedHash!, row.EmbedOrigins);
   }

   public PublicDashboardDto View(PublicDashboard dashboard)
   {
      ArgumentNullException.ThrowIfNull(dashboard);
      DashboardDefinition definition = dashboard.Definition;
      int floor = (int)Math.Ceiling(Settings.PublicCacheDuration.TotalSeconds);
      RefreshPolicy refresh = definition.Refresh is { Mode: RefreshMode.Interval, Seconds: { } seconds } && seconds < floor ? definition.Refresh with { Seconds = floor } : definition.Refresh;
      DashboardDefinition shown = definition with
      {
         Sources = [],
         Links = [],
         Filters = [.. definition.Filters.Where(f => f.Visible).Select(f => f with { Field = new SourceField(string.Empty, [], string.Empty), Except = [] })],
         Widgets = [.. definition.Widgets.Select(w => w with { Config = Strip(w.Config) })],
         Refresh = refresh,
      };
      return new PublicDashboardDto(dashboard.Name, dashboard.Description, shown);
   }

   /// <summary>Waits for one of the dashboard's queries to end, if it has as many running as it may; 429 after <see cref="GateWait"/>.</summary>
   public async Task<IAsyncDisposable> GateAsync(int dashboard, CancellationToken cancellationToken)
   {
      SemaphoreSlim gate = gates.GetOrAdd(dashboard, _ => new SemaphoreSlim(Settings.PublicQueriesPerDashboard, Settings.PublicQueriesPerDashboard));
      if (!await gate.WaitAsync(GateWait, cancellationToken))
      {
         throw new ApiException(StatusCodes.Status429TooManyRequests, ProblemCodes.TooManyRequests, "The dashboard is busy", "Try again in a moment");
      }
      return new Release(gate);
   }

   /// <summary>
   /// The sources of <c>frame-ancestors</c> for a dashboard's page: the sites it names that the application allows
   /// (<see cref="DashboardSettings.EmbedFrameAncestors"/>), with the application itself; the application's when it
   /// names none, or it isn't found (so its page may say it isn't available, which has nothing to click).
   /// </summary>
   public string FrameAncestors(PublicDashboard? dashboard)
   {
      IReadOnlyList<string>? allowed = AllowedAncestors(Settings.EmbedFrameAncestors);
      if (dashboard == null || dashboard.Origins.Count == 0) { return allowed == null ? "*" : string.Join(" ", ["'self'", .. allowed]); }
      IEnumerable<string> named = dashboard.Origins.Where(o => allowed == null || allowed.Any(a => Covers(a, o)));
      return string.Join(" ", ["'self'", .. named]);
   }

   /// <summary>The origins a setting allows; null for any (<c>*</c>).</summary>
   public static IReadOnlyList<string>? AllowedAncestors(string setting)
   {
      ArgumentNullException.ThrowIfNull(setting);
      string trimmed = setting.Trim();
      if (trimmed == "*") { return null; }
      return [.. trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(o => EmbedOrigin.Normalize(o) ?? throw new FormatException($"'{o}' isn't a site such as https://example.com"))];
   }

   /// <summary>Whether an allowed origin covers one named: the same, or (<c>https://*.example.com</c>) one of its subdomains, by the same scheme and port.</summary>
   private static bool Covers(string allowed, string origin)
   {
      if (string.Equals(allowed, origin, StringComparison.Ordinal)) { return true; }
      int wild = allowed.IndexOf("://*.", StringComparison.Ordinal);
      if (wild < 0) { return false; }
      string scheme = allowed[..(wild + 3)];
      string rest = allowed[(wild + 4)..];
      string tail = origin.StartsWith(scheme, StringComparison.Ordinal) ? origin[scheme.Length..].Replace("*.", string.Empty, StringComparison.Ordinal) : string.Empty;
      return tail.EndsWith(rest, StringComparison.Ordinal) && tail.Length > rest.Length;
   }

   public void Dispose()
   {
      definitions.Dispose();
      foreach (SemaphoreSlim gate in gates.Values) { gate.Dispose(); }
   }

   /// <summary>A widget's config without its source, conditions and fields: what it shows stays.</summary>
   private static WidgetConfig Strip(WidgetConfig config)
   {
      static FieldRef Blank() => new([], string.Empty);
      static Dimension Plain(Dimension dimension) => dimension with { Field = Blank() };
      static Measure Bare(Measure measure) => measure with { Field = measure.Field == null ? null : Blank() };
      return config switch
      {
         PieConfig pie => pie with { Source = string.Empty, Conditions = [], Dimension = Plain(pie.Dimension), Measure = Bare(pie.Measure) },
         BarConfig bar => bar with
         {
            Source = string.Empty, Conditions = [], Dimension = Plain(bar.Dimension), Series = bar.Series == null ? null : Plain(bar.Series), Measures = [.. bar.Measures.Select(Bare)],
         },
         LineConfig line => line with
         {
            Source = string.Empty, Conditions = [], Dimension = Plain(line.Dimension), Series = line.Series == null ? null : Plain(line.Series), Measures = [.. line.Measures.Select(Bare)],
         },
         // A raw table's rows are chosen by their key, which a public dashboard doesn't send.
         TableConfig table => table with
         {
            Source = string.Empty, Conditions = [], Emits = table.Emits && table.Mode == TableMode.Grouped, Dimensions = [.. table.Dimensions.Select(Plain)],
            Measures = [.. table.Measures.Select(Bare)], Columns = [.. table.Columns.Select(c => c with { Field = Blank() })],
         },
         _ => config,
      };
   }

   private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
   {
      private int released;

      public ValueTask DisposeAsync()
      {
         if (Interlocked.Exchange(ref released, 1) == 0) { gate.Release(); }
         return ValueTask.CompletedTask;
      }
   }
}
