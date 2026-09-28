using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Language;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Results;

/// <summary>
/// Where a result value leads: one row (<see cref="RowLink"/>), the rows of a collection (<see cref="CollectionLink"/>)
/// or the rows an aggregate was computed from (<see cref="DrillDownLink"/>). The values a link needs are columns of
/// the same result row, hidden ones included, at the ordinals it names.
/// </summary>
public abstract class ColumnLink
{
   private protected ColumnLink() { }

   /// <summary>The result ordinals whose values the link needs.</summary>
   public abstract IReadOnlyList<int> ValueOrdinals { get; }

   /// <summary>
   /// The query for the rows the link leads to from one result row; null when the row leads nowhere (a null
   /// foreign key). The row's own values are passed as parameters, added to <paramref name="parameters"/>.
   /// </summary>
   public abstract QueryRequest? Query(IReadOnlyList<object?> row, QueryParameters? parameters = null);

   /// <summary>The rows of <paramref name="entity"/> whose columns equal the values at the ordinals.</summary>
   private protected static QueryRequest? Rows(EntityDef entity, IReadOnlyList<ColumnDef> columns, IReadOnlyList<int> ordinals,
                                               IReadOnlyList<object?> row, QueryParameters? parameters, string text)
   {
      ArgumentNullException.ThrowIfNull(row);
      QueryParameters values = Copy(parameters);
      List<string> conditions = [];
      for (int i = 0; i < columns.Count; i++)
      {
         object? value = row[ordinals[i]];
         if (value == null) { return null; }
         string name = FreeName(values, text, i + 1);
         values.Add(name, value, columns[i].Type.AsNonNullable());
         conditions.Add(QueryText.QuoteName(columns[i].Name) + " == $" + name);
      }
      return new QueryRequest(QueryText.Compose(entity.QualifiedName.ToString(), [string.Join(" and ", conditions)])) { Parameters = values };
   }

   private protected static QueryParameters Copy(QueryParameters? parameters)
   {
      QueryParameters copy = new();
      if (parameters == null) { return copy; }
      foreach (QueryParameter parameter in parameters.All) { copy.Add(parameter.Name, parameter.Value, parameter.Type); }
      return copy;
   }

   /// <summary>A parameter name for the n-th key value that neither the query nor the given parameters use.</summary>
   private protected static string FreeName(QueryParameters parameters, string text, int n)
   {
      string name = "key" + n.ToString(CultureInfo.InvariantCulture);
      while (parameters.TryGet(name, out _) || text.Contains("$" + name, StringComparison.OrdinalIgnoreCase)) { name += "_"; }
      return name;
   }

   private protected static string Ordinals(IReadOnlyList<int> ordinals) => "[" + string.Join(", ", ordinals) + "]";
}

/// <summary>
/// One row of <see cref="Target"/>: the row whose <see cref="TargetColumns"/> equal the values at <see cref="KeyOrdinals"/>.
/// Foreign-key columns have one, and so do navigation-valued items such as <c>select(customer)</c>.
/// </summary>
public sealed class RowLink : ColumnLink
{
   internal RowLink(EntityDef target, IReadOnlyList<ColumnDef> targetColumns, IReadOnlyList<int> keyOrdinals, NavigationDef? navigation)
   {
      Target = target;
      TargetColumns = targetColumns;
      KeyOrdinals = keyOrdinals;
      Navigation = navigation;
   }

   public EntityDef Target { get; }

   /// <summary>The target's key (or the unique columns a foreign key refers to).</summary>
   public IReadOnlyList<ColumnDef> TargetColumns { get; }

   public IReadOnlyList<int> KeyOrdinals { get; }

   public override IReadOnlyList<int> ValueOrdinals => KeyOrdinals;

   /// <summary>The navigation that leads to the row, when there is one (<c>customer</c> for <c>customer_id</c>).</summary>
   public NavigationDef? Navigation { get; }

   public override QueryRequest? Query(IReadOnlyList<object?> row, QueryParameters? parameters = null) =>
      Rows(Target, TargetColumns, KeyOrdinals, row, parameters, string.Empty);

   public override string ToString() =>
      $"row {Target.DisplayName}({string.Join(", ", TargetColumns.Select(c => c.Name))}) = {Ordinals(KeyOrdinals)}{(Navigation != null ? " via " + Navigation.Name : string.Empty)}";
}

/// <summary>
/// The rows a collection navigation leads to from a row (<c>orders</c> on customers): the rows of the target whose
/// <see cref="TargetColumns"/> equal the values at <see cref="ValueOrdinals"/>. Aggregates of a collection
/// (<c>orders.count()</c>) have one, and so do the rows of an entity (<see cref="RowIdentity.Related"/>).
/// </summary>
public sealed class CollectionLink : ColumnLink
{
   private readonly IReadOnlyList<int> valueOrdinals;

   internal CollectionLink(NavigationDef navigation, IReadOnlyList<int> valueOrdinals)
   {
      Navigation = navigation;
      this.valueOrdinals = valueOrdinals;
   }

   public NavigationDef Navigation { get; }

   public EntityDef Target => Navigation.Target;

   public IReadOnlyList<ColumnDef> TargetColumns => Navigation.TargetColumns;

   public override IReadOnlyList<int> ValueOrdinals => valueOrdinals;

   public override QueryRequest? Query(IReadOnlyList<object?> row, QueryParameters? parameters = null) =>
      Rows(Target, TargetColumns, ValueOrdinals, row, parameters, string.Empty);

   public override string ToString() =>
      $"rows {Target.DisplayName}({string.Join(", ", TargetColumns.Select(c => c.Name))}) = {Ordinals(ValueOrdinals)} via {Navigation.Name}";
}

/// <summary>One part of a group's key, for drilling down: the expression over the grouped rows and where its value is.</summary>
/// <param name="Expression">Query text of the key over one grouped row, e.g. <c>customer_id</c> or <c>o.customer.id</c>.</param>
/// <param name="Parameter">The lambda parameter the expression uses (<c>o</c>), or null when it uses the implicit row.</param>
/// <param name="Ordinal">The result ordinal of the key's value.</param>
/// <param name="Type">The key's type.</param>
public sealed record DrillDownKey(string Expression, string? Parameter, int Ordinal, ScalarType Type);

/// <summary>
/// The rows an aggregate of a group was computed from: <see cref="QueryText"/> (the rows before groupBy) filtered to
/// the rows whose key parts equal the group's.
/// </summary>
public sealed class DrillDownLink : ColumnLink
{
   internal DrillDownLink(string queryText, IReadOnlyList<DrillDownKey> keys)
   {
      QueryText = queryText;
      Keys = keys;
   }

   /// <summary>The query of the rows that were grouped, standalone (named subtrees included).</summary>
   public string QueryText { get; }

   public IReadOnlyList<DrillDownKey> Keys { get; }

   public override IReadOnlyList<int> ValueOrdinals => Keys.Select(k => k.Ordinal).ToList();

   public override QueryRequest Query(IReadOnlyList<object?> row, QueryParameters? parameters = null)
   {
      ArgumentNullException.ThrowIfNull(row);
      QueryParameters values = Copy(parameters);
      List<string> filters = [];
      // Parameter names must be new to the query and to the key expressions (a key may read $parameters too).
      string used = QueryText + " " + string.Join(" ", Keys.Select(k => k.Expression));
      for (int i = 0; i < Keys.Count; i++)
      {
         DrillDownKey key = Keys[i];
         object? value = row[key.Ordinal];
         string condition;
         if (value == null)
         {
            condition = key.Expression + " == null";
         }
         else
         {
            string name = FreeName(values, used, i + 1);
            values.Add(name, value, key.Type.AsNonNullable());
            condition = key.Expression + " == $" + name;
         }
         filters.Add(key.Parameter == null ? condition : key.Parameter + " => " + condition);
      }
      return new QueryRequest(Language.QueryText.Compose(QueryText, filters)) { Parameters = values };
   }

   public override string ToString()
   {
      StringBuilder text = new("rows of ");
      text.Append(QueryText);
      if (Keys.Count > 0)
      {
         text.Append(" where ").AppendJoin(" and ", Keys.Select(k => $"{(k.Parameter != null ? k.Parameter + " => " : string.Empty)}{k.Expression} == [{k.Ordinal}]"));
      }
      return text.ToString();
   }
}

/// <summary>
/// Where an edit of a result value goes: <see cref="Column"/> of the row of <see cref="Entity"/> whose key is at
/// <see cref="KeyOrdinals"/>. A value has one when it is a table column read along a path that keeps rows apart
/// (filters, projections, joins and many-to-one navigations, but not distinct, grouping or set operations).
/// Whether the column may be changed (identity, computed, read-only sources) is for the caller to decide.
/// </summary>
public sealed class EditTarget
{
   internal EditTarget(TableEntity entity, ColumnDef column, IReadOnlyList<int> keyOrdinals)
   {
      Entity = entity;
      Column = column;
      KeyOrdinals = keyOrdinals;
   }

   public TableEntity Entity { get; }

   public ColumnDef Column { get; }

   /// <summary>The ordinals of the values of the entity's key, in key order.</summary>
   public IReadOnlyList<int> KeyOrdinals { get; }

   public override string ToString() => $"{Column} by {string.Join(", ", Entity.Key!.Columns.Select(c => c.Name))} = [{string.Join(", ", KeyOrdinals)}]";
}

/// <summary>
/// The entity row each result row is, when the query only filters, sorts, pages or extends the rows of one entity
/// that has a key: the key's ordinals, and links to the rows that refer to it (its inverse navigations).
/// </summary>
public sealed class RowIdentity
{
   internal RowIdentity(EntityDef entity, IReadOnlyList<int> keyOrdinals, IReadOnlyList<ColumnLink> related)
   {
      Entity = entity;
      KeyOrdinals = keyOrdinals;
      Related = related;
   }

   public EntityDef Entity { get; }

   public IReadOnlyList<int> KeyOrdinals { get; }

   /// <summary>Collections (<see cref="CollectionLink"/>) and single rows (<see cref="RowLink"/>) that refer to the row.</summary>
   public IReadOnlyList<ColumnLink> Related { get; }

   public override string ToString() =>
      $"{Entity.DisplayName} by {string.Join(", ", Entity.Key!.Columns.Select(c => c.Name))} = [{string.Join(", ", KeyOrdinals)}]";
}
