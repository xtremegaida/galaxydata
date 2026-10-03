using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Catalog;

/// <summary>
/// Logical mapping on top of the physical schema: relations the databases don't declare (including ones that
/// span sources), virtual entities written in the query language, per-entity settings and navigation renames.
/// Entities are referenced by path, as in queries (<c>shop.orders</c>).
/// </summary>
public sealed record CatalogOverlay
{
   public static readonly CatalogOverlay Empty = new();

   public IReadOnlyList<OverlayRelation> Relations { get; init; } = [];

   public IReadOnlyList<OverlayVirtualEntity> VirtualEntities { get; init; } = [];

   public IReadOnlyList<OverlayEntitySettings> Entities { get; init; } = [];

   public IReadOnlyList<OverlayNavigation> Navigations { get; init; } = [];

   public NavigationNamingOptions? Naming { get; init; }

   public string ToJson(bool indented = true) =>
      JsonSerializer.Serialize(this, indented ? CatalogOverlayJsonContext.Indented.CatalogOverlay : CatalogOverlayJsonContext.Default.CatalogOverlay);

   /// <summary>
   /// An overlay from its JSON: lists left out are empty; a relation, virtual entity, entity setting or navigation
   /// without the names it needs is a <see cref="JsonException"/> that says which.
   /// </summary>
   public static CatalogOverlay FromJson(string json)
   {
      ArgumentNullException.ThrowIfNull(json);
      CatalogOverlay overlay = JsonSerializer.Deserialize(json, CatalogOverlayJsonContext.Default.CatalogOverlay) ?? Empty;
      // The serializer sets lists the JSON leaves out to null, not to their defaults (they are init-only).
      return overlay with
      {
         Relations = [.. (overlay.Relations ?? []).Select(Checked)],
         VirtualEntities = [.. (overlay.VirtualEntities ?? []).Select(v => v.Name != null && v.Query != null ? v : throw Missing("A virtual entity", "\"name\" and \"query\""))],
         Entities = [.. (overlay.Entities ?? []).Select(e => e.Entity != null ? e with { Columns = [.. (e.Columns ?? []).Select(c => c.Name != null ? c : throw Missing("A column setting", "\"name\""))] }
                                                                            : throw Missing("An entity setting", "\"entity\""))],
         Navigations = [.. (overlay.Navigations ?? []).Select(n => n.Entity != null && n.Name != null ? n : throw Missing("A navigation setting", "\"entity\" and \"name\""))],
         Naming = overlay.Naming is { } naming
            ? naming with
            {
               SeparatedSuffixes = naming.SeparatedSuffixes ?? NavigationNamingOptions.Default.SeparatedSuffixes,
               CamelSuffixes = naming.CamelSuffixes ?? NavigationNamingOptions.Default.CamelSuffixes,
               DisplayColumnNames = naming.DisplayColumnNames ?? NavigationNamingOptions.Default.DisplayColumnNames,
            }
            : null,
      };
   }

   private static OverlayRelation Checked(OverlayRelation relation) =>
      relation.From != null && relation.To != null && relation.FromColumns is { Count: > 0 } && relation.ToColumns is { Count: > 0 }
         ? relation
         : throw Missing("A relation", "\"from\", \"fromColumns\", \"to\" and \"toColumns\"");

   private static JsonException Missing(string what, string needs) => new($"{what} of the overlay needs {needs}");
}

/// <summary>A many-to-one link from <see cref="From"/> columns to unique columns of <see cref="To"/>.</summary>
public sealed record OverlayRelation(string From, IReadOnlyList<string> FromColumns, string To, IReadOnlyList<string> ToColumns)
{
   /// <summary>The name of the navigation on <see cref="From"/>; the convention decides when unset.</summary>
   public string? Name { get; init; }

   /// <summary>The name of the navigation back from <see cref="To"/>; the convention decides when unset.</summary>
   public string? InverseName { get; init; }

   public string? Description { get; init; }
}

public sealed record OverlayVirtualEntity(string Name, string Query)
{
   public IReadOnlyList<string>? Key { get; init; }

   public string? Description { get; init; }
}

public sealed record OverlayEntitySettings(string Entity)
{
   /// <summary>A declared key for views and tables without one; it enables navigation, never editing.</summary>
   public IReadOnlyList<string>? Key { get; init; }

   public string? DisplayColumn { get; init; }

   public bool Hidden { get; init; }

   public IReadOnlyList<OverlayColumn> Columns { get; init; } = [];
}

public sealed record OverlayColumn(string Name)
{
   public bool Hidden { get; init; }

   public string? Label { get; init; }

   /// <summary>Overrides the introspected logical type, e.g. <c>date</c> for a SQLite text column.</summary>
   public ScalarType? Type { get; init; }
}

/// <summary>Renames or hides a navigation, found by the name the convention gave it.</summary>
public sealed record OverlayNavigation(string Entity, string Name)
{
   public string? RenameTo { get; init; }

   public bool Hidden { get; init; }
}

public sealed record NavigationNamingOptions
{
   public static readonly NavigationNamingOptions Default = new();

   /// <summary>Suffixes after a separator, matched in any case: <c>customer_id</c> gives <c>customer</c>.</summary>
   public IReadOnlyList<string> SeparatedSuffixes { get; init; } = ["_id", "_fk", "_key", "_ref", "_code"];

   /// <summary>
   /// Camel-case suffixes, matched exactly and only after a lower-case letter or digit, so <c>CustomerID</c> gives
   /// <c>Customer</c> but <c>paid</c> stays <c>paid</c>.
   /// </summary>
   public IReadOnlyList<string> CamelSuffixes { get; init; } = ["Id", "ID", "Fk", "FK", "Key", "Ref", "Code"];

   /// <summary>Column names picked, in order, as an entity's display column.</summary>
   public IReadOnlyList<string> DisplayColumnNames { get; init; } =
      ["name", "title", "display_name", "displayname", "label", "code", "description"];
}

[JsonSourceGenerationOptions(
   PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
   DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
   UseStringEnumConverter = true)]
[JsonSerializable(typeof(CatalogOverlay))]
internal sealed partial class CatalogOverlayJsonContext : JsonSerializerContext
{
   private static CatalogOverlayJsonContext? indented;

   public static CatalogOverlayJsonContext Indented =>
      indented ??= new CatalogOverlayJsonContext(new JsonSerializerOptions(Default.Options) { WriteIndented = true });
}
