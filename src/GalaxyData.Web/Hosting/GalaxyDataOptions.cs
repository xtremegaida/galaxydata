using System.ComponentModel.DataAnnotations;

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
   public MergeSettings Merge { get; set; } = new();
}

/// <summary>The merge engine's settings; see <c>DuckDbMergeOptions</c>. Directories are relative to the data directory unless absolute.</summary>
public sealed class MergeSettings
{
   /// <summary>How much memory DuckDB may use (<c>2GB</c>, <c>512MB</c>); null for DuckDB's default, 80% of RAM.</summary>
   public string? MemoryLimit { get; set; }

   /// <summary>The threads DuckDB may use; null for one per core.</summary>
   public int? Threads { get; set; }

   /// <summary>Where data that doesn't fit in memory goes; null for a directory of its own under the system's temp directory.</summary>
   public string? TempDirectory { get; set; }

   /// <summary>Where DuckDB looks for extensions; null for its default. The application needs none.</summary>
   public string? ExtensionDirectory { get; set; }

   /// <summary>Whether DuckDB may download an extension a statement needs; off by default.</summary>
   public bool DownloadExtensions { get; set; }
}
