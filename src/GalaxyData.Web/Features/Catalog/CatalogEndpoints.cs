using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Overlay;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GalaxyData.Web.Features.Catalog;

/// <summary>The catalog's version, its sources as they stand, and what building it found (a table its schema's name hides).</summary>
public sealed record CatalogDto(string Version, IReadOnlyList<CatalogSourceDto> Sources, IReadOnlyList<CatalogDiagnosticDto> Diagnostics);

/// <summary>A source: how reading its schema stands, and how many entities it has in the catalog.</summary>
public sealed record CatalogSourceDto(string Alias, string Kind, string? DisplayName, SchemaStatus Status, DateTime? RefreshedAt, bool IsReadOnly, bool HasSchema,
                                      int Entities, string? Problem);

/// <summary>A problem building the catalog found; one with an item of the overlay names it (<see cref="Item"/>).</summary>
public sealed record CatalogDiagnosticDto(string Code, DiagnosticSeverity Severity, string Message, string? Subject, OverlayItemDto? Item);

/// <summary>
/// A node of the catalog's tree. Its id is its path as queries write it (<c>shop</c>, <c>shop.sales</c>,
/// <c>shop.orders</c>, <c>xl["Budget 2024"]["Sheet 1"]</c>). Sources tell their kind, status and whether they are
/// read-only; entities their row count (as the database estimates it) and whether the user may change their rows.
/// </summary>
public sealed record TreeNodeDto(string Id, TreeNodeKind Kind, string Name, bool HasChildren)
{
   public string? Label { get; init; }

   public string? SourceKind { get; init; }

   public SchemaStatus? Status { get; init; }

   public bool? IsReadOnly { get; init; }

   public long? Rows { get; init; }

   public bool? Editable { get; init; }

   public string? Comment { get; init; }
}

public sealed record TreeChildrenDto(string? Parent, IReadOnlyList<TreeNodeDto> Nodes);

/// <summary>A node found, the ids of its ancestors (the root's child first) to open, and the columns found when it was found by them.</summary>
public sealed record TreeHitDto(TreeNodeDto Node, IReadOnlyList<string> Path, IReadOnlyList<string>? Columns);

public sealed record TreeSearchDto(string Text, IReadOnlyList<TreeHitDto> Hits, bool More);

public sealed record KeyDto(string? Name, IReadOnlyList<string> Columns, bool IsDeclared);

/// <summary>A column, and what the user may do with it: change it, and give it a value in a new row.</summary>
public sealed record EntityColumnDto(string Name, int Ordinal, TypeDto Type, string? NativeType, bool IsKey, bool IsIdentity, bool IsComputed, bool HasDefault,
                                     bool IsRowVersion, bool Hidden, string? Label, string? Comment, bool CanUpdate, InsertMode Insert, string? ReadOnlyReason);

/// <summary>A navigation to the rows a row is linked to: along a foreign key, or a relation the overlay adds.</summary>
public sealed record NavigationDto(string Name, string Target, Multiplicity Multiplicity, IReadOnlyList<string> Columns, IReadOnlyList<string> TargetColumns,
                                   bool IsInverse, RelationOrigin Origin, bool IsEnforced, bool IsCrossSource, bool Hidden, bool Inherited);

/// <summary>
/// An entity: what it is (a table or view of a source, or a virtual entity of the overlay), its columns, keys and
/// navigations, and what the user may do with its rows.
/// </summary>
public sealed record EntityDto(string Name, string QualifiedName, EntityKind Kind, string? Source, string? Schema, string? Table, string? Comment,
                               long? RowCountEstimate, bool HasTriggers, KeyDto? Key, IReadOnlyList<KeyDto> UniqueKeys, string? DisplayColumn,
                               IReadOnlyList<EntityColumnDto> Columns, IReadOnlyList<NavigationDto> Navigations, CapabilitiesDto Capabilities,
                               string? Query, string? Problem);

/// <summary>
/// The catalog, for anyone who reads data: its sources, its tree (children of a node, search) and its entities.
/// Answers carry the catalog's version (<see cref="CatalogService.VersionHeader"/>), which changes when the catalog
/// does.
/// </summary>
public static class CatalogEndpoints
{
   public const int MaxSearchLength = 200;

   public const int MaxSearchResults = 200;

   public static RouteGroupBuilder MapCatalog(this RouteGroupBuilder api)
   {
      RouteGroupBuilder catalog = api.MapGroup("/catalog")
         .WithTags("Catalog")
         .RequireAuthorization(Policies.CanRead)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      catalog.MapGet("/", GetAsync).WithName("GetCatalog").WithSummary("The catalog's version and sources");
      catalog.MapGet("/tree/children", ChildrenAsync).WithName("GetTreeChildren")
         .WithSummary("The children of a node of the tree, or its top nodes (the sources)")
         .ProducesProblem(StatusCodes.Status404NotFound);
      catalog.MapGet("/tree/search", SearchAsync).WithName("SearchTree")
         .WithSummary("Nodes whose names (or paths, or columns) have the text in them")
         .RequiresQuery("text")
         .ProducesValidationProblem();
      catalog.MapGet("/entity", EntityAsync).WithName("GetEntity")
         .WithSummary("An entity, by its name as queries write it")
         .RequiresQuery("name")
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status404NotFound);
      return api;
   }

   private static async Task<Ok<CatalogDto>> GetAsync(HttpResponse response, CatalogService catalogs, CancellationToken cancellationToken)
   {
      CatalogState state = await StateAsync(response, catalogs, cancellationToken);
      Dictionary<string, int> counts = state.Catalog.Entities.OfType<TableEntity>()
         .GroupBy(e => e.Source.Alias, StringComparer.OrdinalIgnoreCase)
         .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
      return TypedResults.Ok(new CatalogDto(state.Version,
         state.Sources.Select(s => new CatalogSourceDto(s.Alias, s.Kind, s.DisplayName, s.Status, s.RefreshedAt, s.IsReadOnly, s.HasSchema,
            counts.GetValueOrDefault(s.Alias), s.Problem)).ToList(),
         state.Catalog.Diagnostics.Select(d => new CatalogDiagnosticDto(d.Code, d.Severity, d.Message, d.Subject,
            d.Item is { } item ? new OverlayItemDto(item.Kind, state.Overlay.IdOf(item)) : null)).ToList()));
   }

   private static async Task<Results<Ok<TreeChildrenDto>, ProblemHttpResult>> ChildrenAsync(string? parent, ClaimsPrincipal me, HttpResponse response,
      CatalogService catalogs, CancellationToken cancellationToken)
   {
      CatalogState state = await StateAsync(response, catalogs, cancellationToken);
      IReadOnlyList<TreeNode> children;
      if (string.IsNullOrEmpty(parent))
      {
         children = state.Tree.Roots;
      }
      else if (state.Tree.Find(parent) is { } node)
      {
         children = node.Children;
      }
      else
      {
         return ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such node", $"There is no {parent} in the catalog (any more)");
      }
      bool canEdit = CanEditData(me);
      return TypedResults.Ok(new TreeChildrenDto(string.IsNullOrEmpty(parent) ? null : parent, children.Select(n => Node(n, canEdit)).ToList()));
   }

   private static async Task<Results<Ok<TreeSearchDto>, ValidationProblem>> SearchAsync(string? text, int? take, ClaimsPrincipal me, HttpResponse response,
      CatalogService catalogs, CancellationToken cancellationToken)
   {
      Dictionary<string, string[]> errors = [];
      if (string.IsNullOrWhiteSpace(text)) { errors["text"] = ["Give the text to look for"]; }
      else if (text.Length > MaxSearchLength) { errors["text"] = [$"Look for at most {MaxSearchLength} characters"]; }
      if (take is < 1 or > MaxSearchResults) { errors["take"] = [$"Take 1 to {MaxSearchResults}"]; }
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      CatalogState state = await StateAsync(response, catalogs, cancellationToken);
      bool canEdit = CanEditData(me);
      (IReadOnlyList<TreeHit> hits, bool more) = state.Tree.Search(text!, take ?? 50);
      return TypedResults.Ok(new TreeSearchDto(text!.Trim(), hits.Select(h => new TreeHitDto(Node(h.Node, canEdit),
         CatalogTree.Ancestors(h.Node).Select(a => a.Id).ToList(), h.Columns.Count > 0 ? h.Columns : null)).ToList(), more));
   }

   private static async Task<Results<Ok<EntityDto>, ValidationProblem, ProblemHttpResult>> EntityAsync(string? name, ClaimsPrincipal me, HttpResponse response,
      CatalogService catalogs, SourceProviders providers, CancellationToken cancellationToken)
   {
      if (!EntityName.TryParse(name, out EntityName? path))
      {
         return ApiProblems.Invalid(new Dictionary<string, string[]> { ["name"] = [$"'{name}' isn't an entity's name, such as shop.orders or xl[\"Budget\"][\"Sheet 1\"]"] });
      }
      CatalogState state = await StateAsync(response, catalogs, cancellationToken);
      NameMatch<CatalogItem> match = state.Catalog.Resolve(path);
      if (match.Status == MatchStatus.Ambiguous)
      {
         string candidates = string.Join(", ", match.Candidates.Select(c => c is EntityDef e ? e.DisplayName : c.ToString()));
         return ApiProblems.Invalid(new Dictionary<string, string[]> { ["name"] = [$"{name} names more than one thing, whose names differ only in case: {candidates}"] });
      }
      if (match.Item is not EntityDef entity)
      {
         return ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such entity", $"There is no entity {name} in the catalog");
      }
      return TypedResults.Ok(Describe(entity, providers, CanEditData(me)));
   }

   private static Task<CatalogState> StateAsync(HttpResponse response, CatalogService catalogs, CancellationToken cancellationToken) =>
      catalogs.GetAsync(response, cancellationToken);

   private static bool CanEditData(ClaimsPrincipal user) => user.IsInRole(nameof(UserRole.DataManager)) || user.IsInRole(nameof(UserRole.Admin));

   private static TreeNodeDto Node(TreeNode node, bool canEdit)
   {
      TreeNodeDto dto = new(node.Id, node.Kind, node.Name, node.Children.Count > 0);
      if (node.Source is { } source)
      {
         return dto with { Label = source.DisplayName, SourceKind = source.Kind, Status = source.Status, IsReadOnly = source.IsReadOnly };
      }
      if (node.Entity is { } entity)
      {
         return dto with
         {
            Rows = (entity as TableEntity)?.RowCountEstimate,
            Editable = canEdit && DmlRules.WhyNoChanges(entity) == null,
            Comment = entity.Comment,
         };
      }
      return dto;
   }

   internal static EntityDto Describe(EntityDef entity, SourceProviders providers, bool canEdit)
   {
      TableEntity? table = entity as TableEntity;
      SqlDialect? dialect = table == null ? null : providers.For(table.Source.ProviderKind).Dialect;
      CapabilitiesDto capabilities = EntityCapabilities.Of(entity, dialect, canEdit);
      VirtualEntity? defined = entity as VirtualEntity;
      return new EntityDto(entity.DisplayName, entity.QualifiedName.ToString(), entity.Kind, table?.Source.Alias, table?.Schema, table?.Table, entity.Comment,
         table?.RowCountEstimate, table?.HasTriggers ?? false, entity.Key is { } key ? Key(key) : null, entity.UniqueKeys.Select(Key).ToList(),
         entity.DisplayColumn?.Name, entity.Columns.Select(c => Column(c, dialect, capabilities)).ToList(),
         [.. entity.Navigations.Select(n => Navigation(n, inherited: false)), .. entity.InheritedNavigations.Select(n => Navigation(n, inherited: true))],
         capabilities, defined?.QueryText, defined?.Problem);
   }

   private static KeyDto Key(KeyDef key) => new(key.Name, key.Columns.Select(c => c.Name).ToList(), key.IsDeclared);

   private static EntityColumnDto Column(ColumnDef column, SqlDialect? dialect, CapabilitiesDto entity)
   {
      ColumnCapabilities can = EntityCapabilities.Of(column, dialect, entity);
      return new EntityColumnDto(column.Name, column.Ordinal, TypeDto.Of(column.Type), column.NativeType, column.IsKey, column.IsIdentity, column.IsComputed,
         column.HasDefault, column.IsRowVersion, column.Hidden, column.Label, column.Comment, can.CanUpdate, can.Insert, can.Reason);
   }

   private static NavigationDto Navigation(NavigationDef navigation, bool inherited) =>
      new(navigation.Name, navigation.Target.DisplayName, navigation.Multiplicity, navigation.OwnerColumns.Select(c => c.Name).ToList(),
         navigation.TargetColumns.Select(c => c.Name).ToList(), navigation.IsInverse, navigation.Relation.Origin, navigation.Relation.IsEnforced,
         navigation.Relation.IsCrossSource, navigation.Hidden, inherited);
}
