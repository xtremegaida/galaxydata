using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GalaxyData.Query.Catalog;

namespace GalaxyData.Query.Results;

public enum LineageKind : byte
{
   /// <summary>The value of one physical column, possibly reached through navigations.</summary>
   Direct,

   /// <summary>An expression over one or more columns.</summary>
   Computed,

   /// <summary>An aggregate over the rows of a group.</summary>
   Aggregated,

   /// <summary>A literal or parameter; no column involved.</summary>
   Constant,

   /// <summary>The columns of the inputs of a set operation, by position.</summary>
   Union,

   Unknown,
}

/// <summary>
/// A physical column a result value comes from, and the navigations that lead to it from the query's rows:
/// <c>customer.name</c> on orders is <c>shop.customers.name</c> through <c>customer</c>.
/// </summary>
public sealed class ColumnSource : IEquatable<ColumnSource>
{
   public ColumnSource(ColumnDef column, IReadOnlyList<NavigationDef>? path = null)
   {
      ArgumentNullException.ThrowIfNull(column);
      Column = column;
      Path = path ?? [];
   }

   /// <summary>A column of a table or view; never of a virtual entity, whose columns resolve to the ones they read.</summary>
   public ColumnDef Column { get; }

   public IReadOnlyList<NavigationDef> Path { get; }

   /// <summary>The navigation names, dot separated; empty when the column belongs to the rows themselves.</summary>
   public string PathText => string.Join(".", Path.Select(n => n.Name));

   /// <summary>The same column reached through one more navigation first.</summary>
   public ColumnSource Through(IReadOnlyList<NavigationDef> prefix) => prefix.Count == 0 ? this : new ColumnSource(Column, [.. prefix, .. Path]);

   public bool Equals(ColumnSource? other) =>
      other != null && ReferenceEquals(Column, other.Column) && Path.Count == other.Path.Count && Path.SequenceEqual(other.Path);

   public override bool Equals(object? obj) => Equals(obj as ColumnSource);

   public override int GetHashCode()
   {
      HashCode hash = new();
      hash.Add(Column);
      foreach (NavigationDef navigation in Path) { hash.Add(navigation); }
      return hash.ToHashCode();
   }

   public override string ToString() => Path.Count == 0 ? Column.ToString() : $"{PathText} -> {Column}";
}

/// <summary>Where a result column's values come from; known when the query is prepared, before anything runs.</summary>
public sealed class ColumnLineage
{
   private ColumnLineage(LineageKind kind, IReadOnlyList<ColumnSource> sources, string? expressionText)
   {
      Kind = kind;
      Sources = sources;
      ExpressionText = expressionText;
   }

   public static ColumnLineage Unknown { get; } = new(LineageKind.Unknown, [], null);

   public LineageKind Kind { get; }

   /// <summary>The physical columns involved, each once; exactly one for <see cref="LineageKind.Direct"/>.</summary>
   public IReadOnlyList<ColumnSource> Sources { get; }

   /// <summary>The query text of the expression, for computed and constant values.</summary>
   public string? ExpressionText { get; }

   /// <summary>For a direct column, the navigations that lead to it (<c>customer.region</c>); otherwise null.</summary>
   public string? NavigationPath => Kind == LineageKind.Direct && Sources[0].Path.Count > 0 ? Sources[0].PathText : null;

   public static ColumnLineage Direct(ColumnSource source) => new(LineageKind.Direct, [source], null);

   public static ColumnLineage Constant(string? expressionText) => new(LineageKind.Constant, [], expressionText);

   public static ColumnLineage Computed(IEnumerable<ColumnSource> sources, string? expressionText, LineageKind kind = LineageKind.Computed)
   {
      List<ColumnSource> distinct = [];
      foreach (ColumnSource source in sources)
      {
         if (!distinct.Contains(source)) { distinct.Add(source); }
      }
      return new ColumnLineage(kind, distinct, expressionText);
   }

   /// <summary>The same lineage seen from rows that reach these through <paramref name="prefix"/>.</summary>
   public ColumnLineage Through(IReadOnlyList<NavigationDef> prefix) =>
      prefix.Count == 0 || Sources.Count == 0 ? this : new ColumnLineage(Kind, Sources.Select(s => s.Through(prefix)).ToList(), ExpressionText);

   public override string ToString()
   {
      StringBuilder text = new();
      text.Append(Kind.ToString().ToLowerInvariant());
      if (ExpressionText != null) { text.Append(" (").Append(ExpressionText).Append(')'); }
      if (Sources.Count > 0)
      {
         text.Append(Kind == LineageKind.Direct ? " " : " from ").AppendJoin(", ", Sources);
      }
      return text.ToString();
   }
}
