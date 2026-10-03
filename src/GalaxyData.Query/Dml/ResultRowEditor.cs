using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Results;

namespace GalaxyData.Query.Dml;

/// <summary>
/// Turns edits of a query's result rows into changes of the table rows the values came from, through the result's
/// edit targets (<see cref="ResultColumn.EditTarget"/>) and row identity. Rows are as the result gives them, hidden
/// key columns included.
/// </summary>
public static class ResultRowEditor
{
   /// <summary>
   /// The updates that set result values of one row, new values by result ordinal: one for each table row the values
   /// belong to (a row of orders and of its customer, say), with the values the row was read with as the originals.
   /// </summary>
   public static IReadOnlyList<UpdateRow> Update(ResultSchema schema, IReadOnlyList<object?> row, IReadOnlyDictionary<int, object?> values)
   {
      ArgumentNullException.ThrowIfNull(schema);
      ArgumentNullException.ThrowIfNull(row);
      ArgumentNullException.ThrowIfNull(values);
      List<(TableEntity Table, IReadOnlyList<int> KeyOrdinals, Dictionary<string, object?> Values, Dictionary<string, object?> Original)> rows = [];
      foreach ((int ordinal, object? value) in values.OrderBy(v => v.Key))
      {
         if (ordinal < 0 || ordinal >= schema.Columns.Count) { throw new ArgumentOutOfRangeException(nameof(values), ordinal, "The result has no column at this ordinal"); }
         ResultColumn column = schema.Columns[ordinal];
         EditTarget target = column.EditTarget
            ?? throw new ArgumentException($"'{column.Name}' can't be edited: it isn't a table's column read along a path that keeps the table's rows apart", nameof(values));
         int at = rows.FindIndex(r => r.Table == target.Entity && r.KeyOrdinals.SequenceEqual(target.KeyOrdinals));
         if (at < 0)
         {
            rows.Add((target.Entity, target.KeyOrdinals, new Dictionary<string, object?>(StringComparer.Ordinal), new Dictionary<string, object?>(StringComparer.Ordinal)));
            at = rows.Count - 1;
         }
         (_, _, Dictionary<string, object?> changed, Dictionary<string, object?> original) = rows[at];
         if (changed.TryGetValue(target.Column.Name, out object? other) && !Equals(other, value))
         {
            throw new ArgumentException($"'{target.Column.Name}' of {target.Entity.DisplayName} is given two values", nameof(values));
         }
         changed[target.Column.Name] = value;
         original[target.Column.Name] = row[ordinal];
      }
      return rows.Select(r => new UpdateRow(r.Table, Key(r.Table, r.KeyOrdinals, row), r.Values) { Original = r.Original }).ToList();
   }

   /// <summary>The delete of the table row a result row is (<see cref="ResultSchema.RowIdentity"/>), with the row's values of the table's columns as the originals.</summary>
   public static DeleteRow Delete(ResultSchema schema, IReadOnlyList<object?> row)
   {
      ArgumentNullException.ThrowIfNull(schema);
      ArgumentNullException.ThrowIfNull(row);
      RowIdentity identity = schema.RowIdentity ?? throw new ArgumentException("The result's rows aren't the rows of one entity with a key", nameof(schema));
      if (identity.Entity is not TableEntity table) { throw new ArgumentException($"The result's rows are rows of {identity.Entity.DisplayName}, which isn't a table", nameof(schema)); }
      Dictionary<string, object?> original = new(StringComparer.Ordinal);
      foreach (ResultColumn column in schema.Columns)
      {
         if (column.EditTarget is { } target && target.Entity == table && target.KeyOrdinals.SequenceEqual(identity.KeyOrdinals))
         {
            original.TryAdd(target.Column.Name, row[column.Ordinal]);
         }
      }
      return new DeleteRow(table, Key(table, identity.KeyOrdinals, row)) { Original = original };
   }

   private static Dictionary<string, object?> Key(TableEntity table, IReadOnlyList<int> ordinals, IReadOnlyList<object?> row)
   {
      IReadOnlyList<ColumnDef> columns = table.Key!.Columns;
      Dictionary<string, object?> key = new(StringComparer.Ordinal);
      for (int i = 0; i < columns.Count; i++)
      {
         key[columns[i].Name] = row[ordinals[i]] ?? throw new ArgumentException($"The row has no value for '{columns[i].Name}' of the key of {table.DisplayName}", nameof(row));
      }
      return key;
   }
}
