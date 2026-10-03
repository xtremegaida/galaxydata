using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace GalaxyData.Web.Browse;

/// <summary>How a filter condition compares a column with its value.</summary>
public enum GridOp
{
   /// <summary>Equal (text exactly, as the database compares it).</summary>
   Eq,

   /// <summary>Not equal, or null.</summary>
   Ne,
   Lt,
   Le,
   Gt,
   Ge,

   /// <summary>From <c>value</c> to <c>valueTo</c>, both included.</summary>
   Between,

   /// <summary>Text that has the value in it, ignoring case.</summary>
   Contains,

   /// <summary>Text that hasn't the value in it, ignoring case, or null.</summary>
   NotContains,

   /// <summary>Text that starts with the value, ignoring case.</summary>
   StartsWith,

   /// <summary>Text that ends with the value, ignoring case.</summary>
   EndsWith,

   /// <summary>Null (or, for text, empty).</summary>
   Blank,

   /// <summary>Not null (and, for text, not empty).</summary>
   NotBlank,
}

/// <summary>
/// A condition on a column: its value (and, for <see cref="GridOp.Between"/>, the value up to) as rows' values are
/// sent. Values are declared <c>object</c>, which JSON reads as <see cref="JsonElement"/> (see <see cref="BrowseFromDto"/>).
/// </summary>
public sealed record GridConditionDto(GridOp Op, object? Value = null, object? ValueTo = null);

/// <summary>Conditions on one column, all of which (<c>and</c>) or any of which (<c>or</c>) a row must meet.</summary>
public sealed record GridFilterDto([Required] string Column, [Required] List<GridConditionDto> Conditions, bool Any = false);

public sealed record GridSortDto([Required] string Column, bool Desc = false);

/// <summary>
/// What a grid shows of a query's rows: rows meeting every filter and the where expression (the language's, over
/// the rows), sorted, from <see cref="Offset"/>, at most <see cref="Limit"/>.
/// </summary>
public sealed record GridStateDto
{
   public List<GridFilterDto>? Filters { get; init; }

   [StringLength(10_000)]
   public string? Where { get; init; }

   public List<GridSortDto>? Sort { get; init; }

   [Range(0, long.MaxValue)]
   public long Offset { get; init; }

   /// <summary>At most <c>Query:MaxPageSize</c>; 100 (or that, when it is less) when not given.</summary>
   [Range(1, 100_000)]
   public int? Limit { get; init; }
}
