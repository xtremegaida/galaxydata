using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Language;

namespace GalaxyData.Web.Browse;

/// <summary>A query of an entity's rows, with its parameters.</summary>
public sealed record EntityQuery(EntityDef Entity, string Text, QueryParameters Parameters);

/// <summary>
/// Where a navigation leads from one row: a query of the target entity's own rows, filtered, so they stay that
/// entity's rows (and can be changed as its rows can). When the row's key gives the values the navigation matches on
/// (<c>orders</c> of a customer: <c>shop.orders.where(customer_id == $key1)</c>), they are parameters; otherwise
/// (<c>customer</c> of an order) the target's rows are those the row matches:
/// <c>shop.customers.where(t => shop.orders.any(o => o.id == $key1 and o.customer_id == t.id))</c>.
/// </summary>
public static class NavigationResolver
{
   /// <summary>The navigation of <paramref name="entity"/> named so (its own or one it has of its base entity); null when there is none, or more than one.</summary>
   public static NavigationDef? Find(EntityDef entity, string name, out string? problem)
   {
      ArgumentNullException.ThrowIfNull(entity);
      ArgumentNullException.ThrowIfNull(name);
      NameMatch<NavigationDef> match = entity.FindNavigation(name);
      problem = null;
      if (match.IsFound && !match.Item!.Hidden) { return match.Item; }
      if (match.Status == MatchStatus.Ambiguous)
      {
         problem = $"{entity.DisplayName} has more than one navigation named '{name}' but for case: {string.Join(", ", match.Candidates.Select(n => n.Name))}";
         return null;
      }
      NavigationDef? inherited = entity.InheritedNavigations.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal))
         ?? entity.InheritedNavigations.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
      if (inherited is { Hidden: false }) { return inherited; }
      problem = $"{entity.DisplayName} has no navigation '{name}'";
      return null;
   }

   /// <summary>The key of a row of <paramref name="entity"/>, as sent; null, with <paramref name="problem"/>, when it isn't one.</summary>
   public static IReadOnlyList<object?>? Key(EntityDef entity, IReadOnlyList<object?> key, out string? problem)
   {
      ArgumentNullException.ThrowIfNull(entity);
      ArgumentNullException.ThrowIfNull(key);
      problem = null;
      if (entity.Key is not { } own)
      {
         problem = $"{entity.DisplayName} has no key, so its rows can't be told apart";
         return null;
      }
      if (key.Count != own.Columns.Count)
      {
         problem = $"The key of {entity.DisplayName} is {own.Columns.Count} value{(own.Columns.Count == 1 ? string.Empty : "s")} ({string.Join(", ", own.Columns.Select(c => c.Name))})";
         return null;
      }
      List<object?> values = [];
      for (int i = 0; i < key.Count; i++)
      {
         try
         {
            values.Add(ValueCodec.Decode(ValueCodec.Json(key[i]), own.Columns[i].Type) ?? throw new ValueFormatException($"null isn't a value of '{own.Columns[i].Name}', a key"));
         }
         catch (ValueFormatException e)
         {
            problem = $"'{own.Columns[i].Name}': {e.Message}";
            return null;
         }
      }
      return values;
   }

   /// <summary>The rows <paramref name="navigation"/> leads to from the row of <paramref name="from"/> whose key is <paramref name="key"/>.</summary>
   public static EntityQuery Resolve(EntityDef from, IReadOnlyList<object?> key, NavigationDef navigation)
   {
      ArgumentNullException.ThrowIfNull(from);
      ArgumentNullException.ThrowIfNull(key);
      ArgumentNullException.ThrowIfNull(navigation);
      KeyDef own = from.Key ?? throw new ArgumentException($"{from.DisplayName} has no key", nameof(from));
      EntityDef target = navigation.Target;
      QueryParameters parameters = new();
      string Parameter(int i)
      {
         string name = "key" + (i + 1).ToString(CultureInfo.InvariantCulture);
         parameters.Add(name, key[i], own.Columns[i].Type.AsNonNullable());
         return "$" + name;
      }
      IReadOnlyList<ColumnDef> owner = navigation.OwnerColumns;
      if (owner.All(c => own.Columns.Contains(c)))
      {
         List<string> conditions = [];
         for (int i = 0; i < owner.Count; i++)
         {
            conditions.Add($"{QueryText.QuoteName(navigation.TargetColumns[i].Name)} == {Parameter(IndexOf(own, owner[i]))}");
         }
         return new EntityQuery(target, QueryText.Compose(target.DisplayName, [string.Join(" and ", conditions)]), parameters);
      }
      // The target's row is named apart from the source the entity is in (a source 't' would be the row).
      string row = "t";
      while (string.Equals(row, from.QualifiedName.Parts[0], StringComparison.OrdinalIgnoreCase)) { row += "_"; }
      List<string> matches = [];
      for (int i = 0; i < own.Columns.Count; i++) { matches.Add($"{Member("o", own.Columns[i].Name)} == {Parameter(i)}"); }
      for (int i = 0; i < owner.Count; i++) { matches.Add($"{Member("o", owner[i].Name)} == {Member(row, navigation.TargetColumns[i].Name)}"); }
      string filter = $"{row} => {from.DisplayName}.any(o => {string.Join(" and ", matches)})";
      return new EntityQuery(target, QueryText.Compose(target.DisplayName, [filter]), parameters);
   }

   private static int IndexOf(KeyDef key, ColumnDef column)
   {
      for (int i = 0; i < key.Columns.Count; i++)
      {
         if (ReferenceEquals(key.Columns[i], column)) { return i; }
      }
      throw new ArgumentException($"'{column.Name}' isn't part of the key", nameof(column));
   }

   private static string Member(string row, string name) => QueryText.AppendMember(new StringBuilder(row), name).ToString();
}
