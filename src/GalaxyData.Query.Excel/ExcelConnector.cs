using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Connectors;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;

namespace GalaxyData.Query.Excel;

/// <summary>
/// Folders of workbooks, whose sheets the merge engine's database keeps: the merge engine must be DuckDB's. The
/// connector owns its provider, and registers each source's folder with it under the source's alias.
/// </summary>
public sealed class ExcelConnector : Connector, IAttachedSources
{
   private static readonly IReadOnlyDictionary<string, string> NoOptions = new Dictionary<string, string>();
   private static long schemas;

   private readonly ExcelSourceProvider provider;
   private readonly Lock gate = new();
   private readonly Dictionary<string, ExcelFolderOptions> attached = new(StringComparer.Ordinal);

   public ExcelConnector(ConnectorContext context)
   {
      ArgumentNullException.ThrowIfNull(context);
      DuckDbMergeEngine merge = context.Merge as DuckDbMergeEngine
         ?? throw new ArgumentException("Folders of workbooks keep their sheets in the merge engine's database, which must be DuckDB's (a DuckDbMergeEngine)", nameof(context));
      provider = new ExcelSourceProvider(merge);
   }

   public override SourceProvider Provider => provider;

   public override ConnectionKind Kind { get; } = new ExcelKind();

   public override IAttachedSources Attached => this;

   public override CommandLineHelp CommandLine { get; } = new(CommandLineTargets.Folder, "a folder of .xlsx workbooks");

   public SourceInfo Attach(string alias, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> options)
   {
      ArgumentException.ThrowIfNullOrEmpty(alias);
      ExcelFolderOptions folder = ExcelKind.FolderOptions(settings, options);
      lock (gate)
      {
         if (!attached.TryGetValue(alias, out ExcelFolderOptions? registered) || !Same(registered, folder))
         {
            provider.AddFolder(alias, folder);
            attached[alias] = folder;
         }
      }
      return provider.Source(alias);
   }

   public bool Detach(string alias)
   {
      lock (gate)
      {
         attached.Remove(alias);
         return provider.RemoveFolder(alias);
      }
   }

   public async Task<SourceSchema> ReadSchemaAsync(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> options,
                                                   CancellationToken cancellationToken)
   {
      // A name no alias has (aliases have no '-'), so a source's own registration stays as it is.
      string name = "schema-" + Interlocked.Increment(ref schemas).ToString(CultureInfo.InvariantCulture);
      provider.AddFolder(name, ExcelKind.FolderOptions(settings, options));
      try
      {
         return await provider.IntrospectAsync(name, IntrospectionOptions.Default, cancellationToken).ConfigureAwait(false);
      }
      finally
      {
         provider.RemoveFolder(name);
      }
   }

   public override async ValueTask<OpenedSource> OpenAsync(CommandLineSource source, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      if (source.Writable) { throw new FormatException($"'{source.Alias}' is a folder of workbooks, which can't be written"); }
      if (!Directory.Exists(source.Target)) { throw new DirectoryNotFoundException($"The folder of workbooks for '{source.Alias}' doesn't exist: {source.Target}"); }
      Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase) { [ExcelKind.Folder] = Path.GetFullPath(source.Target) };
      SourceInfo info = Attach(source.Alias, settings, NoOptions);
      SourceSchema schema = await provider.IntrospectAsync(source.Alias, IntrospectionOptions.Default, cancellationToken).ConfigureAwait(false);
      return new OpenedSource(info, schema) { Warnings = schema.Warnings ?? [] };
   }

   private static bool Same(ExcelFolderOptions a, ExcelFolderOptions b) =>
      string.Equals(a.Path, b.Path, StringComparison.Ordinal) && a.HeaderRow == b.HeaderRow && a.AllText == b.AllText && a.IncludeHiddenSheets == b.IncludeHiddenSheets;

   /// <summary>Drops the sheets loaded from every folder.</summary>
   protected override void Dispose(bool disposing)
   {
      if (disposing) { provider.Dispose(); }
   }
}

/// <summary>A folder of workbooks: no connection string, just the folder, and always read-only.</summary>
public sealed class ExcelKind : ConnectionKind
{
   public const string Folder = "Folder";

   public const string HeaderRowOption = "headerRow";

   public const string AllTextOption = "allText";

   public const string IncludeHiddenSheetsOption = "includeHiddenSheets";

   public override string Id => ExcelSourceProvider.Kind;

   public override string DisplayName => "Excel folder";

   public override string Icon => "table_view";

   public override bool SupportsRaw => false;

   public override bool AlwaysReadOnly => true;

   public override IReadOnlyList<GroupDto> Groups { get; } = [new("connection", "Folder", false)];

   public override IReadOnlyList<FieldDto> Fields { get; } =
   [
      new(Folder, "Folder of workbooks", FieldType.FolderPath)
      {
         Required = true,
         Group = "connection",
         Help = "Its own .xlsx workbooks (not those of folders in it) are the source's schemas, their sheets its tables",
      },
   ];

   public override IReadOnlyList<FieldDto> Options { get; } =
   [
      new(HeaderRowOption, "First row names the columns", FieldType.Bool) { Default = "true" },
      new(AllTextOption, "Read every column as text", FieldType.Bool) { Default = "false" },
      new(IncludeHiddenSheetsOption, "Include hidden sheets", FieldType.Bool) { Default = "false" },
   ];

   /// <summary>How a folder of workbooks reads with these settings and options.</summary>
   public static ExcelFolderOptions FolderOptions(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> options)
   {
      ArgumentNullException.ThrowIfNull(settings);
      ArgumentNullException.ThrowIfNull(options);
      return new ExcelFolderOptions
      {
         Path = settings[Folder],
         HeaderRow = Flag(options, HeaderRowOption) ?? true,
         AllText = Flag(options, AllTextOption) ?? false,
         IncludeHiddenSheets = Flag(options, IncludeHiddenSheetsOption) ?? false,
      };
   }

   public override bool IsSecret(string keyword) => false;

   protected override DbConnectionStringBuilder NewBuilder() => throw new NotSupportedException("A folder of workbooks has no connection string");

   public override List<KeyValuePair<string, string>> Normalize(IEnumerable<KeyValuePair<string, string>> pairs)
   {
      ArgumentNullException.ThrowIfNull(pairs);
      List<KeyValuePair<string, string>> normalized = [];
      foreach ((string key, string value) in pairs)
      {
         if (!string.Equals(key, Folder, StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException($"A folder of workbooks has no setting '{key}'; its one setting is {Folder}"); }
         normalized.Add(new KeyValuePair<string, string>(Folder, value));
      }
      return normalized;
   }

   public override List<KeyValuePair<string, string>> Parse(string connectionString) => throw new NotSupportedException("A folder of workbooks has no connection string");

   public override string Display(IReadOnlyDictionary<string, string> settings, IEnumerable<string> secretKeywords) =>
      throw new NotSupportedException("A folder of workbooks has no connection string");

   /// <summary>The folder's path.</summary>
   public override string ConnectionString(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> secrets, bool readOnly)
   {
      ArgumentNullException.ThrowIfNull(settings);
      return settings[Folder];
   }

   public override Task<string> ProbeAsync(string connectionString, CancellationToken cancellationToken)
   {
      int count = Directory.EnumerateFiles(connectionString, "*.xlsx")
         .Count(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal) && !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal));
      return Task.FromResult($"Found {Things(count, "workbook", "workbooks")} in the folder");
   }
}
