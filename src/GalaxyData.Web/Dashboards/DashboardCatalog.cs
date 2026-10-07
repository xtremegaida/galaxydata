using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Language;
using GalaxyData.Query.Types;

namespace GalaxyData.Web.Dashboards;

/// <summary>A dashboard's source as the catalog has it: its entity, and its path as queries write it.</summary>
public sealed record ResolvedSource(string Id, string Label, EntityDef Entity, string Path);

/// <summary>
/// A field as the catalog has it: the forward navigations to its column from the source's rows, and the column (of
/// its type). Written as a member of a row (<c>r0.customer.city</c>), or of the implicit row (<c>customer.city</c>).
/// </summary>
public sealed record ResolvedField(ResolvedSource Source, IReadOnlyList<NavigationDef> Steps, ColumnDef Column)
{
   public ScalarType Type => Column.Type;

   /// <summary>As written in the definition and in messages: <c>customer.city</c>.</summary>
   public string Name => Member(null);

   /// <summary>The field of <paramref name="row"/> (a lambda's parameter), or of the implicit row when null.</summary>
   public string Member(string? row)
   {
      StringBuilder text = new();
      IEnumerable<string> names = Steps.Select(s => s.Name).Append(Column.Name);
      if (row == null)
      {
         string[] all = [.. names];
         text.Append(QueryText.QuoteName(all[0]));
         foreach (string name in all.Skip(1)) { QueryText.AppendMember(text, name); }
         return text.ToString();
      }
      text.Append(row);
      foreach (string name in names) { QueryText.AppendMember(text, name); }
      return text.ToString();
   }
}

/// <summary>
/// One way along a link between two sources: the navigations from one's rows to the other's. When they end at an
/// entity other than the destination's (a virtual entity's base), the last is followed by matching the destination's
/// rows on the navigation's columns (<see cref="Correlated"/>).
/// </summary>
public sealed record LinkHop(ResolvedSource From, ResolvedSource To, IReadOnlyList<NavigationDef> Steps, bool Correlated);

/// <summary>
/// A definition as the catalog has it now: its sources' entities, its fields, its links both ways, and what doesn't
/// fit (<see cref="Issues"/>). Fields are resolved as they are asked for, and their problems said once.
/// </summary>
public sealed class DashboardCatalog
{
   private readonly Dictionary<string, ResolvedSource?> sources = new(StringComparer.Ordinal);
   private readonly Dictionary<string, List<LinkHop>> hops = new(StringComparer.Ordinal);
   private readonly Dictionary<(string Source, string Path), ResolvedField?> fields = [];
   private readonly List<DashboardIssue> issues = [];
   private readonly HashSet<(string? Widget, string? Field)> said = [];

   private DashboardCatalog(DashboardDefinition definition, QueryCatalog catalog)
   {
      Definition = definition;
      Catalog = catalog;
   }

   public DashboardDefinition Definition { get; }

   public QueryCatalog Catalog { get; }

   public IReadOnlyList<DashboardIssue> Issues => issues;

   public static DashboardCatalog Resolve(DashboardDefinition definition, QueryCatalog catalog)
   {
      ArgumentNullException.ThrowIfNull(definition);
      ArgumentNullException.ThrowIfNull(catalog);
      DashboardCatalog resolved = new(definition, catalog);
      resolved.ResolveSources();
      resolved.ResolveLinks();
      return resolved;
   }

   public ResolvedSource? Source(string id) => sources.GetValueOrDefault(id);

   /// <summary>The ways from a source to the sources linked with it.</summary>
   public IReadOnlyList<LinkHop> HopsFrom(string source) => hops.TryGetValue(source, out List<LinkHop>? found) ? found : [];

   public void Issue(IssueSeverity severity, string message, string? widget, string? field)
   {
      if (said.Add((widget, field + "|" + message))) { issues.Add(new DashboardIssue(severity, message, widget, field)); }
   }

   /// <summary>
   /// A field of a source's rows; null (an error about <paramref name="widget"/>, at <paramref name="path"/>) when the
   /// source or a step or the column isn't there, or a step leads to many rows.
   /// </summary>
   public ResolvedField? Field(string sourceId, FieldRef field, string path, string? widget)
   {
      ArgumentNullException.ThrowIfNull(field);
      string key = string.Join("\u0001", field.Path.Append(field.Column));
      if (Source(sourceId) is not { } source)
      {
         Issue(IssueSeverity.Error, $"Its source {sourceId} isn't in the catalog", widget, path);
         return null;
      }
      if (fields.TryGetValue((sourceId, key), out ResolvedField? known))
      {
         if (known == null) { Issue(IssueSeverity.Error, $"{Describe(field)} isn't a field of {source.Label}", widget, path); }
         return known;
      }
      EntityDef entity = source.Entity;
      List<NavigationDef> steps = [];
      string? problem = null;
      for (int i = 0; i < field.Path.Count && problem == null; i++)
      {
         NavigationDef? navigation = Navigation(entity, field.Path[i]);
         if (navigation == null) { problem = $"{entity.DisplayName} has no navigation {field.Path[i]}"; }
         else if (navigation.IsCollection) { problem = $"{field.Path[i]} leads to many rows; a field goes through navigations to one row"; }
         else
         {
            steps.Add(navigation);
            entity = navigation.Target;
         }
      }
      ResolvedField? resolved = null;
      if (problem == null)
      {
         NameMatch<ColumnDef> column = entity.FindColumn(field.Column);
         if (column.IsFound) { resolved = new ResolvedField(source, steps, column.Item!); }
         else
         {
            problem = column.Status == MatchStatus.Ambiguous
               ? $"{field.Column} names more than one column of {entity.DisplayName} but for case"
               : $"{entity.DisplayName} has no column {field.Column}";
         }
      }
      fields[(sourceId, key)] = resolved;
      if (problem != null) { Issue(IssueSeverity.Error, problem, widget, path); }
      return resolved;
   }

   /// <summary>A name for a lambda's row nested <paramref name="depth"/> deep: <c>r0</c>, <c>r1</c>, … with <c>_</c>s while the catalog has something of the name.</summary>
   public string Row(int depth)
   {
      string name = "r" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
      while (Catalog.Root.Lookup(name).Status != MatchStatus.NotFound) { name += "_"; }
      return name;
   }

   /// <summary>A navigation of an entity, its own or (a virtual entity's) inherited; exact case first.</summary>
   public static NavigationDef? Navigation(EntityDef entity, string name)
   {
      ArgumentNullException.ThrowIfNull(entity);
      NameMatch<NavigationDef> own = entity.FindNavigation(name);
      if (own.IsFound) { return own.Item; }
      if (entity.InheritedNavigations.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal)) is { } exact) { return exact; }
      List<NavigationDef> inherited = [.. entity.InheritedNavigations.Where(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))];
      return inherited.Count == 1 ? inherited[0] : null;
   }

   private static string Describe(FieldRef field) => string.Join(".", field.Path.Append(field.Column));

   private void ResolveSources()
   {
      for (int i = 0; i < Definition.Sources.Count; i++)
      {
         DashboardSource source = Definition.Sources[i];
         ResolvedSource? resolved = null;
         if (!EntityName.TryParse(source.Entity, out EntityName? name))
         {
            Issue(IssueSeverity.Error, $"'{source.Entity}' isn't an entity's path", null, $"sources[{i}].entity");
         }
         else
         {
            NameMatch<CatalogItem> match = Catalog.Resolve(name);
            if (match.Status == MatchStatus.Ambiguous) { Issue(IssueSeverity.Error, $"{source.Entity} names more than one entity but for case", null, $"sources[{i}].entity"); }
            else if (match.Item is not EntityDef entity) { Issue(IssueSeverity.Error, $"There is no entity {source.Entity} in the catalog", null, $"sources[{i}].entity"); }
            else { resolved = new ResolvedSource(source.Id, source.Label, entity, source.Entity); }
         }
         sources[source.Id] = resolved;
      }
   }

   /// <summary>
   /// Each link both ways: as written, and back through the navigations' opposites. The navigations must lead from
   /// the one's entity to the other's, or to the base entity a virtual entity filters (then its rows are matched on
   /// the navigation's columns); two sources of one entity may be linked as they are.
   /// </summary>
   private void ResolveLinks()
   {
      for (int i = 0; i < Definition.Links.Count; i++)
      {
         DashboardLink link = Definition.Links[i];
         string path = $"links[{i}]";
         if (Source(link.From) is not { } from || Source(link.To) is not { } to) { continue; }
         if (link.Path.Count == 0)
         {
            if (from.Entity != to.Entity) { Issue(IssueSeverity.Error, $"{from.Label} and {to.Label} are of other entities: say the navigations from one to the other", null, path + ".path"); }
            else
            {
               Add(new LinkHop(from, to, [], Correlated: false));
               Add(new LinkHop(to, from, [], Correlated: false));
            }
            continue;
         }
         EntityDef entity = from.Entity;
         List<NavigationDef> steps = [];
         bool ok = true;
         for (int s = 0; s < link.Path.Count; s++)
         {
            NavigationDef? navigation = Navigation(entity, link.Path[s]);
            if (navigation == null)
            {
               Issue(IssueSeverity.Error, $"{entity.DisplayName} has no navigation {link.Path[s]}", null, $"{path}.path[{s}]");
               ok = false;
               break;
            }
            steps.Add(navigation);
            entity = navigation.Target;
         }
         if (!ok) { continue; }
         if (!Lands(entity, to.Entity))
         {
            Issue(IssueSeverity.Error, $"The navigations lead to {entity.DisplayName}, not {to.Entity.DisplayName}", null, path + ".path");
            continue;
         }
         Add(new LinkHop(from, to, steps, Correlated: entity != to.Entity));
         // Back: the opposites, in the other order; from a virtual entity's base, matched on the virtual entity's rows.
         List<NavigationDef> back = [.. steps.AsEnumerable().Reverse().Select(n => n.Opposite)];
         EntityDef landing = back[^1].Target;
         if (!Lands(landing, from.Entity))
         {
            Issue(IssueSeverity.Error, $"The navigations can't be followed back from {to.Label} to {from.Label}", null, path + ".path");
            continue;
         }
         Add(new LinkHop(to, from, back, Correlated: landing != from.Entity));
      }
   }

   /// <summary>Whether navigations ending at <paramref name="landing"/> reach <paramref name="entity"/>'s rows: it, or the base it filters.</summary>
   private static bool Lands(EntityDef landing, EntityDef entity) => landing == entity || entity is VirtualEntity { BaseEntity: { } baseEntity } && baseEntity == landing;

   private void Add(LinkHop hop)
   {
      if (!hops.TryGetValue(hop.From.Id, out List<LinkHop>? list)) { hops[hop.From.Id] = list = []; }
      list.Add(hop);
   }
}
