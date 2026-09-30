using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DuckDB.NET.Data;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Excel;

/// <summary>
/// One folder of workbooks, kept in a catalog of the merge engine's database: a schema for each workbook and a
/// table for each sheet, loaded when a query first reads it and again when its workbook changes (its time or size)
/// or the columns asked for do, or when a query that converts strictly reads a sheet loaded with nulls for values
/// that didn't convert. A sheet is loaded into a table of its own, then swapped in, so queries reading the old rows
/// meanwhile go on seeing them.
/// </summary>
internal sealed class ExcelFolder
{
   private readonly DuckDbMergeEngine merge;
   private readonly ConcurrentDictionary<string, SheetState> sheets = new(StringComparer.OrdinalIgnoreCase);

   /// <summary>The tables sheets are being loaded into now.</summary>
   private readonly ConcurrentDictionary<string, byte> staging = new(StringComparer.OrdinalIgnoreCase);

   /// <summary>Schemas every DuckDB catalog has, where tables can't be made: workbooks named so are left out.</summary>
   private static readonly HashSet<string> ReservedSchemas = new(["information_schema", "pg_catalog"], StringComparer.OrdinalIgnoreCase);

   /// <summary>Changes to the catalog's schemas and table names, one at a time: DuckDB fails one of two that race.</summary>
   private readonly SemaphoreSlim changes = new(1, 1);

   private int loads;

   public ExcelFolder(string alias, string catalog, ExcelFolderOptions options, DuckDbMergeEngine merge)
   {
      Alias = alias;
      Catalog = catalog;
      Options = options;
      this.merge = merge;
      Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(options.Path));
   }

   public string Alias { get; }

   public ExcelFolderOptions Options { get; }

   /// <summary>The folder, as a full path.</summary>
   public string Path { get; }

   /// <summary>The catalog of the merge engine's database the sheets are loaded into.</summary>
   public string Catalog { get; }

   private static string Quote(string name) => SqlDialect.DuckDb.QuoteIdentifier(name);

   private string Name(string schema, string table) => $"{Quote(Catalog)}.{Quote(schema)}.{Quote(table)}";

   public void Attach() => Execute($"ATTACH IF NOT EXISTS ':memory:' AS {Quote(Catalog)}");

   public void Detach()
   {
      try
      {
         Execute($"DETACH DATABASE IF EXISTS {Quote(Catalog)}");
      }
      catch (Exception e) when (e is DbException or ObjectDisposedException)
      {
         // The merge engine is gone, and the catalog with it.
      }
   }

   private void Execute(string sql)
   {
      using DuckDBConnection connection = merge.Connect();
      Execute(connection, sql);
   }

   private static void Execute(DuckDBConnection connection, string sql)
   {
      using DuckDBCommand command = connection.CreateCommand();
      command.CommandText = sql;
      command.ExecuteNonQuery();
   }

   /// <summary>The folder's workbooks: its own .xlsx files (not those of folders in it), without Excel's lock files (<c>~$</c>) and hidden files.</summary>
   public List<FileInfo> Workbooks()
   {
      DirectoryInfo folder = new(Path);
      if (!folder.Exists) { throw new DirectoryNotFoundException($"The folder of {Alias} doesn't exist: {Path}"); }
      return folder.EnumerateFiles("*.xlsx", SearchOption.TopDirectoryOnly)
         .Where(f => string.Equals(f.Extension, ".xlsx", StringComparison.OrdinalIgnoreCase) &&
                     !f.Name.StartsWith("~$", StringComparison.Ordinal) && !f.Name.StartsWith("._", StringComparison.Ordinal) &&
                     (f.Attributes & FileAttributes.Hidden) == 0)
         .OrderBy(f => f.Name, StringComparer.Ordinal)
         .ToList();
   }

   /// <summary>
   /// The folder's schema: a schema for each workbook, named by its file, and a table for each sheet with a value
   /// (hidden ones only when asked for), with every row read for the columns' types. Workbooks and sheets that can't
   /// be read are left out, with a warning. Nothing is loaded.
   /// </summary>
   public Task<SourceSchema> IntrospectAsync(IntrospectionOptions options, CancellationToken cancellationToken) =>
      Task.Run(() => Introspect(options, cancellationToken), cancellationToken);

   private SourceSchema Introspect(IntrospectionOptions options, CancellationToken cancellationToken)
   {
      List<TableSchema> tables = [];
      List<string> warnings = [];
      HashSet<string> schemas = new(StringComparer.OrdinalIgnoreCase);
      foreach (FileInfo file in Workbooks())
      {
         string schema = System.IO.Path.GetFileNameWithoutExtension(file.Name);
         if (!Included(schema, options)) { continue; }
         if (ReservedSchemas.Contains(schema))
         {
            warnings.Add($"'{file.Name}' is left out: DuckDB, which keeps the sheets, has a schema of that name in every database");
            continue;
         }
         if (!schemas.Add(schema))
         {
            warnings.Add($"'{file.Name}' is left out: its name differs from another workbook's only in case");
            continue;
         }
         Workbook workbook;
         try
         {
            workbook = Workbook.Open(file.FullName);
         }
         catch (Exception e) when (Unreadable(e))
         {
            warnings.Add($"'{file.Name}' can't be read: {Reason(e)}");
            continue;
         }
         using (workbook)
         {
            foreach (SheetInfo sheet in workbook.Sheets)
            {
               cancellationToken.ThrowIfCancellationRequested();
               if (sheet.Hidden && !Options.IncludeHiddenSheets) { continue; }
               SheetLayout layout;
               try
               {
                  layout = SheetLayout.Read(workbook.ReadRows(sheet), Options.HeaderRow, Options.AllText);
               }
               catch (Exception e) when (Unreadable(e))
               {
                  warnings.Add($"The sheet '{sheet.Name}' of '{file.Name}' can't be read: {Reason(e)}");
                  continue;
               }
               // A sheet without a value has no columns, and no table.
               if (layout.Columns.Count == 0) { continue; }
               List<ColumnSchema> columns = layout.Columns
                  .Select((c, i) => new ColumnSchema(c.Name, i, DuckDbTypeMapper.TypeName(c.Type), c.Type))
                  .ToList();
               tables.Add(new TableSchema(schema, sheet.Name, TableKind.Table, columns)
               {
                  RowCountEstimate = options.IncludeRowCountEstimates ? layout.Rows : null,
               });
            }
         }
      }
      return new SourceSchema(ExcelSourceProvider.Kind, null, string.Empty,
                              [.. tables.OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal)])
      {
         Warnings = warnings.Count == 0 ? null : warnings,
      };
   }

   private static bool Included(string schema, IntrospectionOptions options)
   {
      if (options.IncludeSchemas is { Count: > 0 } include && !include.Contains(schema, StringComparer.OrdinalIgnoreCase)) { return false; }
      return options.ExcludeSchemas is not { Count: > 0 } exclude || !exclude.Contains(schema, StringComparer.OrdinalIgnoreCase);
   }

   private static bool Unreadable(Exception e) =>
      e is WorkbookFormatException or IOException or UnauthorizedAccessException or XmlException or InvalidDataException;

   private static string Reason(Exception e) => e switch
   {
      WorkbookFormatException format => format.Message,
      XmlException xml => "its XML is damaged: " + xml.Message,
      InvalidDataException => "it is damaged",
      _ => e.Message,
   };

   /// <summary>Loads the sheets of the tables that aren't loaded, or whose workbooks or columns changed since they were.</summary>
   public async ValueTask PrepareAsync(IReadOnlyList<TableEntity> tables, bool lenient, CancellationToken cancellationToken)
   {
      foreach (TableEntity table in tables) { await PrepareAsync(table, lenient, cancellationToken).ConfigureAwait(false); }
   }

   private async ValueTask PrepareAsync(TableEntity table, bool lenient, CancellationToken cancellationToken)
   {
      SheetState state = sheets.GetOrAdd(table.Schema + "\0" + table.Table, _ => new SheetState());
      TableColumn[] wanted = table.Columns.Select(c => new TableColumn(c.Name, c.Type)).ToArray();
      await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
         FileInfo file = new(System.IO.Path.Combine(Path, table.Schema + ".xlsx"));
         // A schema names a workbook in the folder, never a file elsewhere.
         if (!string.Equals(file.DirectoryName, Path, StringComparison.Ordinal))
         {
            throw new QueryExecutionException($"{Alias}: '{table.Schema}' isn't the name of a workbook in {Path}");
         }
         if (!file.Exists)
         {
            // Excel saves by writing a new file and renaming it into place: for a moment there is none.
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            file.Refresh();
         }
         if (!file.Exists)
         {
            throw new QueryExecutionException($"{Alias}: the workbook '{table.Schema}.xlsx' is no longer in {Path}; refresh the source's schema");
         }
         FileStamp stamp = new(file.LastWriteTimeUtc, file.Length);
         if (state.Stamp == stamp && state.Columns != null && state.Columns.SequenceEqual(wanted) && (lenient || !state.Nulled)) { return; }
         state.Nulled = await Task.Run(() => Load(file, table, wanted, lenient, cancellationToken), cancellationToken).ConfigureAwait(false);
         state.Stamp = stamp;
         state.Columns = wanted;
      }
      finally
      {
         state.Gate.Release();
      }
   }

   /// <summary>
   /// Loads a sheet as the table's columns: each found by name among the sheet's (in any case, when no name matches
   /// exactly), its values converted to the column's type. A value that doesn't convert fails the load, or is null
   /// when conversion is lenient; whether any was.
   /// </summary>
   private bool Load(FileInfo file, TableEntity table, TableColumn[] wanted, bool lenient, CancellationToken cancellationToken)
   {
      Workbook workbook;
      try
      {
         workbook = Workbook.Open(file.FullName);
      }
      catch (Exception e) when (Unreadable(e))
      {
         throw new QueryExecutionException($"{Alias}: '{file.Name}' can't be read: {Reason(e)}", e);
      }
      using (workbook)
      {
         SheetInfo sheet = workbook.Sheets.FirstOrDefault(s => string.Equals(s.Name, table.Table, StringComparison.Ordinal))
            ?? workbook.Sheets.FirstOrDefault(s => string.Equals(s.Name, table.Table, StringComparison.OrdinalIgnoreCase))
            ?? throw new QueryExecutionException($"{Alias}: '{file.Name}' has no sheet '{table.Table}' now; refresh the source's schema");
         try
         {
            SheetLayout layout = SheetLayout.Read(workbook.ReadRows(sheet), Options.HeaderRow, allText: true);
            int[] columns = wanted.Select(c => Find(layout, c.Name)?.Index
               ?? throw new QueryExecutionException($"{Alias}: the sheet '{sheet.Name}' of '{file.Name}' has no column '{c.Name}' now; refresh the source's schema")).ToArray();
            return Store(workbook, sheet, layout, table, wanted, columns, lenient, file.Name, cancellationToken);
         }
         catch (Exception e) when (Unreadable(e))
         {
            throw new QueryExecutionException($"{Alias}: the sheet '{sheet.Name}' of '{file.Name}' can't be read: {Reason(e)}", e);
         }
         catch (Exception e) when (e is DbException or InvalidCastException or InvalidOperationException)
         {
            throw new QueryExecutionException($"{Alias}: the merge engine failed to load the sheet '{sheet.Name}' of '{file.Name}': {e.Message}", e);
         }
      }
   }

   private static SheetColumn? Find(SheetLayout layout, string name) =>
      layout.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
      ?? layout.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

   /// <summary>Appends the sheet's rows to a new table, then swaps it in for the table queries read; whether a value was nulled.</summary>
   private bool Store(Workbook workbook, SheetInfo sheet, SheetLayout layout, TableEntity table, TableColumn[] wanted, int[] columns, bool lenient,
                      string fileName, CancellationToken cancellationToken)
   {
      // Sheet names can't have brackets in them, so this name is never a sheet's.
      string loading = "[load " + Interlocked.Increment(ref loads).ToString(CultureInfo.InvariantCulture) + "]";
      staging[table.Schema + "\0" + loading] = 0;
      try
      {
         return Store(workbook, sheet, layout, table, wanted, columns, lenient, fileName, loading, cancellationToken);
      }
      finally
      {
         staging.TryRemove(table.Schema + "\0" + loading, out _);
      }
   }

   private bool Store(Workbook workbook, SheetInfo sheet, SheetLayout layout, TableEntity table, TableColumn[] wanted, int[] columns, bool lenient,
                      string fileName, string staging, CancellationToken cancellationToken)
   {
      using DuckDBConnection connection = merge.Connect();
      changes.Wait(cancellationToken);
      try
      {
         Execute(connection, $"CREATE SCHEMA IF NOT EXISTS {Quote(Catalog)}.{Quote(table.Schema)}");
      }
      finally
      {
         changes.Release();
      }
      string definitions = string.Join(", ", wanted.Select(c => Quote(c.Name) + " " + DuckDbTypeMapper.TypeName(c.Type)));
      Execute(connection, $"CREATE TABLE {Name(table.Schema, staging)} ({definitions})");
      try
      {
         bool nulled = Append(connection, workbook, sheet, layout, table.Schema, staging, wanted, columns, lenient, fileName, cancellationToken);
         changes.Wait(cancellationToken);
         try
         {
            Execute(connection, "BEGIN TRANSACTION");
            try
            {
               Execute(connection, $"DROP TABLE IF EXISTS {Name(table.Schema, table.Table)}");
               Execute(connection, $"ALTER TABLE {Name(table.Schema, staging)} RENAME TO {Quote(table.Table)}");
               Execute(connection, "COMMIT");
            }
            catch
            {
               Execute(connection, "ROLLBACK");
               throw;
            }
         }
         finally
         {
            changes.Release();
         }
         return nulled;
      }
      catch
      {
         try
         {
            Execute(connection, $"DROP TABLE IF EXISTS {Name(table.Schema, staging)}");
         }
         catch (DbException)
         {
            // It goes with the catalog.
         }
         throw;
      }
   }

   /// <summary>Appends the sheet's rows, as the columns' types, to the staging table; whether a value that didn't convert was nulled.</summary>
   private bool Append(DuckDBConnection connection, Workbook workbook, SheetInfo sheet, SheetLayout layout, string schema, string staging,
                       TableColumn[] wanted, int[] columns, bool lenient, string fileName, CancellationToken cancellationToken)
   {
      bool nulled = false;
      int width = columns.Length == 0 ? 0 : columns.Max() + 1;
      int[] slots = Enumerable.Repeat(-1, width).ToArray();
      for (int i = 0; i < columns.Length; i++) { slots[columns[i]] = i; }
      object?[] values = new object?[wanted.Length];
      using DuckDBAppender appender = connection.CreateAppender(Catalog, schema, staging);
      try
      {
         foreach (SheetRow row in workbook.ReadRows(sheet))
         {
            if (row.Number <= layout.Header || !SheetLayout.HasValue(row)) { continue; }
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(values);
            foreach (Cell cell in row.Cells)
            {
               int slot = cell.Column < slots.Length ? slots[cell.Column] : -1;
               if (slot < 0) { continue; }
               if (CellConversion.TryConvert(cell.Value, wanted[slot].Type, workbook.Date1904, out values[slot], out string? error))
               {
                  // Decimals of no precision are held as doubles, as in the merge engine's tables.
                  if (values[slot] is decimal number && wanted[slot].Type.Precision == 0) { values[slot] = (double)number; }
               }
               else
               {
                  if (!lenient)
                  {
                     throw new QueryExecutionException(
                        $"{Alias}: row {row.Number.ToString(CultureInfo.InvariantCulture)} of the sheet '{sheet.Name}' in '{fileName}', column '{wanted[slot].Name}' " +
                        $"(cell {Workbook.Letters(cell.Column)}{row.Number.ToString(CultureInfo.InvariantCulture)}): {error}");
                  }
                  values[slot] = null;
                  nulled = true;
               }
            }
            IDuckDBAppenderRow line = appender.CreateRow();
            foreach (object? value in values) { line = DuckDbAppending.Append(line, value); }
            line.EndRow();
         }
      }
      catch
      {
         // Rows the appender holds (a part of one, above all) must go before it is disposed.
         appender.Clear();
         throw;
      }
      appender.Close();
      return nulled;
   }

   /// <summary>
   /// Forgets the sheets that are loaded, and drops their tables, other than the tables of <paramref name="keep"/>;
   /// and drops tables left from loads that failed.
   /// </summary>
   public void Prune(IReadOnlyCollection<TableSchema> keep)
   {
      HashSet<string> kept = new(keep.Select(t => t.Schema + "\0" + t.Name), StringComparer.OrdinalIgnoreCase);
      List<(string Schema, string Table)> loaded = [];
      using DuckDBConnection connection = merge.Connect();
      using (DuckDBCommand command = connection.CreateCommand())
      {
         command.CommandText = "SELECT schema_name, table_name FROM duckdb_tables() WHERE database_name = $catalog";
         command.Parameters.Add(new DuckDBParameter("catalog", Catalog));
         using DbDataReader reader = command.ExecuteReader();
         while (reader.Read()) { loaded.Add((reader.GetString(0), reader.GetString(1))); }
      }
      changes.Wait();
      try
      {
         foreach ((string schema, string name) in loaded)
         {
            if (kept.Contains(schema + "\0" + name) || staging.ContainsKey(schema + "\0" + name)) { continue; }
            Execute(connection, $"DROP TABLE IF EXISTS {Name(schema, name)}");
            sheets.TryRemove(schema + "\0" + name, out _);
         }
      }
      finally
      {
         changes.Release();
      }
   }

   private readonly record struct FileStamp(DateTime Written, long Length);

   private sealed record TableColumn(string Name, ScalarType Type);

   private sealed class SheetState
   {
      public SemaphoreSlim Gate { get; } = new(1, 1);

      public FileStamp? Stamp { get; set; }

      public TableColumn[]? Columns { get; set; }

      /// <summary>Whether values that didn't convert were loaded as nulls (the load was lenient).</summary>
      public bool Nulled { get; set; }
   }
}
