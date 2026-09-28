using System.Collections.Generic;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Results;

namespace GalaxyData.Query.Explain;

/// <summary>
/// What a query would do, worked out without running it: its diagnostics, the columns it returns (with lineage,
/// links and edit targets), the plan, and the SQL each source runs. Plain data, for rendering or serializing.
/// </summary>
public sealed class QueryExplain
{
   public required string Text { get; init; }

   public required IReadOnlyList<QueryDiagnostic> Diagnostics { get; init; }

   /// <summary>Whether the query can run: it binds, plans and has SQL for its sources.</summary>
   public required bool Success { get; init; }

   /// <summary>One sentence on how the query runs, or why it can't.</summary>
   public required string Summary { get; init; }

   public ResultSchema? Schema { get; init; }

   /// <summary>The optimized plan, operators with their inputs; null when the query didn't plan.</summary>
   public ExplainNode? Plan { get; init; }

   public IReadOnlyList<ExplainFragment> Fragments { get; init; } = [];

   /// <summary>The merge engine's SQL, which combines the fragments' rows, when the query reads more than one source; null otherwise.</summary>
   public string? MergeSql { get; init; }

   public IReadOnlyList<ExplainParameter> MergeParameters { get; init; } = [];

   /// <summary>For a verbose explain: the plan as lowered and after each optimizer phase, with the rules that fired.</summary>
   public IReadOnlyList<ExplainPhase>? Phases { get; init; }
}

/// <summary>One operator of a plan. Details are written in query syntax.</summary>
public sealed class ExplainNode
{
   /// <summary>The operator: Scan, Filter, Project, Inner join, Sort, Limit, Aggregate, Union all, ...</summary>
   public required string Operator { get; init; }

   /// <summary>What it does, in query syntax: the condition of a filter, the items of a projection, ...</summary>
   public string? Detail { get; init; }

   /// <summary>Where it runs: a source's alias (or <c>merge</c> once queries combine sources).</summary>
   public string? Site { get; init; }

   /// <summary>The names of the columns it produces.</summary>
   public required IReadOnlyList<string> Columns { get; init; }

   /// <summary>A guess at how many rows it produces, when there is one to make.</summary>
   public long? EstimatedRows { get; init; }

   public IReadOnlyList<ExplainNode> Inputs { get; init; } = [];

   /// <summary>The plans of subqueries in its expressions, numbered as the details refer to them.</summary>
   public IReadOnlyList<ExplainSubquery> Subqueries { get; init; } = [];
}

/// <summary>A subquery of an operator's expression: <c>(subquery 1)</c> in a detail is the plan numbered 1.</summary>
public sealed record ExplainSubquery(int Number, string Kind, ExplainNode Plan);

/// <summary>The SQL one source runs for the query.</summary>
public sealed class ExplainFragment
{
   /// <summary>The source's alias.</summary>
   public required string Source { get; init; }

   public required string Dialect { get; init; }

   public required string Sql { get; init; }

   public required IReadOnlyList<ExplainParameter> Parameters { get; init; }

   /// <summary>How the fragment's rows are used: the whole result when one source answers the query, else how they are fetched into the merge engine.</summary>
   public required string Strategy { get; init; }

   /// <summary>The merge table the rows are loaded into (<c>f1</c>), as the merge SQL names it; null for the whole result.</summary>
   public string? Table { get; init; }

   /// <summary>A guess at how many rows the fragment returns, when there is one to make.</summary>
   public long? EstimatedRows { get; init; }

   /// <summary>For a fragment fetched by the keys of another, the SQL it runs for each batch of keys; null otherwise.</summary>
   public string? BindJoinTemplate { get; init; }
}

/// <summary>A parameter of a fragment's SQL: its name in the SQL, type, and value in query syntax (<c>'open'</c>, <c>$since</c>).</summary>
public sealed record ExplainParameter(string Name, string Type, string Value);

/// <summary>The plan after one optimizer phase (or as lowered), with the rules that changed it.</summary>
public sealed record ExplainPhase(string Name, IReadOnlyList<string> Rules, string Plan);
