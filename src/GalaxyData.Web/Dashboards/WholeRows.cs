using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Types;

namespace GalaxyData.Web.Dashboards;

/// <summary>A column of rows fetched: its name in the query, and its type.</summary>
public sealed record FetchedColumn(string Name, ScalarType Type);

/// <summary>Rows as a query gives them, with their columns.</summary>
public sealed record Fetched(List<object?[]> Rows, IReadOnlyList<FetchedColumn> Columns)
{
   /// <summary>The column's position in the rows; -1 when there is none of the name.</summary>
   public int IndexOf(string name)
   {
      for (int i = 0; i < Columns.Count; i++)
      {
         if (string.Equals(Columns[i].Name, name, StringComparison.Ordinal)) { return i; }
      }
      return -1;
   }
}

/// <summary>A chart's rows as each of its queries gives them: those queries run, or what they would give worked out from its rows read whole.</summary>
internal interface IChartRows
{
   /// <summary>The categories a series' bars are limited to: <c>d0</c>, <c>m0</c>; one more than the limit, to tell there are more.</summary>
   Task<Fetched> CategoriesAsync(CancellationToken cancellationToken);

   /// <summary>The series ranked, of the categories found: <c>s0</c>, <c>m0</c>, the largest first.</summary>
   Task<Fetched> RankingAsync(PlanFound found, CancellationToken cancellationToken);

   /// <summary>The chart's rows, of what was found.</summary>
   Task<Fetched> MainAsync(PlanFound found, CancellationToken cancellationToken);

   /// <summary>The rest ("Other") by the dimension: <c>d0</c>, <c>m0</c>, of the series after those ranked up to the last shown.</summary>
   Task<Fetched> RestAsync(PlanFound found, CancellationToken cancellationToken);

   /// <summary>The measure over every row: one row of one value.</summary>
   Task<Fetched> TotalAsync(CancellationToken cancellationToken);
}

/// <summary>A value of a chart's rows read whole can't be added up here (of a type this doesn't add, or too large): its queries run instead.</summary>
internal sealed class WholeRowsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// What a chart's queries give, worked out in memory from its rows read whole (<see cref="WholePlan"/>: every group of
/// its dimension and series, in the database's order of them), each as its query would give it: the same groups, kept
/// by the same conditions, their measure added up to coarser groups' as the plan says, sorted and cut as the query
/// would be. Ties are broken in the rows' order (the database's order of the dimension), or, for series ranked, by
/// their values: text by its characters' codes, guids as their text (as the query's <c>toString</c> does). Where the
/// database orders text otherwise (a collation that ignores case), only equal measures at the edge of what is shown
/// may break another way.
/// </summary>
internal sealed class WholeRows : IChartRows
{
   private readonly WidgetPlan plan;
   private readonly WholePlan whole;
   private readonly Fetched rows;
   private readonly int d;
   private readonly int s;
   private readonly int m;

   public WholeRows(WidgetPlan plan, Fetched rows)
   {
      this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
      whole = plan.Whole ?? throw new ArgumentException("The plan reads no whole rows", nameof(plan));
      this.rows = rows ?? throw new ArgumentNullException(nameof(rows));
      d = rows.IndexOf("d0");
      s = rows.IndexOf("s0");
      m = rows.IndexOf("m0");
      if (d < 0 || m < 0 || (whole.Shape != WholeShape.Slices && s < 0)) { throw new ArgumentException("The whole rows have no d0, s0 or m0", nameof(rows)); }
   }

   public Task<Fetched> CategoriesAsync(CancellationToken cancellationToken)
   {
      WidgetSort sort = whole.Categories ?? throw new InvalidOperationException("Only bars by a series find their categories first");
      List<(object? Key, object? Measure)> groups = Group(rows.Rows, d);
      IEnumerable<(object? Key, object? Measure)> sorted = sort.By == SortTarget.Dimension
         ? (sort.Descending ? Enumerable.Reverse(groups) : groups)
         : sort.Descending ? groups.OrderByDescending(g => g.Measure, Measures) : groups.OrderBy(g => g.Measure, Measures);
      return Rows([.. sorted.Take(plan.Limit + 1).Select(g => new[] { g.Key, g.Measure })], d, m);
   }

   public Task<Fetched> RankingAsync(PlanFound found, CancellationToken cancellationToken)
   {
      SeriesPlan series = plan.Series ?? throw new InvalidOperationException("The chart isn't limited to its largest series");
      ScalarType type = rows.Columns[s].Type;
      List<(object? Key, object? Measure)> ranked = [.. Group(Of(found.Categories), s)
         .OrderByDescending(g => g.Measure, Measures)
         .ThenBy(g => g.Key, Comparer<object?>.Create((a, b) => Tie(a, b, type)))
         .Take((plan.Step?.Skip ?? 0) + series.Limit + 1)];
      return Rows([.. ranked.Select(g => new[] { g.Key, g.Measure })], s, m);
   }

   public Task<Fetched> MainAsync(PlanFound found, CancellationToken cancellationToken)
   {
      if (whole.Shape == WholeShape.Slices)
      {
         // Largest first; ties in the rows' order, which is the dimension's (guids, which the database doesn't sort, by their text).
         ScalarType type = rows.Columns[d].Type;
         IEnumerable<object?[]> slices = rows.Rows.OrderByDescending(r => r[m], Measures);
         if (type.Kind == ScalarKind.Guid) { slices = ((IOrderedEnumerable<object?[]>)slices).ThenBy(r => r[d], Comparer<object?>.Create((a, b) => Tie(a, b, type))); }
         return Task.FromResult(rows with { Rows = [.. slices.Take(whole.MainTake)] });
      }
      IEnumerable<object?[]> kept = Of(found.Categories);
      if (found.Series is { } series)
      {
         HashSet<Key> shown = Keys(series.Shown);
         kept = kept.Where(r => shown.Contains(new Key(r[s])));
      }
      return Task.FromResult(rows with { Rows = [.. kept.Take(whole.MainTake)] });
   }

   public Task<Fetched> RestAsync(PlanFound found, CancellationToken cancellationToken)
   {
      SeriesValues series = found.Series ?? throw new InvalidOperationException("A rest is of the series after those ranked");
      HashSet<Key> before = Keys(series.Before);
      List<(object? Key, object? Measure)> rest = Group(Of(found.Categories).Where(r => !before.Contains(new Key(r[s]))), d);
      IEnumerable<(object? Key, object? Measure)> cut = whole.RestTake is { } take ? rest.Take(take) : rest;
      return Rows([.. cut.Select(g => new[] { g.Key, g.Measure })], d, m);
   }

   public Task<Fetched> TotalAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new Fetched([[Add(rows.Rows.Select(r => r[m]))]], [rows.Columns[m]]));

   /// <summary>The rows of the categories found (all, when none were).</summary>
   private IEnumerable<object?[]> Of(CategoryValues? categories)
   {
      if (categories == null) { return rows.Rows; }
      HashSet<Key> kept = Keys(categories.Values);
      return rows.Rows.Where(r => kept.Contains(new Key(r[d])));
   }

   /// <summary>The rows grouped by a column's value, in the order each value is first met, their measures added up.</summary>
   private List<(object? Key, object? Measure)> Group(IEnumerable<object?[]> of, int column)
   {
      Dictionary<Key, int> index = [];
      List<(object? Key, List<object?> Measures)> groups = [];
      foreach (object?[] row in of)
      {
         Key key = new(row[column]);
         if (!index.TryGetValue(key, out int at))
         {
            index[key] = at = groups.Count;
            groups.Add((row[column], []));
         }
         groups[at].Measures.Add(row[m]);
      }
      return [.. groups.Select(g => (g.Key, Add(g.Measures)))];
   }

   private Task<Fetched> Rows(List<object?[]> list, int key, int measure) =>
      Task.FromResult(new Fetched(list, [rows.Columns[key], rows.Columns[measure]]));

   /// <summary>Groups' measures added up as the plan says: summed, or the least or greatest; nulls left out, as aggregates leave them; null when there is no value.</summary>
   private object? Add(IEnumerable<object?> measures)
   {
      object? result = null;
      foreach (object? value in measures)
      {
         if (value == null) { continue; }
         result = result == null ? value : whole.Combine switch
         {
            Combine.Sum => Sum(result, value),
            Combine.Min => Order(value, result) < 0 ? value : result,
            _ => Order(value, result) > 0 ? value : result,
         };
      }
      return result;
   }

   private static object Sum(object a, object b)
   {
      if (a.GetType() != b.GetType()) { throw new WholeRowsException($"{a.GetType().Name} and {b.GetType().Name} values aren't added up here"); }
      try
      {
         return a switch
         {
            long x => checked(x + (long)b),
            decimal x => x + (decimal)b,
            double x => x + (double)b,
            ulong x => checked(x + (ulong)b),
            int x => checked(x + (int)b),
            uint x => checked(x + (uint)b),
            short x => checked((short)(x + (short)b)),
            ushort x => checked((ushort)(x + (ushort)b)),
            byte x => checked((byte)(x + (byte)b)),
            sbyte x => checked((sbyte)(x + (sbyte)b)),
            float x => x + (float)b,
            Int128 x => checked(x + (Int128)b),
            UInt128 x => checked(x + (UInt128)b),
            BigInteger x => x + (BigInteger)b,
            _ => throw new WholeRowsException($"{a.GetType().Name} values aren't added up here"),
         };
      }
      catch (OverflowException e)
      {
         throw new WholeRowsException("A sum is too large to add up here", e);
      }
   }

   /// <summary>Measures in the order the database sorts them: nulls first (smallest), numbers by their value whatever their type, others as they compare.</summary>
   private static readonly Comparer<object?> Measures = Comparer<object?>.Create(Order);

   private static int Order(object? a, object? b)
   {
      if (a == null || b == null) { return a == null ? (b == null ? 0 : -1) : 1; }
      if (a is string x && b is string y) { return string.CompareOrdinal(x, y); }
      if (a.GetType() == b.GetType() && a is IComparable comparable) { return comparable.CompareTo(b); }
      if (IsNumber(a) && IsNumber(b))
      {
         try
         {
            return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
         }
         catch (OverflowException)
         {
            return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
         }
      }
      throw new WholeRowsException($"{a.GetType().Name} and {b.GetType().Name} values aren't compared here");
   }

   private static bool IsNumber(object value) => value is long or decimal or double or ulong or int or uint or short or ushort or byte or sbyte or float;

   /// <summary>
   /// Values in the order a ranking's ties are broken: nulls first, guids by their text (as <c>toString</c> writes them),
   /// text by its characters' codes, others as they compare.
   /// </summary>
   private static int Tie(object? a, object? b, ScalarType type)
   {
      if (a == null || b == null) { return a == null ? (b == null ? 0 : -1) : 1; }
      if (type.Kind == ScalarKind.Guid || a is Guid || b is Guid)
      {
         return string.CompareOrdinal(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
      }
      if (a is string x && b is string y) { return string.CompareOrdinal(x, y); }
      return Order(a, b);
   }

   private static HashSet<Key> Keys(IEnumerable<object?> values) => [.. values.Select(v => new Key(v))];

   /// <summary>A value as a key: equal when the values are (nulls to each other).</summary>
   private readonly record struct Key(object? Value);
}
