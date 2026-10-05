using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Explain;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Problems;

namespace GalaxyData.Web.Queries;

public static class QueryLimits
{
   /// <summary>The longest query text kept (the engine's longest, by default).</summary>
   public const int MaxTextLength = 100_000;
}

/// <summary>
/// A value for a <c>$name</c> parameter (the name with or without the <c>$</c>). With a <see cref="Type"/> (as the
/// language writes types: <c>int64</c>, <c>date</c>, <c>decimal(12,2)</c>), the value is read as rows' values are
/// sent; without one, a JSON value is a boolean, a whole number (<c>int64</c>), another number (<c>decimal</c>), or
/// text, which adapts to what it meets (a date column takes <c>"2026-03-01"</c>); null takes the type it meets.
/// </summary>
/// <remarks>The value is declared <c>object</c> (a <see cref="System.Text.Json.JsonElement"/>): request validation would look into one declared so.</remarks>
public sealed record QueryParameterInput([Required] string Name, string? Type = null, object? Value = null);

/// <summary>A query, and values for its parameters.</summary>
public sealed record QueryTextRequest([Required] string Text, List<QueryParameterInput>? Parameters = null);

/// <summary>A page of a query's rows, as a grid shows them (filtered, sorted, paged), with its columns when asked, and counted when asked.</summary>
public sealed record QueryPageRequest([Required] string Text, List<QueryParameterInput>? Parameters = null, GridStateDto? Grid = null, bool IncludeSchema = true,
                                      bool IncludeCount = false);

/// <summary>A query to explain; with <see cref="Grid"/>, the page the grid would fetch. <see cref="Verbose"/> adds the plan after each phase of the optimizer.</summary>
public sealed record QueryExplainRequest([Required] string Text, List<QueryParameterInput>? Parameters = null, GridStateDto? Grid = null, bool Verbose = false);

/// <summary>
/// Where a value of a page's row leads: the link of the column at <see cref="Column"/>, or the rows that refer to
/// the row (<see cref="Related"/>, an index into <see cref="RowIdentityDto.Related"/>). <see cref="Text"/> and
/// <see cref="Parameters"/> are the page's (its <c>queryText</c> and <c>parameters</c>), and <see cref="Row"/> its
/// values (<c>v</c>), as sent.
/// </summary>
/// <remarks>
/// With <see cref="CatalogVersion"/> (the page's <c>X-Catalog-Version</c>), a catalog built since is a 409: the
/// columns may have moved.
/// </remarks>
public sealed record QueryLinkRequest([Required] string Text, [Required] List<object?> Row, List<QueryParameterInput>? Parameters = null, int? Column = null,
                                      int? Related = null, string? CatalogVersion = null);

/// <summary>
/// A parameter the text uses, whether a value was given for it, and the type the query takes for it (as far as the
/// query was checked), to ask for a value of: the type it takes without a value, whatever value was given.
/// </summary>
public sealed record UsedParameterDto(string Name, bool Given, string? Type);

/// <summary>A column of a query's result, as validating it finds it.</summary>
public sealed record QueryColumnDto(string Name, TypeDto Type);

/// <summary>
/// What is wrong with a query, placed in its text, and what it uses and gives: its parameters, and its columns when it
/// would run. Parameters without a value are taken as null to check the rest; where a null doesn't fit (<c>-$n</c>),
/// the problem is told as <c>info</c>, not an error, and the query is checked no further: <see cref="Complete"/> says
/// whether it was checked to the end. <see cref="Success"/> says there is no error.
/// </summary>
public sealed record QueryValidationDto(bool Success, bool Complete, IReadOnlyList<DiagnosticDto> Diagnostics, IReadOnlyList<UsedParameterDto> Parameters,
                                        IReadOnlyList<QueryColumnDto>? Columns);

public enum LinkKind
{
   /// <summary>One row of <c>target</c> (a foreign key's, a navigation's).</summary>
   Row,

   /// <summary>The rows of a collection (<c>orders.count()</c>, the orders of a customer).</summary>
   Collection,

   /// <summary>The rows an aggregate of a group was worked out from.</summary>
   DrillDown,
}

/// <summary>Where a column's values lead, and the values (by ordinal, hidden columns included) the link needs.</summary>
public sealed record ColumnLinkDto(LinkKind Kind, IReadOnlyList<int> Ordinals, string? Target, string? Navigation, Multiplicity? Multiplicity);

/// <summary>Where a change of a column's value goes: the column of the row of a table whose key is at <see cref="KeyOrdinals"/>, and whether the user may change it.</summary>
public sealed record EditTargetDto(string Entity, string Column, IReadOnlyList<int> KeyOrdinals, bool CanUpdate, string? ReadOnlyReason);

/// <summary>A column of the rows: hidden ones (keys, for links and edits) are sent too, and not shown.</summary>
public sealed record ResultColumnDto(string Name, int Ordinal, TypeDto Type, bool Hidden, LineageDto Lineage, ColumnLinkDto? Link, EditTargetDto? EditTarget);

/// <summary>The entity whose rows the rows are, when they are one entity's: its key's ordinals, what refers to each row, and what the user may do with them.</summary>
public sealed record RowIdentityDto(string Entity, IReadOnlyList<int> KeyOrdinals, IReadOnlyList<ColumnLinkDto> Related, CapabilitiesDto Capabilities);

/// <summary>The columns of a query's rows, every one in ordinal order, and what the rows are.</summary>
public sealed record ResultSchemaDto(IReadOnlyList<ResultColumnDto> Columns, RowIdentityDto? RowIdentity);

/// <summary>A row: its id (its key as JSON, when the rows are an entity's), and every value <c>v</c> by ordinal, as <see cref="ValueCodec"/> writes them.</summary>
public sealed record ResultRowDto(string? Id, IReadOnlyList<object?> V);

public sealed record FragmentStatsDto(string Source, string Table, long Rows, double ElapsedMs, FetchStrategy Strategy, int Keys, int Batches);

/// <summary>What running the page took: its time, the rows fetched from the sources, keys sent to them, and each fragment's part.</summary>
public sealed record QueryStatsDto(double ElapsedMs, long FetchedRows, long KeysSent, IReadOnlyList<FragmentStatsDto> Fragments);

/// <summary>
/// A page of a query's rows: the query that gave them (the grid's composed onto it) and its parameters; the columns,
/// when asked; the rows from <see cref="Offset"/>; whether more follow; how many there are, when asked and counted in
/// time; what running it took; and the query's warnings.
/// </summary>
public sealed record QueryPageDto(string QueryText, IReadOnlyList<QueryParameterDto> Parameters, ResultSchemaDto? Schema, IReadOnlyList<ResultRowDto> Rows,
                                  long Offset, bool HasMore, long? Total, QueryStatsDto Stats, IReadOnlyList<DiagnosticDto> Warnings);

/// <summary>A node of a plan: its operator, what it does, where it runs, its columns, how many rows it is thought to give, and its inputs (by id).</summary>
public sealed record ExplainNodeDto(int Id, string Operator, string? Detail, string? Site, IReadOnlyList<string> Columns, long? EstimatedRows, IReadOnlyList<int> Inputs,
                                    IReadOnlyList<ExplainSubqueryDto> Subqueries);

/// <summary>A subquery of a node, and its plan's root (by id).</summary>
public sealed record ExplainSubqueryDto(int Number, string Kind, int Plan);

/// <summary>SQL a source runs: how its rows are fetched, and the statement for each batch of keys when they are fetched by the keys of another.</summary>
public sealed record ExplainFragmentDto(string Source, string Dialect, string Sql, IReadOnlyList<ExplainParameter> Parameters, string Strategy, string? Table,
                                        long? EstimatedRows, string? BindJoinTemplate, IReadOnlyList<ExplainParameter> BindJoinParameters);

/// <summary>
/// How a query would run, without running it: its result's columns; the plan, as a list of nodes (its root
/// <see cref="Plan"/>, inputs by id); the SQL each source runs; the merge engine's SQL; the plan after each phase of
/// the optimizer, when verbose; and all of it as text (<see cref="Text"/>), as <c>gdq explain</c> writes it.
/// </summary>
public sealed record QueryExplainDto(string QueryText, IReadOnlyList<QueryParameterDto> Parameters, string Summary, IReadOnlyList<DiagnosticDto> Diagnostics,
                                     ResultSchemaDto? Schema, int? Plan, IReadOnlyList<ExplainNodeDto> Nodes, IReadOnlyList<ExplainFragmentDto> Fragments,
                                     string? MergeSql, IReadOnlyList<ExplainParameter> MergeParameters, IReadOnlyList<ExplainPhase>? Phases, string Text);

/// <summary>
/// Where a link leads: the query of its rows (null when it leads nowhere, as a null foreign key), and, when they are
/// rows a grid can browse, the source to browse (an entity, or a navigation from a row) and the key of the row to choose.
/// </summary>
public sealed record QueryLinkDto(string? QueryText, IReadOnlyList<QueryParameterDto> Parameters, BrowseSourceDto? Browse, IReadOnlyList<object?>? Key);
