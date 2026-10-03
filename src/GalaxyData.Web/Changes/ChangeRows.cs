using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Types;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Metadata;

namespace GalaxyData.Web.Changes;

/// <summary>
/// A pending change as it is edited or planned: its values read from their JSON, by column name (sorted, so the
/// same change is always written the same).
/// </summary>
internal sealed class ChangeState
{
   /// <summary>The stored change; null for one made by these operations.</summary>
   public PendingChange? Row { get; set; }

   public PendingChangeKind Kind { get; set; }

   public string Entity { get; set; } = string.Empty;

   public string Source { get; set; } = string.Empty;

   public string? RowKey { get; set; }

   public string? TempId { get; set; }

   public SortedDictionary<string, JsonElement> Values { get; private set; } = new(StringComparer.Ordinal);

   public SortedDictionary<string, JsonElement> Original { get; private set; } = new(StringComparer.Ordinal);

   public SortedDictionary<string, JsonElement> Display { get; private set; } = new(StringComparer.Ordinal);

   /// <summary>Dropped by these operations.</summary>
   public bool Removed { get; set; }

   public static ChangeState Read(PendingChange row) => new()
   {
      Row = row,
      Kind = row.Kind,
      Entity = row.Entity,
      Source = row.Source,
      RowKey = row.RowKey,
      TempId = row.TempId,
      Values = ChangeRows.ReadValues(row.ValuesJson),
      Original = ChangeRows.ReadValues(row.OriginalJson),
      Display = ChangeRows.ReadValues(row.DisplayJson),
   };

   /// <summary>Starts again as a change of another kind, keeping the stored row (and its place in the order).</summary>
   public void Reset(PendingChangeKind kind)
   {
      Kind = kind;
      Values.Clear();
      Original.Clear();
      Display.Clear();
      Removed = false;
   }

   /// <summary>Writes the change to its row; whether the row changed.</summary>
   public bool WriteTo(PendingChange row)
   {
      string values = ChangeRows.WriteValues(Values);
      string original = ChangeRows.WriteValues(Original);
      string display = ChangeRows.WriteValues(Display);
      if (row.Kind == Kind && row.ValuesJson == values && row.OriginalJson == original && row.DisplayJson == display && row.Entity == Entity &&
          row.Source == Source && row.RowKey == RowKey && row.TempId == TempId)
      {
         return false;
      }
      row.Kind = Kind;
      row.Entity = Entity;
      row.Source = Source;
      row.RowKey = RowKey;
      row.TempId = TempId;
      row.ValuesJson = values;
      row.OriginalJson = original;
      row.DisplayJson = display;
      return true;
   }
}

/// <summary>Finding the entities, columns and values pending changes name, and writing them the one way they are kept.</summary>
internal static class ChangeRows
{
   private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

   public static SortedDictionary<string, JsonElement> ReadValues(string json) =>
      new(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Options) ?? [], StringComparer.Ordinal);

   public static string WriteValues(SortedDictionary<string, JsonElement> values) => JsonSerializer.Serialize(values, Options);

   /// <summary>A stored key's values, as they are sent.</summary>
   public static List<JsonElement> KeyValues(string rowKey) => JsonSerializer.Deserialize<List<JsonElement>>(rowKey, Options) ?? [];

   public static Dictionary<string, object?> ForDto(SortedDictionary<string, JsonElement> values) => values.ToDictionary(v => v.Key, v => (object?)v.Value, StringComparer.Ordinal);

   /// <summary>The entity a name gives, or why there is none.</summary>
   public static EntityDef? Entity(QueryCatalog catalog, string? name, out string? problem)
   {
      problem = null;
      if (string.IsNullOrWhiteSpace(name))
      {
         problem = "Name the entity, as queries name it (shop.orders)";
         return null;
      }
      if (!EntityName.TryParse(name, out EntityName? path))
      {
         problem = $"'{name}' isn't an entity's name, such as shop.orders";
         return null;
      }
      NameMatch<CatalogItem> match = catalog.Resolve(path);
      if (match.Status == MatchStatus.Ambiguous)
      {
         problem = $"{name} names more than one entity, whose names differ only in case: " +
            string.Join(", ", match.Candidates.OfType<EntityDef>().Select(e => e.DisplayName));
         return null;
      }
      if (match.Item is not EntityDef entity)
      {
         problem = $"There is no entity {name}";
         return null;
      }
      return entity;
   }

   /// <summary>The column a name gives (its exact name first, then ignoring case), or why there is none.</summary>
   public static ColumnDef? Column(EntityDef entity, string name, out string? problem)
   {
      NameMatch<ColumnDef> match = entity.FindColumn(name);
      problem = match.IsFound ? null
         : match.Status == MatchStatus.Ambiguous
            ? $"'{name}' could be {string.Join(" or ", match.Candidates.Select(c => "'" + c.Name + "'"))} of {entity.DisplayName}: give the name as it is spelled"
            : $"{entity.DisplayName} has no column '{name}'";
      return match.Item;
   }

   /// <summary>A value for a column, as the CLR value the engine takes, and as it is kept (as it is sent, written the one way); or why it isn't one.</summary>
   public static bool TryRead(ColumnDef column, object? given, out object? value, out JsonElement kept, out string? problem)
   {
      value = null;
      kept = default;
      problem = null;
      try
      {
         value = ValueCodec.Decode(ValueCodec.Json(given), column.Type);
      }
      catch (ValueFormatException e)
      {
         problem = e.Message;
         return false;
      }
      kept = ValueCodec.Json(ValueCodec.Encode(value, column.Type));
      if (kept.ValueKind == JsonValueKind.String && kept.GetString()!.Length > ChangeLimits.MaxValueLength)
      {
         problem = $"A value has at most {ChangeLimits.MaxValueLength} characters";
         return false;
      }
      return true;
   }

   /// <summary>Whether two values, as they are kept, are the same value of the column (<c>"12.5"</c> and <c>"12.50"</c> of a decimal are).</summary>
   public static bool Same(ColumnDef column, JsonElement a, JsonElement b)
   {
      if (!TryRead(column, a, out object? left, out _, out _) || !TryRead(column, b, out object? right, out _, out _)) { return false; }
      return (left, right) switch
      {
         (null, null) => true,
         (null, _) or (_, null) => false,
         (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
         // An offset is part of the value where the database keeps it (SQL Server's datetimeoffset).
         (DateTimeOffset x, DateTimeOffset y) => x.EqualsExact(y),
         _ => left.Equals(right),
      };
   }

   /// <summary>Whether a value may be shown for a row a navigation leads to: text, a number, true, false or null.</summary>
   public static bool IsDisplayValue(JsonElement value) =>
      value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;

   public static bool IsNullable(ColumnDef column) => column.Type.Nullable || column.Type.Kind == ScalarKind.Unknown;
}
