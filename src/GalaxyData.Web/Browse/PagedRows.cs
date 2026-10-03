using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Results;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GalaxyData.Web.Browse;

/// <summary>A page's rows: those of the page, whether more follow, how many there are (when counted in time), and what running it took.</summary>
public sealed record FetchedPage(List<object?[]> Rows, bool More, long? Total, ExecutionStats Stats);

/// <summary>What pages of rows share, browsed or queried: fetching a page with its count alongside, and describing what it is.</summary>
internal static class PagedRows
{
   /// <summary>
   /// The rows of <paramref name="page"/> (prepared for one more than <paramref name="limit"/>, to know whether more
   /// follow), counted alongside within <paramref name="countTimeout"/> when <paramref name="count"/> says. The last
   /// page tells the count, so it doesn't wait for it.
   /// </summary>
   public static async Task<FetchedPage> FetchAsync(QueryEngine engine, PreparedQuery page, ComposedQuery composed, int limit, long offset, bool count,
                                                    TimeSpan countTimeout, CancellationToken cancellationToken)
   {
      using CancellationTokenSource counting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      Task<long?> counted = count ? CountAsync(engine, composed, countTimeout, counting.Token) : Task.FromResult<long?>(null);
      List<object?[]> rows;
      ExecutionStats stats;
      try
      {
         await using QueryResult result = await page.ExecuteAsync(cancellationToken);
         rows = [.. await result.ToListAsync(cancellationToken)];
         stats = result.Stats;
      }
      catch
      {
         // The page's problem is the one to tell; the count's, if it has one, goes with it.
         await counting.CancelAsync();
         await Quietly(counted);
         throw;
      }
      bool more = rows.Count > limit;
      if (more) { rows.RemoveAt(rows.Count - 1); }
      long? total;
      if (count && !more && (rows.Count > 0 || offset == 0))
      {
         total = offset + rows.Count;
         await counting.CancelAsync();
         await Quietly(counted);
      }
      else
      {
         total = await counted;
      }
      return new FetchedPage(rows, more, total, stats);
   }

   /// <summary>How many rows the grid's query gives; null when counting them takes longer than <paramref name="limit"/>.</summary>
   private static async Task<long?> CountAsync(QueryEngine engine, ComposedQuery composed, TimeSpan limit, CancellationToken cancellationToken)
   {
      PreparedQuery count = engine.Prepare(new QueryRequest(composed.Text) { Parameters = composed.Parameters, Timeout = limit }).ForCount();
      if (!count.Success) { return null; }
      try
      {
         await using QueryResult result = await count.ExecuteAsync(cancellationToken);
         IReadOnlyList<object?[]> rows = await result.ToListAsync(cancellationToken);
         return rows.Count == 1 ? Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture) : null;
      }
      catch (Exception e) when (e is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
      {
         // Out of time, or failed where the page didn't (rows fetched past the limit, a source busy): the rows come
         // all the same, without the count.
         return null;
      }
   }

   /// <summary>Waits for a count that is no longer wanted, whatever became of it.</summary>
   private static async Task Quietly(Task<long?> count)
   {
      try
      {
         await count;
      }
      catch (Exception e) when (e is not OutOfMemoryException)
      {
         // Cancelled, or failed with the page.
      }
   }

   public static LineageDto Lineage(ColumnLineage lineage) =>
      new(lineage.Kind, lineage.Sources.Select(s => new LineageSourceDto(s.Column.ToString(), s.Path.Count == 0 ? null : s.PathText)).ToList(), lineage.ExpressionText);

   public static List<QueryParameterDto> Parameters(QueryParameters parameters) =>
      parameters.All.Select(p => new QueryParameterDto(p.Name, p.Type.ToString(), ValueCodec.Encode(p.Value, p.Type))).ToList();

   /// <summary>A query that can't run: its diagnostics, placed in the user's where expression when they are about it.</summary>
   public static IResult QueryProblem(IReadOnlyList<QueryDiagnostic> diagnostics, string text, int? whereStart = null, string? where = null)
   {
      if (whereStart is int start && where != null)
      {
         int end = start + where.Length;
         List<QueryDiagnostic> inWhere = diagnostics.Where(d => d.IsError && d.Start >= start && d.Start <= end).ToList();
         if (inWhere.Count > 0)
         {
            ProblemDetails problem = ApiProblems.ForDiagnostics(inWhere.Select(d => d with { Start = d.Start - start, End = Math.Min(d.End, end) - start }).ToList());
            problem.Extensions["field"] = "grid.where";
            return TypedResults.Problem(problem);
         }
      }
      ProblemDetails other = ApiProblems.ForDiagnostics(diagnostics);
      other.Extensions["queryText"] = text;
      return TypedResults.Problem(other);
   }
}
