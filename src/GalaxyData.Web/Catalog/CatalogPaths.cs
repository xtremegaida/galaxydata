using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Catalog;

namespace GalaxyData.Web.Catalog;

/// <summary>A step of a way between entities: a navigation, from one entity to another, to one row or many.</summary>
public sealed record CatalogPathStepDto(string Navigation, string From, string To, bool IsCollection);

public sealed record CatalogPathDto(IReadOnlyList<CatalogPathStepDto> Steps);

/// <summary>Ways between two entities' rows through navigations, for linking a dashboard's sources.</summary>
public static class CatalogPaths
{
   public const int MaxPaths = 20;

   /// <summary>
   /// The ways from <paramref name="from"/> to <paramref name="to"/> (or the base <paramref name="to"/> filters, if it is
   /// a virtual entity), shortest first, none visiting an entity twice; navigations the overlay hides aren't offered.
   /// </summary>
   public static List<List<NavigationDef>> Find(EntityDef from, EntityDef to, int depth, int max)
   {
      ArgumentNullException.ThrowIfNull(from);
      ArgumentNullException.ThrowIfNull(to);
      EntityDef? baseEntity = (to as VirtualEntity)?.BaseEntity;
      List<List<NavigationDef>> found = [];
      List<(EntityDef At, List<NavigationDef> Path)> level = [(from, [])];
      for (int length = 1; length <= depth && found.Count < max && level.Count > 0; length++)
      {
         List<(EntityDef, List<NavigationDef>)> next = [];
         foreach ((EntityDef at, List<NavigationDef> path) in level)
         {
            foreach (NavigationDef navigation in at.Navigations.Concat(at.InheritedNavigations).Where(n => !n.Hidden).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            {
               EntityDef target = navigation.Target;
               if (target == from || path.Any(p => p.Target == target)) { continue; }
               List<NavigationDef> longer = [.. path, navigation];
               if (target == to || target == baseEntity)
               {
                  if (found.Count < max) { found.Add(longer); }
                  continue;
               }
               next.Add((target, longer));
            }
         }
         level = next;
      }
      return found;
   }
}
