using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GalaxyData.Query.Catalog;

/// <summary>
/// Names navigations by convention. A forward navigation takes its foreign-key column name without the key
/// suffix (<c>customer_id</c> → <c>customer</c>), or the target table's name for composite keys. An inverse takes
/// the dependent table's name (<c>orders</c>), or <c>orders_by_ship_address</c> when several relations come from
/// the same table or the relation refers to its own table. A name that collides with a column or an earlier
/// navigation falls back to the constraint name, then gets a numeric suffix.
/// </summary>
internal static class NavigationNaming
{
   public static string? StripKeySuffix(string column, NavigationNamingOptions options)
   {
      foreach (string suffix in options.SeparatedSuffixes)
      {
         if (column.Length > suffix.Length && column.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
         {
            string stripped = column[..^suffix.Length].TrimEnd('_');
            return stripped.Length > 0 ? stripped : null;
         }
      }
      foreach (string suffix in options.CamelSuffixes)
      {
         if (column.Length > suffix.Length && column.EndsWith(suffix, StringComparison.Ordinal))
         {
            char before = column[^(suffix.Length + 1)];
            if (char.IsLower(before) || char.IsDigit(before)) { return column[..^suffix.Length]; }
         }
      }
      return null;
   }

   public static string ForwardBase(RelationDef relation, NavigationNamingOptions options)
   {
      if (relation.FromColumns.Count == 1 && StripKeySuffix(relation.FromColumns[0].Name, options) is { } stripped)
      {
         return stripped;
      }
      return relation.To.Name;
   }

   public static string InverseBase(RelationDef relation, IEnumerable<RelationDef> relationsToPrincipal)
   {
      bool shared = ReferenceEquals(relation.From, relation.To) ||
         relationsToPrincipal.Count(r => ReferenceEquals(r.From, relation.From)) > 1;
      return shared ? $"{relation.From.Name}_by_{relation.Forward.Name}" : relation.From.Name;
   }

   /// <summary>Picks a name on <paramref name="owner"/> that no column or navigation uses yet.</summary>
   public static string Unique(EntityDef owner, string preferred, string? fallback)
   {
      if (!owner.HasMemberNamed(preferred)) { return preferred; }
      if (!string.IsNullOrEmpty(fallback) && !owner.HasMemberNamed(fallback)) { return fallback; }
      for (int n = 2; ; n++)
      {
         string candidate = preferred + "_" + n.ToString(CultureInfo.InvariantCulture);
         if (!owner.HasMemberNamed(candidate)) { return candidate; }
      }
   }
}
