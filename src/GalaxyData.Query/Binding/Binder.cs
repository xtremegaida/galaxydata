using System;
using System.Collections.Generic;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Language;

namespace GalaxyData.Query.Binding;

/// <summary>
/// Gives parsed query text its meaning against a catalog: names become entities, columns and navigations,
/// methods become query operators, and every expression gets a type. Binding stops at the first error.
/// </summary>
public static class Binder
{
   public static BoundProgram Bind(string text, ICatalog catalog, QueryParameters? parameters = null) =>
      Bind(QueryParser.Default.Parse(text), catalog, parameters);

   public static BoundProgram Bind(ParseResult parsed, ICatalog catalog, QueryParameters? parameters = null)
   {
      ArgumentNullException.ThrowIfNull(parsed);
      ArgumentNullException.ThrowIfNull(catalog);
      return Bind(parsed, new BindContext(catalog, parameters ?? QueryParameters.Empty));
   }

   internal static BoundProgram Bind(ParseResult parsed, BindContext context)
   {
      if (parsed.Root == null) { return new BoundProgram(parsed.Text, [], null, parsed.Diagnostics); }
      return new BinderRun(parsed.Text, context).Run(parsed.Root);
   }
}

internal sealed class BindContext(ICatalog catalog, QueryParameters parameters)
{
   public ICatalog Catalog { get; } = catalog;

   public QueryParameters Parameters { get; } = parameters;

   public FunctionRegistry Functions { get; init; } = FunctionRegistry.Default;

   /// <summary>While the catalog is built: binds a virtual entity that hasn't been bound yet.</summary>
   public Action<VirtualEntity>? EnsureVirtual { get; init; }
}

internal sealed class BindException(QueryDiagnostic diagnostic) : Exception(diagnostic.Message)
{
   public QueryDiagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>A namespace reached while binding a path (<c>shop</c> in <c>shop.orders</c>); never part of a result.</summary>
internal sealed class BoundNamespace(CatalogNamespace ns, SyntaxNode syntax) : BoundNode(syntax)
{
   public CatalogNamespace Namespace { get; } = ns;
}

#region Scopes

internal abstract class Scope(Scope? parent)
{
   public Scope? Parent { get; } = parent;
}

/// <summary>Named subtrees, parameters and the catalog: what every expression can see.</summary>
internal sealed class RootScope() : Scope(null)
{
   private readonly NameTable<BoundLet> lets = new();

   public NameMatch<BoundLet> FindLet(string name) => lets.Find(name);

   public void Add(BoundLet let) => lets.Add(let.Name, let);
}

/// <summary>The implicit row of a method argument: its members are in scope by name, and <c>it</c> is the row.</summary>
internal sealed class RowScope(Scope parent, RowVariable row) : Scope(parent)
{
   public RowVariable Row { get; } = row;
}

/// <summary>The parameters of a lambda argument; the row's members are reached through them.</summary>
internal sealed class LambdaScope(Scope parent, string parameter, RowVariable row) : Scope(parent)
{
   public string Parameter { get; } = parameter;

   public RowVariable Row { get; } = row;
}

#endregion

internal sealed partial class BinderRun
{
   private readonly string text;
   private readonly BindContext context;
   private readonly List<QueryDiagnostic> diagnostics = [];
   private readonly RootScope root = new();
   private readonly HashSet<string> warned = new(StringComparer.OrdinalIgnoreCase);

   public BinderRun(string text, BindContext context)
   {
      this.text = text;
      this.context = context;
   }

   public BoundProgram Run(SyntaxNode syntax)
   {
      SyntaxNode[] statements = syntax is BlockSyntax block ? block.Statements : [syntax];
      List<BoundLet> lets = [];
      try
      {
         for (int i = 0; i < statements.Length; i++)
         {
            SyntaxNode statement = statements[i];
            bool last = i == statements.Length - 1;
            if (TryDefinition(statement, out IdentifierSyntax? name, out SyntaxNode? value))
            {
               if (last)
               {
                  throw Error(statement, DiagnosticCodes.MissingResult,
                     $"The last statement is the query to run, but this one only names '{name.Name}'; add the query after a ';'");
               }
               BoundLet let = BindLet(name, value, statement);
               lets.Add(let);
               root.Add(let);
               continue;
            }
            if (!last)
            {
               throw Error(statement, DiagnosticCodes.StatementNotDefinition,
                  "Only the last statement is the query; the ones before it must name a subtree, as in 'x := ...;'");
            }
            return new BoundProgram(text, lets, BindResult(statement), diagnostics);
         }
      }
      catch (BindException failure)
      {
         diagnostics.Add(failure.Diagnostic);
      }
      return new BoundProgram(text, lets, null, diagnostics);
   }

   private static bool TryDefinition(SyntaxNode statement, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IdentifierSyntax? name,
                                     [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SyntaxNode? value)
   {
      switch (statement)
      {
         case BinarySyntax { Op: ":=", Left: IdentifierSyntax target } assign:
            name = target;
            value = assign.Right;
            return true;
         case LetSyntax let:
            name = new IdentifierSyntax(let.Name, let.Start + 4);
            value = let.Value;
            return true;
         default:
            name = null;
            value = null;
            return false;
      }
   }

   private BoundLet BindLet(IdentifierSyntax name, SyntaxNode value, SyntaxNode statement)
   {
      if (name.Name.StartsWith('$'))
      {
         throw Error(name, DiagnosticCodes.InvalidDefinition, "Names starting with '$' are parameters, supplied with the query; pick another name");
      }
      if (root.FindLet(name.Name).Status != MatchStatus.NotFound)
      {
         throw Error(name, DiagnosticCodes.InvalidDefinition, $"'{name.Name}' is already defined above");
      }
      if (context.Catalog.Root.Lookup(name.Name).Status != MatchStatus.NotFound)
      {
         Warn(name, DiagnosticCodes.ShadowedName, $"'{name.Name}' now means this subtree; the source of the same name is still reached as ::{name.Name}");
      }
      BoundNode bound = BindNode(value, root);
      return bound switch
      {
         BoundQuery or BoundExpr { IsScalar: true } => new BoundLet(name.Name, bound, statement),
         BoundNamespace ns => throw Error(value, DiagnosticCodes.NotAValue, $"'{Describe(ns.Namespace)}' is a namespace; name one of its entities"),
         _ => throw NotScalar((BoundExpr)bound, value),
      };
   }

   private BoundNode BindResult(SyntaxNode statement)
   {
      BoundNode bound = BindNode(statement, root);
      return bound switch
      {
         BoundQuery or BoundExpr { IsScalar: true } => bound,
         BoundNamespace ns => throw Error(statement, DiagnosticCodes.NotAValue,
            $"'{Describe(ns.Namespace)}' is a namespace; name one of its entities, e.g. {Example(ns.Namespace)}"),
         _ => throw NotScalar((BoundExpr)bound, statement),
      };
   }

   #region Diagnostics

   private BindException Error(SyntaxNode? node, string code, string message)
   {
      int start = node?.Start ?? 0;
      int end = node == null ? text.Length : Math.Max(node.End, node.Start);
      return new BindException(QueryDiagnostic.Error(code, message, start, end));
   }

   private void Warn(SyntaxNode node, string code, string message)
   {
      if (!warned.Add(code + ":" + message)) { return; }
      diagnostics.Add(new QueryDiagnostic(code, DiagnosticSeverity.Warning, message, node.Start, node.End));
   }

   private string SourceText(SyntaxNode node)
   {
      if (node.End <= node.Start || node.End > text.Length) { return string.Empty; }
      return string.Join(' ', text[node.Start..node.End].Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
   }

   private static string Describe(CatalogNamespace ns) => ns.Path.Count == 0 ? "the catalog" : string.Join('.', ns.Path);

   private static string Example(CatalogNamespace ns)
   {
      EntityDef? first = ns.Shortcuts.Count > 0 ? ns.Shortcuts[0] : ns.Entities.Count > 0 ? ns.Entities[0] : null;
      if (first != null) { return first.DisplayName; }
      return ns.Namespaces.Count > 0 ? Describe(ns.Namespaces[0]) + ".…" : Describe(ns) + ".…";
   }

   #endregion
}
