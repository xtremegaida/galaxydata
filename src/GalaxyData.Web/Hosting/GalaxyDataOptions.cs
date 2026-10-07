using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Hosting;

/// <summary>The application's settings: the <c>GalaxyData</c> section of its configuration.</summary>
public sealed class GalaxyDataOptions
{
   public const string Section = "GalaxyData";

   /// <summary>
   /// Where the application keeps what it stores (its database, the keys that protect stored secrets), relative to
   /// the content root unless absolute; made when it's missing. One application at a time may use it.
   /// </summary>
   [Required]
   public string DataDirectory { get; set; } = "data";

   /// <summary>The merge engine, which runs the parts of queries that combine sources.</summary>
   [ValidateObjectMembers]
   public MergeSettings Merge { get; set; } = new();

   /// <summary>The first administrator, and getting back in when no administrator can.</summary>
   [ValidateObjectMembers]
   public BootstrapSettings Bootstrap { get; set; } = new();

   /// <summary>Signing in: passwords, lockout and sessions.</summary>
   [ValidateObjectMembers]
   public AuthSettings Auth { get; set; } = new();

   /// <summary>Connections to sources.</summary>
   [ValidateObjectMembers]
   public ConnectionSettings Connections { get; set; } = new();

   /// <summary>Queries: how long they may run, how much they may fetch, and grids' pages.</summary>
   [ValidateObjectMembers]
   public QuerySettings Query { get; set; } = new();

   /// <summary>Changes to rows: how many a user may have pending, and how long a preview may be committed.</summary>
   [ValidateObjectMembers]
   public ChangeSettings Changes { get; set; } = new();

   /// <summary>What answers tell browsers: the client's content security policy, HSTS and HTTPS.</summary>
   [ValidateObjectMembers]
   public SecuritySettings Security { get; set; } = new();

   /// <summary>A reverse proxy in front of the application, trusted to say who the client is.</summary>
   [ValidateObjectMembers]
   public ProxySettings Proxy { get; set; } = new();

   /// <summary>How much each user may ask of the application.</summary>
   [ValidateObjectMembers]
   public RateLimitSettings RateLimits { get; set; } = new();

   /// <summary>Dashboards: their sizes, revisions, refreshing, caching, and public ones.</summary>
   [ValidateObjectMembers]
   public DashboardSettings Dashboards { get; set; } = new();
}

/// <summary><c>*</c>, or sites written as browsers write origins (<c>https://example.com</c>, <c>https://*.example.com</c>), apart by spaces.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FrameAncestorsAttribute : ValidationAttribute
{
   protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
   {
      ArgumentNullException.ThrowIfNull(validationContext);
      if (value is not string text) { return ValidationResult.Success; }
      try
      {
         _ = Dashboards.PublicDashboards.AllowedAncestors(text);
         return ValidationResult.Success;
      }
      catch (FormatException e)
      {
         string member = validationContext.MemberName ?? validationContext.DisplayName;
         return new ValidationResult($"{member}: {e.Message}, or *", [member]);
      }
   }
}

public sealed class DashboardSettings
{
   /// <summary>Whether dashboards may be public (viewed by anyone with their link, embedded in other sites); off, no public link works.</summary>
   public bool AllowPublic { get; set; } = true;

   /// <summary>
   /// The sites that may frame public dashboards, as a content security policy's <c>frame-ancestors</c> sources
   /// (<c>https://example.com</c>, <c>https://*.example.com</c>), or <c>*</c> for any; a dashboard may name fewer.
   /// </summary>
   [Required, FrameAncestors]
   public string EmbedFrameAncestors { get; set; } = "*";

   /// <summary>The revisions kept for each dashboard, the newest; the published one is kept whatever its age.</summary>
   [Range(1, 1000)]
   public int KeepRevisions { get; set; } = 20;

   [Range(1, 500)]
   public int MaxWidgets { get; set; } = 50;

   [Range(1, 200)]
   public int MaxSources { get; set; } = 20;

   [Range(0, 200)]
   public int MaxFilters { get; set; } = 30;

   /// <summary>The longest a dashboard's definition may be, as JSON, in characters.</summary>
   [Range(1024, 16 * 1024 * 1024)]
   public int MaxDefinitionLength { get; set; } = 256 * 1024;

   /// <summary>The most rows (points, bars, slices) a chart may show.</summary>
   [Range(1, 1_000_000)]
   public int MaxChartRows { get; set; } = 10_000;

   /// <summary>The most slices a selection of a widget's may hold, signed in and in public dashboards.</summary>
   [Range(1, 10_000)]
   public int MaxSelectionKeys { get; set; } = 50;

   [Range(1, 10_000)]
   public int MaxPublicSelectionKeys { get; set; } = 25;

   /// <summary>The most values a condition may list, signed in and in public dashboards.</summary>
   [Range(1, 10_000)]
   public int MaxFilterValues { get; set; } = 500;

   [Range(1, 10_000)]
   public int MaxPublicFilterValues { get; set; } = 100;

   /// <summary>The shortest a dashboard's refresh interval may be.</summary>
   [Range(1, 86_400)]
   public int MinRefreshSeconds { get; set; } = 30;

   /// <summary>How long a widget's query may run, signed in and in public dashboards.</summary>
   [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
   public TimeSpan WidgetTimeout { get; set; } = TimeSpan.FromSeconds(30);

   [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
   public TimeSpan PublicWidgetTimeout { get; set; } = TimeSpan.FromSeconds(15);

   /// <summary>How long a widget's rows are kept for the next to ask the same, signed in and in public dashboards.</summary>
   [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
   public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);

   [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
   public TimeSpan PublicCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

   /// <summary>The most widgets' rows kept at once.</summary>
   [Range(1, 1_000_000)]
   public int CacheSize { get; set; } = 2000;

   /// <summary>The queries a public dashboard may have running at once, for all its viewers (rows kept don't count).</summary>
   [Range(1, 1000)]
   public int PublicQueriesPerDashboard { get; set; } = 4;
}

public sealed class SecuritySettings
{
   /// <summary>
   /// What the client's pages may load: their own scripts, styles (and inline ones, which the grid and editor set),
   /// images and fonts (and data URLs), workers (and blob URLs, as the editor makes them), and calls to the API alone.
   /// </summary>
   public const string DefaultContentSecurityPolicy =
      "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; " +
      "worker-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

   /// <summary>The content security policy of the client's pages and files; empty for none. The API's answers have one of their own, which loads nothing.</summary>
   [RegularExpression("^[^\\x00-\\x1F\\x7F]*$", ErrorMessage = "Security:ContentSecurityPolicy is a header's value: it has no line breaks or other control characters")]
   public string ContentSecurityPolicy { get; set; } = DefaultContentSecurityPolicy;

   /// <summary>Whether answers over HTTPS tell browsers to use HTTPS alone for the host (Strict-Transport-Security), outside development.</summary>
   public bool Hsts { get; set; } = true;

   /// <summary>How long browsers keep to HTTPS for the host.</summary>
   [Range(typeof(TimeSpan), "00:00:00", "730.00:00:00")]
   public TimeSpan HstsMaxAge { get; set; } = TimeSpan.FromDays(180);

   /// <summary>Whether requests over HTTP are redirected to HTTPS.</summary>
   public bool RequireHttps { get; set; }

   /// <summary>The port to redirect to HTTPS on; null for the one the server listens on for HTTPS.</summary>
   [Range(1, 65535)]
   public int? HttpsPort { get; set; }
}

public sealed class ProxySettings
{
   /// <summary>
   /// Whether a reverse proxy passes requests on, saying who the client is and how it connected
   /// (<c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c>): believed only of the proxies trusted.
   /// </summary>
   public bool Enabled { get; set; }

   /// <summary>The trusted proxies' addresses. With no proxies or networks given, only one on the same machine is trusted.</summary>
   [IpAddresses]
   public List<string>? KnownProxies { get; set; }

   /// <summary>The networks trusted proxies are in, in CIDR form (<c>10.0.0.0/8</c>).</summary>
   [IpAddresses(Networks = true)]
   public List<string>? KnownNetworks { get; set; }

   /// <summary>How many proxies in a row are believed, from the one nearest.</summary>
   [Range(1, 10)]
   public int ForwardLimit { get; set; } = 1;
}

public sealed class RateLimitSettings
{
   /// <summary>Requests to the API a user (or, signed out, an address) may make a minute, in bursts of up to as many; 0 for no limit.</summary>
   [Range(0, 1_000_000)]
   public int RequestsPerMinute { get; set; } = 600;

   /// <summary>Requests that run queries or reach sources a user may have running at once: browsing, queries, previews and commits, trying connections and overlay items.</summary>
   [Range(1, 1000)]
   public int ConcurrentQueries { get; set; } = 4;

   /// <summary>Such requests a user may have waiting for one of those to end; more are refused (429).</summary>
   [Range(0, 10_000)]
   public int QueuedQueries { get; set; } = 16;
}

/// <summary>
/// Each item is an IP address, or with <see cref="Networks"/> a network in CIDR form (<c>10.0.0.0/8</c>), written in
/// full: <c>10</c> and <c>10.1</c>, which .NET reads as <c>0.0.0.10</c> and <c>10.0.0.1</c>, aren't.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IpAddressesAttribute : ValidationAttribute
{
   public bool Networks { get; set; }

   protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
   {
      ArgumentNullException.ThrowIfNull(validationContext);
      if (value is not IEnumerable<string> items) { return ValidationResult.Success; }
      foreach (string item in items)
      {
         int slash = item.IndexOf('/', StringComparison.Ordinal);
         string address = Networks && slash >= 0 ? item[..slash] : item;
         bool full = address.Contains(':', StringComparison.Ordinal) || address.Split('.') is [_, _, _, _];
         if (!full || (Networks ? !System.Net.IPNetwork.TryParse(item, out _) : !IPAddress.TryParse(item, out _)))
         {
            string member = validationContext.MemberName ?? validationContext.DisplayName;
            return new ValidationResult($"{member}: '{item}' isn't {(Networks ? "a network in CIDR form, such as 10.0.0.0/8" : "an IP address")}", [member]);
         }
      }
      return ValidationResult.Success;
   }
}

public sealed class ChangeSettings
{
   /// <summary>The most changes a user may have pending: each is a statement when they are committed.</summary>
   [Range(1, 1_000_000)]
   public int MaxChanges { get; set; } = 10_000;

   /// <summary>How long after a preview its plan may be committed; a commit after that previews again.</summary>
   [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
   public TimeSpan PlanLifetime { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class QuerySettings
{
   /// <summary>How long a query may run, reading its rows included.</summary>
   [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
   public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(1);

   /// <summary>How long counting a grid's rows may take; a grid shows its rows without the count after that.</summary>
   [Range(typeof(TimeSpan), "00:00:00.001", "00:10:00")]
   public TimeSpan CountTimeout { get; set; } = TimeSpan.FromSeconds(3);

   /// <summary>The most rows a query may fetch from its sources to combine them.</summary>
   [Range(1, int.MaxValue)]
   public int MaxFetchedRows { get; set; } = 10_000_000;

   /// <summary>The most rows a page of a grid may have.</summary>
   [Range(1, 100_000)]
   public int MaxPageSize { get; set; } = 1000;
}

public sealed class ConnectionSettings
{
   /// <summary>The folder files and folders are allowed in when no roots are given: <c>files</c> in the data directory.</summary>
   public const string DefaultFileRoot = "files";

   /// <summary>
   /// The folders a connection's files and folders must be in (database files, folders of workbooks, certificates),
   /// relative to the data directory unless absolute; null for <see cref="DefaultFileRoot"/>. Configured lists
   /// replace the default.
   /// </summary>
   public List<string>? AllowedFileRoots { get; set; }

   /// <summary>How long testing a connection may take.</summary>
   [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
   public TimeSpan TestTimeout { get; set; } = TimeSpan.FromSeconds(10);

   /// <summary>How long reading a connection's schema may take.</summary>
   [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
   public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromMinutes(10);

   /// <summary>How many schemas are read at once.</summary>
   [Range(1, 16)]
   public int ParallelRefreshes { get; set; } = 2;
}

/// <summary>The merge engine's settings; see <c>DuckDbMergeOptions</c>. Directories are relative to the data directory unless absolute.</summary>
public sealed class MergeSettings
{
   /// <summary>How much memory DuckDB may use (<c>2GB</c>, <c>512MB</c>); null for DuckDB's default, 80% of RAM.</summary>
   public string? MemoryLimit { get; set; }

   /// <summary>The threads DuckDB may use; null for one per core.</summary>
   [Range(1, 1024)]
   public int? Threads { get; set; }

   /// <summary>Where data that doesn't fit in memory goes; null for a directory of its own under the system's temp directory.</summary>
   public string? TempDirectory { get; set; }

   /// <summary>Where DuckDB looks for extensions; null for its default. The application needs none.</summary>
   public string? ExtensionDirectory { get; set; }

   /// <summary>Whether DuckDB may download an extension a statement needs; off by default.</summary>
   public bool DownloadExtensions { get; set; }
}

/// <summary>
/// The first administrator: made with <see cref="AdminPassword"/> when there are no users (the application doesn't
/// start without one then). With <see cref="ResetAdminPassword"/>, the administrator's password is set to it again,
/// once for each password given, to get back in.
/// </summary>
public sealed class BootstrapSettings
{
   [Required]
   [RegularExpression(UserNames.Pattern)]
   public string AdminUserName { get; set; } = "admin";

   public string? AdminPassword { get; set; }

   /// <summary>Whether the first administrator (or one whose password is reset) must change the password at the first sign-in.</summary>
   public bool RequirePasswordChange { get; set; } = true;

   /// <summary>
   /// Sets <see cref="AdminUserName"/>'s password to <see cref="AdminPassword"/>, to be changed at the next sign-in;
   /// makes the user an enabled administrator (made anew if it was deleted). Done once for each password: remove the
   /// setting once in.
   /// </summary>
   public bool ResetAdminPassword { get; set; }
}

public sealed class AuthSettings
{
   [Range(8, 128)]
   public int MinimumPasswordLength { get; set; } = 12;

   /// <summary>Failed sign-ins in a row that lock a user out for <see cref="LockoutDuration"/>.</summary>
   [Range(1, 100)]
   public int MaxFailedSignIns { get; set; } = 5;

   [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
   public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

   /// <summary>How long a session lasts without requests; each request extends it.</summary>
   [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00")]
   public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromHours(8);

   /// <summary>Sign-in attempts a client address may make a minute.</summary>
   [Range(1, 10_000)]
   public int SignInsPerMinute { get; set; } = 10;
}

/// <summary>Checks the settings, nested ones too, when the application starts.</summary>
[OptionsValidator]
internal sealed partial class GalaxyDataOptionsValidator : IValidateOptions<GalaxyDataOptions>;

/// <summary>What a user name may be: letters, digits and <c>. _ @ -</c>, starting with a letter or digit, at most 64 characters.</summary>
public static partial class UserNames
{
   public const string Pattern = "^[A-Za-z0-9][A-Za-z0-9._@-]{0,63}$";

   public static bool IsValid(string name) => Regex().IsMatch(name);

   /// <summary>A name as it may be logged: one that can't be a user's (with line breaks, say) isn't.</summary>
   public static string ForLog(string name) => IsValid(name) ? name : "(not a valid user name)";

   [GeneratedRegex(Pattern)]
   private static partial Regex Regex();
}
