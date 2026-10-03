using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
