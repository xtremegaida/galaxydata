using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Language;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Binding;

internal sealed partial class BinderRun
{
   private const string ItKeyword = "it";
   private const string OuterKeyword = "outer";
   private const string InnerKeyword = "inner";

   /// <summary>Resolves a bare name: lambda parameters, then the implicit row, then outer rows, then subtrees and the catalog.</summary>
   private BoundNode ResolveName(IdentifierSyntax id, Scope scope)
   {
      string name = id.Name;
      if (name.StartsWith('$')) { return Parameter(id); }
      int colons = name.IndexOf("::", StringComparison.Ordinal);
      if (colons >= 0) { return ResolveRooted(id, name[..colons], name[(colons + 2)..]); }

      for (Scope? current = scope; current != null; current = current.Parent)
      {
         switch (current)
         {
            case LambdaScope lambda when string.Equals(lambda.Parameter, name, StringComparison.Ordinal):
               return lambda.Value is BoundRowRef reference ? new BoundRowRef(reference.Row, id) : lambda.Value;
            case JoinScope join when name is OuterKeyword or InnerKeyword:
               return new BoundRowRef(name == OuterKeyword ? join.Outer : join.Inner, id);
            case RecordScope record:
            {
               if (string.Equals(name, ItKeyword, StringComparison.Ordinal)) { return record.Record; }
               NameMatch<ShapeMember> member = record.Shape.Find(name);
               if (member.Status == MatchStatus.Ambiguous) { throw AmbiguousMember(id, name, member); }
               if (member.IsFound) { return MemberNode(record.Record, member.Item!, id); }
               break;
            }
            case RowScope row:
            {
               if (string.Equals(name, ItKeyword, StringComparison.Ordinal)) { return new BoundRowRef(row.Row, id); }
               NameMatch<ShapeMember> member = row.Row.Shape.Find(name);
               if (member.Status == MatchStatus.Ambiguous) { throw AmbiguousMember(id, name, member); }
               if (member.IsFound)
               {
                  WarnIfShadowing(id, name);
                  return MemberNode(new BoundRowRef(row.Row, null), member.Item!, id);
               }
               if (row.Row.Shape.Group != null && GroupElementMember(new BoundRowRef(row.Row, null), name, id) is { } element) { return element; }
               break;
            }
            case RootScope rootScope:
               NameMatch<BoundLet> let = rootScope.FindLet(name);
               if (let.IsFound) { return LetReference(let.Item!, id); }
               NameMatch<CatalogItem> item = context.Catalog.Root.Lookup(name);
               if (item.IsFound) { return CatalogReference(item.Item!, id); }
               if (item.Status == MatchStatus.Ambiguous) { throw AmbiguousItem(id, name, item); }
               break;
         }
      }
      throw UnknownName(id, scope);
   }

   private BoundNode ResolveRooted(IdentifierSyntax id, string first, string rest)
   {
      NameMatch<CatalogItem> item = context.Catalog.Root.Lookup(first);
      if (!item.IsFound)
      {
         if (item.Status == MatchStatus.Ambiguous) { throw AmbiguousItem(id, first, item); }
         throw Error(id, DiagnosticCodes.UnknownName, $"There is no source or namespace '{first}'{Suggestion(first, RootNames())}");
      }
      return BindMember(CatalogReference(item.Item!, id), rest, id);
   }

   private void WarnIfShadowing(SyntaxNode node, string name)
   {
      if (root.FindLet(name).IsFound)
      {
         Warn(node, DiagnosticCodes.ShadowedName, $"'{name}' here is the row's member, which hides the subtree named '{name}'");
      }
   }

   private BoundExpr Parameter(IdentifierSyntax id)
   {
      if (!context.Parameters.TryGet(id.Name, out QueryParameter? parameter))
      {
         throw Error(id, DiagnosticCodes.UnknownParameter, $"No value was given for the parameter {id.Name}");
      }
      return new BoundParameter(parameter.Name, parameter.Type, id);
   }

   private BoundNode LetReference(BoundLet let, SyntaxNode syntax) => let.Value switch
   {
      BoundQuery => new BoundLetQuery(let, syntax),
      BoundLiteral literal => new BoundLiteral(literal.Value, literal.Scalar, syntax),
      _ => new BoundLetValue(let, syntax),
   };

   private BoundNode CatalogReference(CatalogItem item, SyntaxNode syntax) => item switch
   {
      CatalogNamespace ns => new BoundNamespace(ns, syntax),
      EntityDef entity => Scan(entity, syntax),
      _ => throw new InvalidOperationException($"Unexpected catalog item {item}"),
   };

   private BoundEntityScan Scan(EntityDef entity, SyntaxNode syntax)
   {
      if (entity is VirtualEntity virtualEntity)
      {
         if (virtualEntity.State == VirtualState.Pending) { context.EnsureVirtual?.Invoke(virtualEntity); }
         if (virtualEntity.State == VirtualState.Binding)
         {
            throw Error(syntax, DiagnosticCodes.VirtualEntityCycle, $"{entity.DisplayName} is defined in terms of itself");
         }
         if (virtualEntity.Problem != null || virtualEntity.State != VirtualState.Bound)
         {
            throw Error(syntax, DiagnosticCodes.BrokenVirtualEntity,
               $"{entity.DisplayName} can't be used: {virtualEntity.Problem ?? "its definition has not been bound"}");
         }
      }
      return new BoundEntityScan(entity, syntax);
   }

   /// <summary>The member of a record, the child of a namespace, or an error explaining why neither applies.</summary>
   private BoundNode BindMember(BoundNode target, string name, SyntaxNode node)
   {
      switch (target)
      {
         case BoundNamespace ns:
         {
            NameMatch<CatalogItem> item = ns.Namespace.Lookup(name);
            if (item.IsFound) { return CatalogReference(item.Item!, node); }
            if (item.Status == MatchStatus.Ambiguous) { throw AmbiguousItem(node, name, item); }
            throw Error(node, DiagnosticCodes.UnknownName,
               $"{Describe(ns.Namespace)} has no '{name}'{Suggestion(name, NamespaceNames(ns.Namespace))}");
         }
         case BoundQuery:
            throw Error(node, DiagnosticCodes.NotARecord, IsQueryMethod(name)
               ? $"'{name}' is a method; call it with parentheses: .{name}(...)"
               : $"A query is a list of rows, so it has no member '{name}'; to take it from each row, write .select({FormatName(name)})");
         case BoundExpr expr:
            switch (expr.Type)
            {
               case RecordBoundType record:
               {
                  NameMatch<ShapeMember> member = record.Shape.Find(name);
                  if (member.IsFound) { return MemberNode(expr, member.Item!, node); }
                  if (member.Status == MatchStatus.Ambiguous) { throw AmbiguousMember(node, name, member); }
                  if (record.Shape.Group != null && GroupElementMember(expr, name, node) is { } element) { return element; }
                  string owner = record.Shape.Entity?.DisplayName ?? "the row";
                  throw Error(node, DiagnosticCodes.UnknownName,
                     $"{owner} has no '{name}'{Suggestion(name, record.Shape.Members.Select(m => m.Name))}{MemberList(record.Shape)}");
               }
               case GroupCollectionType:
                  return GroupCollectionMember((BoundGroupCollection)expr, name, node);
               default:
                  throw Error(node, DiagnosticCodes.NotARecord, context.Functions.TryGet(name, out _)
                     ? $"'{name}' is a function; call it with parentheses: .{name}()"
                     : $"A {TypeRules.Describe(expr.Scalar)} has no members, so '.{name}' means nothing here");
            }
         default:
            throw new InvalidOperationException($"Unexpected bound node {target}");
      }
   }

   /// <summary>A member of a record: a value, or for a collection navigation the query of the rows it leads to.</summary>
   private static BoundNode MemberNode(BoundExpr target, ShapeMember member, SyntaxNode? syntax) =>
      member is NavigationMember { Navigation.IsCollection: true } navigation
         ? new BoundNavigationQuery(target, navigation.Navigation, syntax)
         : Member(target, member, syntax);

   private BindException AmbiguousMember(SyntaxNode node, string name, NameMatch<ShapeMember> member) =>
      Error(node, DiagnosticCodes.AmbiguousName,
         $"'{name}' matches {string.Join(" and ", member.Candidates.Select(c => c.Name))}; write it with the exact case, or as it[\"{member.Candidates[0].Name}\"]");

   private static BoundExpr Member(BoundExpr target, ShapeMember member, SyntaxNode? syntax)
   {
      bool nullable = target.Type.Nullable;
      BoundType type = member switch
      {
         ColumnMember column => new ScalarBoundType(column.Type.WithNullable(column.Type.Nullable || nullable)),
         RecordMember record => new RecordBoundType(record.Shape, record.Nullable || nullable),
         NavigationMember { Navigation.IsCollection: true } navigation => new CollectionBoundType(RowShape.ForEntity(navigation.Navigation.Target)),
         NavigationMember navigation => new RecordBoundType(
            RowShape.ForEntity(navigation.Navigation.Target), navigation.Navigation.Multiplicity != Multiplicity.One || nullable),
         _ => throw new InvalidOperationException($"Unexpected member {member}"),
      };
      return new BoundMemberAccess(target, member, type, syntax);
   }

   #region Messages

   private BindException AmbiguousItem(SyntaxNode node, string name, NameMatch<CatalogItem> item) =>
      Error(node, DiagnosticCodes.AmbiguousName,
         $"'{name}' matches {string.Join(" and ", item.Candidates.Select(c => c is EntityDef e ? e.QualifiedName.ToString() : c.Name))}; write it with the exact case");

   private BindException UnknownName(IdentifierSyntax id, Scope scope)
   {
      string name = id.Name;
      List<string> candidates = [];
      string hint = string.Empty;
      for (Scope? current = scope; current != null; current = current.Parent)
      {
         switch (current)
         {
            case LambdaScope lambda:
               candidates.Add(lambda.Parameter);
               if (lambda.Shape.Find(name).IsFound && hint.Length == 0)
               {
                  hint = $". Inside '{lambda.Parameter} => ...' the row's members are reached through {lambda.Parameter}: {lambda.Parameter}{MemberText(name)}";
               }
               break;
            case RowScope row:
               candidates.Add(ItKeyword);
               candidates.AddRange(row.Row.Shape.Members.Select(m => m.Name));
               if (row.Row.Shape.Group != null) { candidates.AddRange(row.Row.Shape.Group.ElementRow.Shape.Members.Select(m => m.Name)); }
               break;
            case RecordScope record:
               candidates.Add(ItKeyword);
               candidates.AddRange(record.Shape.Members.Select(m => m.Name));
               break;
            case JoinScope:
               candidates.Add(OuterKeyword);
               candidates.Add(InnerKeyword);
               if (hint.Length == 0) { hint = ". In a join's condition and items, the rows are outer and inner: outer.customer_id == inner.id"; }
               break;
         }
      }
      candidates.AddRange(RootNames());
      string suggestion = hint.Length > 0 ? hint : Suggestion(name, candidates);
      return Error(id, DiagnosticCodes.UnknownName, $"There is no '{name}' here{suggestion}");
   }

   private IEnumerable<string> RootNames()
   {
      foreach (CatalogNamespace ns in context.Catalog.Root.Namespaces) { yield return ns.Name; }
      foreach (EntityDef entity in context.Catalog.Root.Entities) { yield return entity.Name; }
   }

   private static IEnumerable<string> NamespaceNames(CatalogNamespace ns) =>
      ns.Namespaces.Select(n => n.Name).Concat(ns.Entities.Select(e => e.Name)).Concat(ns.Shortcuts.Select(e => e.Name));

   private static string MemberList(RowShape shape)
   {
      List<string> names = shape.Members.Select(m => m.Name).ToList();
      if (names.Count == 0) { return string.Empty; }
      string list = string.Join(", ", names.Take(12));
      return $". It has {list}{(names.Count > 12 ? ", …" : string.Empty)}";
   }

   private static string MemberText(string name) => QueryText.IsBareIdentifier(name) ? "." + name : $"[{QueryText.QuoteString(name)}]";

   private static string FormatName(string name) => QueryText.IsBareIdentifier(name) ? name : $"it[{QueryText.QuoteString(name)}]";

   /// <summary>
   /// ". Did you mean 'x'?" for the closest candidate within a couple of edits, or for the names that start with this
   /// one (orders → orders_by_customer); nothing when none is close.
   /// </summary>
   private static string Suggestion(string name, IEnumerable<string> candidates)
   {
      List<string> distinct = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      string? best = null;
      int bestDistance = int.MaxValue;
      int limit = Math.Max(1, Math.Min(3, name.Length / 3));
      foreach (string candidate in distinct)
      {
         int distance = Distance(name.ToLowerInvariant(), candidate.ToLowerInvariant());
         if (distance < bestDistance && distance <= limit)
         {
            best = candidate;
            bestDistance = distance;
         }
      }
      if (best != null) { return $". Did you mean '{best}'?"; }
      List<string> longer = distinct
         .Where(c => name.Length >= 3 && c.Length > name.Length && c.StartsWith(name, StringComparison.OrdinalIgnoreCase))
         .OrderBy(c => c.Length).ThenBy(c => c, StringComparer.Ordinal).Take(3).ToList();
      return longer.Count == 0 ? string.Empty : $". Did you mean {string.Join(" or ", longer.Select(c => $"'{c}'"))}?";
   }

   /// <summary>Optimal string alignment distance: edits, counting a swap of neighbours as one.</summary>
   private static int Distance(string a, string b)
   {
      int[,] d = new int[a.Length + 1, b.Length + 1];
      for (int i = 0; i <= a.Length; i++) { d[i, 0] = i; }
      for (int j = 0; j <= b.Length; j++) { d[0, j] = j; }
      for (int i = 1; i <= a.Length; i++)
      {
         for (int j = 1; j <= b.Length; j++)
         {
            int cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) { d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1); }
         }
      }
      return d[a.Length, b.Length];
   }

   #endregion
}
