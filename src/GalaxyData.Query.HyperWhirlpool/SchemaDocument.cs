using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.Introspection;

namespace GalaxyData.Query.HyperWhirlpool;

/// <summary>
/// A warehouse's schema document (<c>hw-schema/1</c>): the public views of one version of its model, with the types
/// ClickHouse gives their columns and what the model says they mean. Members it doesn't name are ignored, as the
/// format allows; optional ones it leaves out are null.
/// </summary>
internal sealed record SchemaDocument(string Format, ModelVersion Model, IReadOnlyList<DomainEntry> Domains, IReadOnlyList<ObjectEntry> Objects)
{
   public const string CurrentFormat = "hw-schema/1";

   /// <summary>Members in snake case; one the records don't let be null, missing or null, makes the document unreadable.</summary>
   private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
   {
      PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
      RespectNullableAnnotations = true,
      RespectRequiredConstructorParameters = true,
   };

   /// <summary>
   /// The document a warehouse wrote; <see cref="InvalidDataException"/> when it isn't one, or is in another format
   /// (which is told first, as a later format's members may not read as these).
   /// </summary>
   public static SchemaDocument Parse(string json)
   {
      ArgumentNullException.ThrowIfNull(json);
      try
      {
         using (JsonDocument parsed = JsonDocument.Parse(json))
         {
            JsonElement root = parsed.RootElement;
            string? format = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("format", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            if (format != CurrentFormat)
            {
               throw new InvalidDataException(format == null
                  ? "The warehouse's schema document names no format"
                  : $"The warehouse's schema document is in the format {format}, which this version doesn't read (it reads {CurrentFormat})");
            }
         }
         return JsonSerializer.Deserialize<SchemaDocument>(json, Json) ?? throw new InvalidDataException("The warehouse's schema document is empty");
      }
      catch (JsonException e)
      {
         throw new InvalidDataException($"The warehouse's schema document can't be read: {e.Message}", e);
      }
   }

   /// <summary>
   /// The source's schema: each domain's database a schema, each view of it a view, with its key, and its references as
   /// foreign keys the warehouse doesn't enforce (an event's reference to an entity it hasn't seen is a zero value, not
   /// null). <paramref name="defaultSchema"/> is the connection's database.
   /// </summary>
   public SourceSchema ToSourceSchema(string? clickHouseVersion, string defaultSchema, IntrospectionOptions options)
   {
      ArgumentNullException.ThrowIfNull(options);
      List<TableSchema> tables = Objects
         .Where(o => Included(o.Database, options) && options.IncludeViews)
         .OrderBy(o => o.Database, StringComparer.Ordinal)
         .ThenBy(o => o.Name, StringComparer.Ordinal)
         .Select(Table)
         .ToList();
      string version = $"HyperWhirlpool model {Model.Seq.ToString(CultureInfo.InvariantCulture)} ({Model.Hash[..Math.Min(Model.Hash.Length, 12)]})";
      return new SourceSchema(HyperWhirlpoolSourceProvider.Kind, clickHouseVersion == null ? version : $"{version}, ClickHouse {clickHouseVersion}", defaultSchema, tables);
   }

   private static TableSchema Table(ObjectEntry entry)
   {
      HashSet<string> references = (entry.References ?? []).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
      List<ColumnSchema> columns = entry.Columns.Select((c, i) => new ColumnSchema(c.Name, i, c.Type, ClickHouseTypeMapper.Map(c.Type))
      {
         ReadAs = ClickHouseTypeMapper.ReadAs(c.Type),
         Comment = c.Description,
         Values = c.Values is { Count: > 0 } values ? values : null,
         Aggregate = AggregateOf(c),
         CarriedFrom = c.Carried is { } carried && references.Contains(carried.Reference) ? new CarriedColumn(carried.Reference, carried.Column) : null,
      }).ToList();
      HashSet<string> names = columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
      IReadOnlyList<string> key = entry.Key ?? [];
      string? time = entry.Time != null && names.Contains(entry.Time) ? entry.Time : null;
      string? timeEnd = time != null && entry.TimeEnd != null && names.Contains(entry.TimeEnd) ? entry.TimeEnd : null;
      return new TableSchema(entry.Database, entry.Name, TableKind.View, columns)
      {
         PrimaryKey = key.Count > 0 && key.All(names.Contains) ? new KeySchema(null, key) : null,
         ForeignKeys = (entry.References ?? [])
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new ForeignKeySchema(r.Name, r.Columns, r.Database, r.Object, r.RefColumns) { IsEnforced = false, IsWithinTimeSpan = r.Within == true })
            .ToList(),
         Comment = entry.Description,
         TimeColumn = time,
         TimeEndColumn = timeEnd,
         TimeSpanDays = timeEnd != null && entry.TimeSpanDays is > 0 ? entry.TimeSpanDays : null,
      };
   }

   /// <summary>
   /// How an attribute is summed up over many entities, from how the model aggregates it from events: counts and sums
   /// add up, the least and greatest stay so; others (the first or last value, whether any event had one, a set of
   /// them, a count of distinct values) don't sum up into anything that means as much.
   /// </summary>
   private static AggregateHint? AggregateOf(ColumnEntry column) => column.Role != "attribute" ? null : column.Aggregate switch
   {
      "count" or "sum" => AggregateHint.Sum,
      "min" => AggregateHint.Min,
      "max" => AggregateHint.Max,
      _ => null,
   };

   private static bool Included(string schema, IntrospectionOptions options)
   {
      if (options.IncludeSchemas is { Count: > 0 } include && !include.Contains(schema, StringComparer.OrdinalIgnoreCase)) { return false; }
      return options.ExcludeSchemas is not { Count: > 0 } exclude || !exclude.Contains(schema, StringComparer.OrdinalIgnoreCase);
   }
}

/// <summary>The model version a document describes.</summary>
internal sealed record ModelVersion(long Seq, string Hash, DateTimeOffset? PublishedAt = null);

/// <summary>A domain of the model, and the ClickHouse database its views are in.</summary>
internal sealed record DomainEntry(string Name, string Database, string? Description = null);

/// <summary>
/// A public view: what one row of it is (<see cref="Kind"/>), its columns, key and references, the column its rows are
/// dated by, the one a row's time span ends at, and the most days a span lasts.
/// </summary>
internal sealed record ObjectEntry(string Database, string Name, string Kind, IReadOnlyList<ColumnEntry> Columns, string? Declaration = null, string? Description = null,
                                   IReadOnlyList<string>? Key = null, string? Time = null, IReadOnlyList<ReferenceEntry>? References = null, string? TimeEnd = null,
                                   int? TimeSpanDays = null);

/// <summary>
/// A column: its ClickHouse type, what it holds (<see cref="Role"/>), for attributes and classified columns how they
/// come about, and for carried ones which reference's column it is a copy of.
/// </summary>
internal sealed record ColumnEntry(string Name, string Type, string? Role = null, string? Description = null, string? Aggregate = null, IReadOnlyList<string>? Values = null,
                                   CarriedEntry? Carried = null);

/// <summary>The column of the row a reference (<see cref="ReferenceEntry.Name"/>) finds that a carried column is a copy of.</summary>
internal sealed record CarriedEntry(string Reference, string Column);

/// <summary>
/// Columns of an object that hold the key of another; <see cref="Within"/> when the referring row's time falls within
/// the time span of the row it refers to.
/// </summary>
internal sealed record ReferenceEntry(string Name, IReadOnlyList<string> Columns, string Database, string Object, IReadOnlyList<string> RefColumns, string? Kind = null,
                                      bool? Within = null);
