using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Web.Features.Catalog;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;

namespace GalaxyData.Web.Overlay;

/// <summary>
/// A problem the catalog finds with an overlay item: an error leaves the item, or the part of it at fault (a column's
/// settings, a rename), out of the catalog; a warning doesn't.
/// </summary>
public sealed record OverlayIssueDto(string Code, DiagnosticSeverity Severity, string Message)
{
   public static OverlayIssueDto From(CatalogDiagnostic diagnostic)
   {
      ArgumentNullException.ThrowIfNull(diagnostic);
      return new OverlayIssueDto(diagnostic.Code, diagnostic.Severity, diagnostic.Message);
   }
}

/// <summary>An overlay item, by its kind and id.</summary>
public sealed record OverlayItemDto(OverlayItemKind Kind, int Id);

/// <summary>An overlay item, and its issues.</summary>
public sealed record OverlayItemIssuesDto(OverlayItemKind Kind, int Id, IReadOnlyList<OverlayIssueDto> Issues);

/// <summary>
/// A many-to-one relation: columns of <see cref="From"/> to the key, or a unique key, of <see cref="To"/>, in any
/// source. Entities are paths as queries write them (<c>shop.orders</c>). The navigations are named by
/// <see cref="Name"/> and <see cref="InverseName"/>, or by the convention when they are left out.
/// </summary>
public sealed record RelationInput(
   [Required, StringLength(MetadataDb.PathLength)] string From,
   [Required] List<string> FromColumns,
   [Required, StringLength(MetadataDb.PathLength)] string To,
   [Required] List<string> ToColumns,
   [StringLength(MetadataDb.NameLength)] string? Name = null,
   [StringLength(MetadataDb.NameLength)] string? InverseName = null,
   [StringLength(MetadataDb.DescriptionLength)] string? Description = null);

/// <summary>A navigation renamed (<see cref="RenameTo"/>) or hidden, found by the name the convention gives it.</summary>
public sealed record NavigationOverrideInput(
   [Required, StringLength(MetadataDb.PathLength)] string Entity,
   [Required, StringLength(MetadataDb.NameLength)] string Navigation,
   [StringLength(MetadataDb.NameLength)] string? RenameTo = null,
   bool Hidden = false);

/// <summary>An entity defined by a query; its name has a namespace (<c>reports.big_orders</c>), and <see cref="Key"/> is its key when the query's isn't.</summary>
public sealed record VirtualEntityInput(
   [Required, StringLength(MetadataDb.PathLength)] string Name,
   [Required, StringLength(VirtualEntityInput.MaxQueryLength)] string Query,
   List<string>? Key = null,
   [StringLength(MetadataDb.DescriptionLength)] string? Description = null)
{
   public const int MaxQueryLength = 100_000;
}

/// <summary>
/// An entity's settings: a declared key (for views and tables without one; it serves navigation, never changing
/// rows), its display column, whether it is hidden, and its columns' settings.
/// </summary>
public sealed record EntitySettingsInput(
   [Required, StringLength(MetadataDb.PathLength)] string Entity,
   List<string>? Key = null,
   [StringLength(MetadataDb.NameLength)] string? DisplayColumn = null,
   bool Hidden = false,
   List<ColumnSettingDto>? Columns = null);

public sealed record UpdateRelationRequest([Required] RelationInput Relation, int Version);

public sealed record UpdateNavigationOverrideRequest([Required] NavigationOverrideInput Navigation, int Version);

public sealed record UpdateVirtualEntityRequest([Required] VirtualEntityInput VirtualEntity, int Version);

public sealed record UpdateEntitySettingsRequest([Required] EntitySettingsInput Settings, int Version);

/// <summary>The navigations a relation makes, as the catalog names them: <see cref="Forward"/> on its entity, <see cref="Inverse"/> back from its target.</summary>
public sealed record RelationNavigationsDto(string Forward, string Inverse);

/// <summary>A relation, the navigations it makes (when it works) and what is wrong with it.</summary>
public sealed record RelationDto(int Id, string From, IReadOnlyList<string> FromColumns, string To, IReadOnlyList<string> ToColumns, string? Name, string? InverseName,
                                 string? Description, RelationNavigationsDto? Navigations, IReadOnlyList<OverlayIssueDto> Issues, DateTime CreatedAt,
                                 DateTime UpdatedAt, int Version);

public sealed record NavigationOverrideDto(int Id, string Entity, string Navigation, string? RenameTo, bool Hidden, IReadOnlyList<OverlayIssueDto> Issues,
                                           DateTime CreatedAt, DateTime UpdatedAt, int Version);

public sealed record VirtualEntityDto(int Id, string Name, string Query, IReadOnlyList<string>? Key, string? Description, IReadOnlyList<OverlayIssueDto> Issues,
                                      DateTime CreatedAt, DateTime UpdatedAt, int Version);

public sealed record EntitySettingsDto(int Id, string Entity, IReadOnlyList<string>? Key, string? DisplayColumn, bool Hidden, IReadOnlyList<ColumnSettingDto> Columns,
                                       IReadOnlyList<OverlayIssueDto> Issues, DateTime CreatedAt, DateTime UpdatedAt, int Version);

/// <summary>
/// The whole overlay, with what the catalog (of <see cref="CatalogVersion"/>) finds wrong with each item; how many
/// items have errors, and how many warnings only.
/// </summary>
public sealed record OverlayDto(string CatalogVersion, IReadOnlyList<RelationDto> Relations, IReadOnlyList<NavigationOverrideDto> Navigations,
                                IReadOnlyList<VirtualEntityDto> VirtualEntities, IReadOnlyList<EntitySettingsDto> EntitySettings, int Errors, int Warnings);

/// <summary>
/// What an item would be, tried against the catalog without saving it: its issues; a relation's navigations; the
/// entity a virtual entity or settings make, as the catalog would have it; a virtual entity's query's diagnostics,
/// placed in its text; and the other items that work now and wouldn't with it (<see cref="Breaks"/>).
/// </summary>
public sealed record OverlayCheckDto(IReadOnlyList<OverlayIssueDto> Issues)
{
   public IReadOnlyList<OverlayItemIssuesDto> Breaks { get; init; } = [];

   public RelationNavigationsDto? Navigations { get; init; }

   public EntityDto? Entity { get; init; }

   public IReadOnlyList<DiagnosticDto>? Diagnostics { get; init; }
}
