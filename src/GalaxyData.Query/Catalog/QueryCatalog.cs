using System;
using System.Collections.Generic;
using GalaxyData.Query.Diagnostics;

namespace GalaxyData.Query.Catalog;

/// <summary>The logical schema the binder works against: sources, entities, relations and navigations.</summary>
public interface ICatalog
{
   CatalogNamespace Root { get; }

   IReadOnlyList<SourceInfo> Sources { get; }

   IReadOnlyList<EntityDef> Entities { get; }

   IReadOnlyList<RelationDef> Relations { get; }

   IReadOnlyList<CatalogDiagnostic> Diagnostics { get; }

   /// <summary>Walks <paramref name="path"/> from the root; a shortcut path finds the entity too.</summary>
   NameMatch<CatalogItem> Resolve(EntityName path);

   EntityDef? FindEntity(EntityName path);

   SourceInfo? FindSource(string alias);
}

public sealed record CatalogDiagnostic(string Code, DiagnosticSeverity Severity, string Message, string? Subject = null)
{
   public override string ToString() => Subject == null ? $"{Code} {Severity}: {Message}" : $"{Code} {Severity} [{Subject}]: {Message}";
}

public sealed class QueryCatalog : ICatalog
{
   internal QueryCatalog(CatalogNamespace root, IReadOnlyList<SourceInfo> sources, IReadOnlyList<EntityDef> entities,
                    IReadOnlyList<RelationDef> relations, IReadOnlyList<CatalogDiagnostic> diagnostics)
   {
      Root = root;
      Sources = sources;
      Entities = entities;
      Relations = relations;
      Diagnostics = diagnostics;
   }

   public CatalogNamespace Root { get; }

   public IReadOnlyList<SourceInfo> Sources { get; }

   public IReadOnlyList<EntityDef> Entities { get; }

   public IReadOnlyList<RelationDef> Relations { get; }

   public IReadOnlyList<CatalogDiagnostic> Diagnostics { get; }

   public NameMatch<CatalogItem> Resolve(EntityName path)
   {
      ArgumentNullException.ThrowIfNull(path);
      return Walk(Root, path.Parts);
   }

   internal static NameMatch<CatalogItem> Walk(CatalogNamespace from, IReadOnlyList<string> parts)
   {
      CatalogItem current = from;
      for (int i = 0; i < parts.Count; i++)
      {
         if (current is not CatalogNamespace ns) { return NameMatch<CatalogItem>.NotFound; }
         NameMatch<CatalogItem> match = ns.Lookup(parts[i]);
         if (!match.IsFound) { return match; }
         current = match.Item!;
      }
      return NameMatch<CatalogItem>.Found(current);
   }

   public EntityDef? FindEntity(EntityName path)
   {
      NameMatch<CatalogItem> match = Resolve(path);
      return match.IsFound ? match.Item as EntityDef : null;
   }

   public EntityDef? FindEntity(string path) =>
      EntityName.TryParse(path, out EntityName? name) ? FindEntity(name) : null;

   public SourceInfo? FindSource(string alias)
   {
      foreach (SourceInfo source in Sources)
      {
         if (string.Equals(source.Alias, alias, StringComparison.OrdinalIgnoreCase)) { return source; }
      }
      return null;
   }
}
