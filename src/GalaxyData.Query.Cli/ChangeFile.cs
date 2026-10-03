using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Dml;

namespace GalaxyData.Query.Cli;

/// <summary>
/// Changes to rows from a JSON file: an array of <c>{"insert": "shop.customers", "values": {...}}</c>,
/// <c>{"update": "shop.orders", "key": {...}, "values": {...}, "original": {...}}</c> and
/// <c>{"delete": "shop.order_lines", "key": {...}, "original": {...}}</c>. Whole numbers are 64-bit, other numbers
/// decimals, and text meets its column's type as the planner converts it (<c>"2026-03-01"</c> for a date).
/// </summary>
internal static class ChangeFile
{
   private static readonly string[] Kinds = ["insert", "update", "delete"];

   public static ChangeSet Parse(string json, ICatalog catalog)
   {
      using JsonDocument document = JsonDocument.Parse(json);
      if (document.RootElement.ValueKind != JsonValueKind.Array)
      {
         throw new FormatException("""A change file is a JSON array of changes, e.g. [{"update": "shop.orders", "key": {"id": 1001}, "values": {"total": 260}}]""");
      }
      ChangeSet changes = new();
      int index = 0;
      foreach (JsonElement change in document.RootElement.EnumerateArray()) { changes.Add(Change(change, catalog, index++)); }
      return changes;
   }

   private static RowChange Change(JsonElement change, ICatalog catalog, int index)
   {
      if (change.ValueKind != JsonValueKind.Object) { throw new FormatException($"Change {index} isn't a JSON object"); }
      foreach (string kind in Kinds)
      {
         if (!change.TryGetProperty(kind, out JsonElement name)) { continue; }
         string path = name.ValueKind == JsonValueKind.String ? name.GetString()! : throw new FormatException($"Change {index}: name the entity to {kind} as text");
         EntityDef entity = (EntityName.TryParse(path, out EntityName? parsed) ? catalog.FindEntity(parsed) : null)
            ?? throw new FormatException($"Change {index}: there is no entity '{path}'");
         return kind switch
         {
            "insert" => new InsertRow(entity, Values(change, "values", index) ?? new Dictionary<string, object?>()),
            "update" => new UpdateRow(entity, Required(change, "key", index), Required(change, "values", index)) { Original = Values(change, "original", index) },
            _ => new DeleteRow(entity, Required(change, "key", index)) { Original = Values(change, "original", index) },
         };
      }
      throw new FormatException($"Change {index} is neither an insert, an update nor a delete");
   }

   private static Dictionary<string, object?> Required(JsonElement change, string property, int index) =>
      Values(change, property, index) ?? throw new FormatException($"Change {index} needs \"{property}\"");

   private static Dictionary<string, object?>? Values(JsonElement change, string property, int index)
   {
      if (!change.TryGetProperty(property, out JsonElement values)) { return null; }
      if (values.ValueKind != JsonValueKind.Object) { throw new FormatException($"Change {index}: \"{property}\" is an object of column names and values"); }
      Dictionary<string, object?> result = new(StringComparer.Ordinal);
      foreach (JsonProperty value in values.EnumerateObject())
      {
         result[value.Name] = value.Value.ValueKind switch
         {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number when value.Value.TryGetInt64(out long whole) => whole,
            JsonValueKind.Number => decimal.Parse(value.Value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => throw new FormatException($"Change {index}: the value of '{value.Name}' is neither null, true, false, a number nor text"),
         };
      }
      return result;
   }
}
