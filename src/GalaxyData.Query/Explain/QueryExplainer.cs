using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Language;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Planning.Optimizer;
using GalaxyData.Query.Results;
using GalaxyData.Query.Sql;

namespace GalaxyData.Query.Explain;

/// <summary>Builds a <see cref="QueryExplain"/> from a prepared query.</summary>
internal static class QueryExplainer
{
   public static QueryExplain Explain(PreparedQuery query, BoundProgram? program, PageRequest? paging, bool count, bool optimize, bool verbose)
   {
      Func<PlanNode, string?> site = query.Federation is { } federation
         ? node => federation.Sites.GetValueOrDefault(node)
         : query.Fragments.Count == 1 ? _ => query.Fragments[0].Source.Alias : _ => null;
      return new QueryExplain
      {
         Text = query.Text,
         Diagnostics = query.Diagnostics,
         Success = query.Success,
         Summary = Summary(query),
         Schema = query.Schema,
         Plan = query.Plan == null ? null : new NodeWriter(query.Plan.Root, site).Root(),
         Fragments = query.Fragments.Select(Fragment).ToList(),
         MergeSql = query.Merge?.Text,
         MergeParameters = query.Merge?.Parameters.Select(Parameter).ToList() ?? [],
         Phases = verbose && program is { Success: true } && query.Plan != null ? Phases(program, paging, count, optimize) : null,
      };
   }

   private static string Summary(PreparedQuery query)
   {
      if (!query.Success)
      {
         return query.Diagnostics.FirstOrDefault(d => d.IsError) is { } error ? "The query can't run: " + error.Message : "The query can't run";
      }
      List<string> entities = [];
      Collect(query.Plan!.Root, entities);
      string reading = entities.Count == 0 ? "reading no tables" : "reading " + List(entities);
      if (query.Merge == null)
      {
         QueryFragment fragment = query.Fragments[0];
         return $"Runs as one {fragment.Dialect.Name} query in {fragment.Source.Alias}, {reading}.";
      }
      string engine = $"the merge engine ({query.MergeDialect!.Name})";
      if (query.Fragments.Count == 0) { return $"Runs in {engine}, {reading}."; }
      List<string> sources = query.Fragments.Select(f => $"{f.Source.Alias} ({f.Dialect.Name})").Distinct().ToList();
      string parts = Count(query.Fragments.Count, "fragment");
      int full = query.Fragments.Count(f => f.Table != null && f.BindJoin == null && !f.InMergeEngine);
      int copied = query.Fragments.Count(f => f.Table != null && f.BindJoin == null && f.InMergeEngine);
      int bound = query.Fragments.Count(f => f.BindJoin != null);
      int first = query.Fragments.Count(f => f.Value != null);
      List<string> how = [];
      if (first > 0) { how.Add($"{Count(first, "value")} worked out first"); }
      if (full > 0) { how.Add($"{Count(full, "fragment")} fetched in full"); }
      if (copied > 0) { how.Add($"{Count(copied, "fragment")} copied inside it"); }
      if (bound > 0) { how.Add($"{Count(bound, "fragment")} fetched by the keys of another"); }
      return $"Combines {parts} from {List(sources)} in {engine}, {reading}: {List(how)}.";
   }

   private static void Collect(PlanNode node, List<string> entities)
   {
      if (node is ScanNode scan && !entities.Contains(scan.Entity.DisplayName)) { entities.Add(scan.Entity.DisplayName); }
      foreach (PlanExpr expr in PlanAnalysis.Expressions(node))
      {
         PlanRewriter.Map(expr, e =>
         {
            if (e is PlanSubquery subquery) { Collect(subquery.Plan, entities); }
            return null;
         });
      }
      foreach (PlanNode input in node.Inputs) { Collect(input, entities); }
   }

   private static string Count(int count, string noun) => count == 1 ? "1 " + noun : $"{count.ToString(CultureInfo.InvariantCulture)} {noun}s";

   private static string List(List<string> items) => items.Count switch
   {
      1 => items[0],
      2 => items[0] + " and " + items[1],
      _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
   };

   private static ExplainFragment Fragment(QueryFragment fragment) => new()
   {
      Source = fragment.Source.Alias,
      Dialect = fragment.Dialect.Name,
      Sql = fragment.Sql,
      Parameters = fragment.Statement.Parameters.Select(Parameter).ToList(),
      Strategy = Strategy(fragment),
      Table = fragment.Table,
      EstimatedRows = fragment.EstimatedRows,
      BindJoinTemplate = fragment.BindTemplate?.Text,
      BindJoinParameters = fragment.BindTemplate?.Parameters.Select(Parameter).ToList() ?? [],
   };

   private static string Strategy(QueryFragment fragment) => fragment switch
   {
      { Value: { } value } => $"worked out first, as {value}",
      { Table: null } => "whole result",
      { BindJoin: { } bind } => $"into {fragment.Table}{Inside(fragment)}, by the keys of {bind.Driver.Table}.{bind.DriverColumn} when they are few enough, else in full",
      { InMergeEngine: true } => $"copied into {fragment.Table} inside the merge engine",
      _ => "full fetch into " + fragment.Table,
   };

   /// <summary>The rows of a source kept in the merge engine's database never leave it.</summary>
   private static string Inside(QueryFragment fragment) => fragment.InMergeEngine ? " inside the merge engine" : string.Empty;

   private static ExplainParameter Parameter(SqlParameterSlot slot) => new(slot.Name, slot.Type.ToString(), slot.Description);

   private static List<ExplainPhase> Phases(BoundProgram program, PageRequest? paging, bool count, bool optimize)
   {
      LogicalPlan plan = count ? Lowerer.LowerCount(program) : Lowerer.Lower(program, paging?.Offset, paging?.Limit);
      List<ExplainPhase> phases = [new ExplainPhase("lowered", [], PlanPrinter.Print(plan.Root))];
      if (!optimize) { return phases; }
      OptimizerTrace trace = new();
      PlanOptimizer.Optimize(plan, trace);
      phases.AddRange(trace.Phases.Select(p => new ExplainPhase(p.Phase, p.Rules, p.Plan)));
      return phases;
   }

   /// <summary>Writes a plan as explain nodes, with expressions in query syntax and subqueries numbered in order.</summary>
   private sealed class NodeWriter
   {
      private readonly PlanNode root;
      private readonly Func<PlanNode, string?> site;
      private readonly Dictionary<PlanColumn, string> scanNames = [];
      private readonly Dictionary<PlanSubquery, int> numbers = [];
      private List<PlanSubquery> pending = [];

      public NodeWriter(PlanNode root, Func<PlanNode, string?> site)
      {
         this.root = root;
         this.site = site;
         List<(PlanColumn Column, ScanNode Scan)> scanned = [];
         Scans(root, scanned);
         // A column read by a scan is named by its path from the query's rows (customer.name); when that isn't
         // unique (a table joined to itself), by its table as well.
         Dictionary<string, int> counts = new(StringComparer.Ordinal);
         List<(PlanColumn Column, ScanNode Scan, string Name)> named = [];
         foreach ((PlanColumn column, ScanNode scan) in scanned)
         {
            ColumnSource? source = column.Lineage.Kind == LineageKind.Direct ? column.Lineage.Sources[0] : null;
            string name = source == null || source.Path.Count == 0
               ? QueryText.QuoteName(column.Name)
               : QueryText.AppendMember(new StringBuilder(source.PathText), column.Name).ToString();
            named.Add((column, scan, name));
            counts[name] = counts.GetValueOrDefault(name) + 1;
         }
         foreach ((PlanColumn column, ScanNode scan, string name) in named)
         {
            scanNames[column] = counts[name] > 1 && !name.Contains('.', StringComparison.Ordinal)
               ? QueryText.AppendMember(new StringBuilder(scan.Entity.Name), column.Name).ToString()
               : name;
         }
      }

      public ExplainNode Root() => Node(root);

      private static void Scans(PlanNode node, List<(PlanColumn, ScanNode)> scanned)
      {
         if (node is ScanNode scan)
         {
            foreach (PlanColumn column in scan.Output) { scanned.Add((column, scan)); }
         }
         foreach (PlanExpr expr in PlanAnalysis.Expressions(node))
         {
            PlanRewriter.Map(expr, e =>
            {
               if (e is PlanSubquery subquery) { Scans(subquery.Plan, scanned); }
               return null;
            });
         }
         foreach (PlanNode input in node.Inputs) { Scans(input, scanned); }
      }

      private ExplainNode Node(PlanNode node)
      {
         (string op, string? detail) = Describe(node);
         List<PlanSubquery> subqueries = pending;
         pending = [];
         List<ExplainSubquery> nested = subqueries.Select(s => new ExplainSubquery(numbers[s], s.Kind.ToString().ToLowerInvariant(), Node(s.Plan))).ToList();
         List<ExplainNode> inputs = node.Inputs.Select(Node).ToList();
         return new ExplainNode
         {
            Operator = op,
            Detail = detail,
            Site = site(node),
            Columns = node.Output.Select(c => c.Name).ToList(),
            EstimatedRows = PlanAnalysis.EstimateRows(node),
            Inputs = inputs,
            Subqueries = nested,
         };
      }

      private (string, string?) Describe(PlanNode node)
      {
         switch (node)
         {
            case ScanNode scan:
            {
               ColumnSource? source = scan.Output.Count > 0 && scan.Output[0].Lineage.Kind == LineageKind.Direct ? scan.Output[0].Lineage.Sources[0] : null;
               return ("Scan", scan.Entity.DisplayName + (source is { Path.Count: > 0 } ? " as " + source.PathText : string.Empty));
            }
            case OneRowNode:
               return ("One row", null);
            case FilterNode filter:
               return ("Filter", Expr(filter.Predicate));
            case ProjectNode project:
               return ("Project", string.Join(", ", project.Items.Select(Item)));
            case JoinNode join:
            {
               string kind = join.Kind switch
               {
                  JoinKind.Inner => "Inner join",
                  JoinKind.Left => "Left join",
                  JoinKind.Semi => "Semi join",
                  JoinKind.Anti => "Anti join",
                  _ => join.Kind + " join",
               };
               string? condition = join.Condition == null ? null : Expr(join.Condition);
               return (kind, join.Navigation != null ? join.Navigation.Name + (condition != null ? ": " + condition : string.Empty) : condition);
            }
            case SortNode sort:
               return ("Sort", string.Join(", ", sort.Keys.Select(k => k.Descending ? "desc(" + Expr(k.Expr) + ")" : Expr(k.Expr))));
            case LimitNode limit:
            {
               List<string> parts = [];
               if (limit.Offset != null) { parts.Add("skip " + Expr(limit.Offset)); }
               if (limit.Count != null) { parts.Add("take " + Expr(limit.Count)); }
               return ("Limit", string.Join(", ", parts));
            }
            case DistinctNode:
               return ("Distinct", null);
            case AggregateNode aggregate:
            {
               StringBuilder text = new();
               if (aggregate.Keys.Count > 0) { text.Append("by ").AppendJoin(", ", aggregate.Keys.Select(Item)); }
               if (aggregate.Aggregates.Count > 0)
               {
                  text.Append(text.Length > 0 ? "; " : string.Empty).AppendJoin(", ", aggregate.Aggregates.Select(Aggregate));
               }
               return ("Aggregate", text.ToString());
            }
            case SetOpNode set:
            {
               string name = set.Operation switch
               {
                  SetOperation.Union => "Union",
                  SetOperation.UnionAll => "Union all",
                  SetOperation.Intersect => "Intersect",
                  _ => "Except",
               };
               return (name, string.Join(", ", set.Output.Select(c => QueryText.QuoteName(c.Name))));
            }
            default:
               return (node.GetType().Name, null);
         }
      }

      /// <summary>A projected column: its value when it passes one through, else <c>name: value</c>.</summary>
      private string Item(ProjectItem item)
      {
         string value = Expr(item.Expr);
         return item.IsPassThrough ? value : $"{QueryText.QuoteName(item.Column.Name)}: {value}";
      }

      private string Aggregate(AggregateItem item)
      {
         string function = item.Function switch
         {
            AggregateFunction.CountRows => "count()",
            AggregateFunction.CountDistinct => $"countDistinct({Expr(item.Argument!)})",
            _ => $"{item.Function.ToString().ToLowerInvariant()}({Expr(item.Argument!)})",
         };
         return $"{QueryText.QuoteName(item.Column.Name)}: {function}";
      }

      private string Name(PlanColumn column) => scanNames.TryGetValue(column, out string? name) ? name : QueryText.QuoteName(column.Name);

      private string Expr(PlanExpr expr) => expr switch
      {
         PlanColumnRef reference => Name(reference.Column),
         PlanLiteral literal => Literal(literal.Value),
         PlanParameter { Source: ParameterSource.User } parameter => "$" + parameter.Name,
         PlanParameter parameter => parameter.Name + "()",
         PlanUnary unary => OperatorText.Symbol(unary.Op) + Operand(unary.Operand),
         PlanBinary binary => $"{Operand(binary.Left)} {OperatorText.Symbol(binary.Op)} {Operand(binary.Right)}",
         PlanIsNull isNull => $"{Operand(isNull.Operand)} {(isNull.Negated ? "!=" : "==")} null",
         PlanInList inList => inList.Negated
            ? $"not ({Operand(inList.Operand)} in [{string.Join(", ", inList.Items.Select(Expr))}])"
            : $"{Operand(inList.Operand)} in [{string.Join(", ", inList.Items.Select(Expr))}]",
         PlanConditional conditional => $"{Operand(conditional.Condition)} ? {Operand(conditional.WhenTrue)} : {Operand(conditional.WhenFalse)}",
         PlanFunction call => $"{call.Function.Name}({string.Join(", ", call.Arguments.Select(Expr))})",
         PlanSubquery subquery => Subquery(subquery),
         _ => expr.GetType().Name,
      };

      private string Subquery(PlanSubquery subquery)
      {
         if (!numbers.TryGetValue(subquery, out int number))
         {
            number = numbers.Count + 1;
            numbers[subquery] = number;
            pending.Add(subquery);
         }
         string reference = "subquery " + number.ToString(CultureInfo.InvariantCulture);
         return subquery.Kind switch
         {
            SubqueryKind.Exists => $"{(subquery.Negated ? "not exists" : "exists")}({reference})",
            SubqueryKind.In => $"{Operand(subquery.Operand!)} in ({reference})",
            _ => $"({reference})",
         };
      }

      private string Operand(PlanExpr expr) =>
         expr is PlanBinary or PlanConditional or PlanIsNull or PlanInList ? "(" + Expr(expr) + ")" : Expr(expr);

      /// <summary>A constant as it would be written in a query (the SQL may parameterize it).</summary>
      private static string Literal(object? value)
      {
         string text = BoundTreePrinter.Literal(value);
         int cast = text.LastIndexOf("'::", StringComparison.Ordinal);
         return value is not string && cast > 0 ? text[..(cast + 1)] : text;
      }
   }
}
