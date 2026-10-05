using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Results;
using GalaxyData.Web.Catalog;

namespace GalaxyData.Web.Browse;

/// <summary>A row of an entity, by its key (the values in key order, as rows' values are sent).</summary>
/// <remarks>
/// Values are declared <c>object</c>, which JSON reads as <see cref="JsonElement"/>: request validation would look
/// into a <see cref="JsonElement"/> declared so, and fail on its indexer.
/// </remarks>
public sealed record BrowseFromDto([Required] string Entity, [Required] List<object?> Key);

/// <summary>The rows to browse: an entity's (<see cref="Entity"/>), or those a navigation leads to from a row (<see cref="From"/> and <see cref="Navigation"/>).</summary>
public sealed record BrowseSourceDto
{
   public string? Entity { get; init; }

   public BrowseFromDto? From { get; init; }

   public string? Navigation { get; init; }
}

/// <summary>A page of rows: what to browse, what the grid shows of it, and whether to send the columns and count the rows.</summary>
public sealed record BrowsePageRequest([Required] BrowseSourceDto Source, GridStateDto? Grid = null, bool IncludeSchema = true, bool IncludeCount = false);

/// <summary>A parameter of a query: its type as the language writes it, and its value as rows' values are sent.</summary>
public sealed record QueryParameterDto(string Name, string Type, object? Value);

/// <summary>A physical column a value comes from (<c>shop.customers.name</c>), and the navigations that lead to it (<c>customer</c>).</summary>
public sealed record LineageSourceDto(string Column, string? Path);

/// <summary>Where a column's values come from: a column, an expression of columns, an aggregate, a constant.</summary>
public sealed record LineageDto(LineageKind Kind, IReadOnlyList<LineageSourceDto> Sources, string? Expression);

/// <summary>
/// A column of the grid (the <c>n</c>-th value of each row): its type, whether it is part of the key, whether the
/// user may change it and give it a value in a new row, where its values come from, and the reference it is part of
/// (an index into <see cref="BrowseSchemaDto.References"/>).
/// </summary>
public sealed record GridColumnDto(string Name, TypeDto Type, bool IsKey, bool CanUpdate, InsertMode Insert, string? ReadOnlyReason, LineageDto Lineage,
                                   int? Reference);

/// <summary>
/// A row each row refers to (along a foreign key, <c>customer</c> by <c>customer_id</c>): the columns that hold the
/// reference (indexes into the values, in the foreign key's order: all of a composite key's, though some of them
/// may show another reference), the target's columns they match (by name, in the same order), whether every column
/// that holds it is among the values (<see cref="Complete"/>: else it can't be set by choosing a row), the target's
/// column that shows its rows (none when it has none), and the target's display value, the <c>n</c>-th of each row's
/// <c>r</c>.
/// </summary>
public sealed record GridReferenceDto(string Navigation, string Target, IReadOnlyList<int> Columns, IReadOnlyList<string> TargetColumns, bool Complete,
                                      string? DisplayColumn, Multiplicity Multiplicity);

/// <summary>The rows that refer to each row (<c>orders</c> of a customer), to browse from it.</summary>
public sealed record GridCollectionDto(string Navigation, string Target, Multiplicity Multiplicity);

/// <summary>
/// What the rows are: the entity they are rows of (whose key columns make each row's <c>k</c>), what the user may do
/// with them, the columns, the references each row has, and the rows that refer to each.
/// </summary>
public sealed record BrowseSchemaDto(string Entity, IReadOnlyList<string>? Key, CapabilitiesDto Capabilities, IReadOnlyList<GridColumnDto> Columns,
                                     IReadOnlyList<GridReferenceDto> References, IReadOnlyList<GridCollectionDto> Collections);

/// <summary>
/// A row: its id (its key as JSON text, the same each time; null when the rows have no key), its key's values
/// <c>k</c>, the values <c>v</c> (by column), and the display values <c>r</c> of the rows it refers to (by reference).
/// Values are sent as <see cref="ValueCodec"/> writes them.
/// </summary>
public sealed record GridRowDto(string? Id, IReadOnlyList<object?>? K, IReadOnlyList<object?> V, IReadOnlyList<object?>? R);

/// <summary>
/// A page of rows: the query that gives them (to open as a query), with its parameters; the columns, when asked
/// for; the rows from <see cref="Offset"/>; whether more follow; and how many rows there are, when asked for and
/// counted in time (null otherwise).
/// </summary>
public sealed record BrowsePageDto(string QueryText, IReadOnlyList<QueryParameterDto> Parameters, string Entity, BrowseSchemaDto? Schema,
                                   IReadOnlyList<GridRowDto> Rows, long Offset, bool HasMore, long? Total);

/// <summary>A crumb of a trail: the first names an entity, each after it a navigation from the row chosen in the one before.</summary>
public sealed record TrailCrumbDto
{
   public string? Entity { get; init; }

   public string? Navigation { get; init; }

   /// <summary>The key of the row chosen in it, if one is (values as in <see cref="BrowseFromDto.Key"/>).</summary>
   public List<object?>? Key { get; init; }
}

public sealed record BrowseTrailRequest([Required] List<TrailCrumbDto> Crumbs);

/// <summary>
/// A crumb as it stands: the entity it reaches, what to call it (the entity, or the navigation), and the display
/// value of the row chosen in it, if it was found. A crumb that can't be followed says why, and is the last.
/// </summary>
public sealed record TrailStepDto(string? Entity, string Label, object? Title, bool? Found, string? Problem);

public sealed record BrowseTrailDto(IReadOnlyList<TrailStepDto> Crumbs);
