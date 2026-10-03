using System;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GalaxyData.Web.Problems;

/// <summary>
/// Answers a request that failed with an exception with the problem it is: the engine's failures by what went wrong
/// (a source down is 502, a query too slow 504), the rest as 500s that say nothing of what failed but in development.
/// A source's own words, which may name its server, are logged, not answered with.
/// </summary>
internal sealed partial class ApiExceptionHandler(IProblemDetailsService problems, IHostEnvironment environment, ILogger<ApiExceptionHandler> logger)
   : IExceptionHandler
{
   /// <summary>The status nginx made common for a client that went away before it was answered.</summary>
   public const int ClientClosedRequest = 499;

   public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(httpContext);
      ArgumentNullException.ThrowIfNull(exception);
      if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
      {
         // No one is left to answer.
         LogAborted(logger, httpContext.Request.Method, httpContext.Request.Path);
         httpContext.Response.StatusCode = ClientClosedRequest;
         return true;
      }
      ProblemDetails problem = Describe(exception, environment.IsDevelopment());
      Log(exception);
      httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
      // A client that takes no JSON gets the status alone.
      await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception });
      return true;
   }

   /// <summary>The problem an exception is; <paramref name="development"/> tells what an unexpected one was.</summary>
   internal static ProblemDetails Describe(Exception exception, bool development)
   {
      switch (exception)
      {
         case QueryException query:
            return ApiProblems.ForDiagnostics(query.Diagnostics);
         case QueryTimeoutException timeout:
            return ApiProblems.Create(StatusCodes.Status504GatewayTimeout, ProblemCodes.QueryTimeout, "The query took too long", timeout.Message);
         case SourceUnavailableException unavailable:
            ProblemDetails down = ApiProblems.Create(StatusCodes.Status502BadGateway, ProblemCodes.SourceUnavailable, "A source couldn't be reached",
               $"Couldn't connect to {unavailable.Target.Alias}");
            down.Extensions["source"] = unavailable.Target.Alias;
            return down;
         case QueryExecutionException failed:
            return ApiProblems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.QueryFailed, "The query failed", failed.Message);
         case DmlScriptException script:
            return ApiProblems.ForScript(script);
         case DbUpdateConcurrencyException:
            return ApiProblems.Create(StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict, "It was changed by someone else first",
               "Read it again, and make the change again");
         case ApiException api:
            return ApiProblems.Create(api.Status, api.Code, api.Title, api.Detail);
         case BadHttpRequestException bad:
            return ApiProblems.Create(bad.StatusCode, ProblemCodes.ForStatus(bad.StatusCode), "The request isn't one the API takes", bad.Message);
         default:
            return ApiProblems.Create(StatusCodes.Status500InternalServerError, ProblemCodes.InternalError, "Something went wrong",
               development ? exception.ToString() : null);
      }
   }

   private void Log(Exception exception)
   {
      switch (exception)
      {
         case QueryException or ApiException or BadHttpRequestException or DmlScriptException or DbUpdateConcurrencyException:
            LogRefused(logger, exception.Message);
            break;
         case QueryTimeoutException:
            LogTimeout(logger, exception.Message);
            break;
         case SourceUnavailableException unavailable:
            LogUnavailable(logger, unavailable.Target.Alias, unavailable.Reason, exception);
            break;
         case QueryExecutionException:
            LogFailed(logger, exception.Message);
            break;
         default:
            LogUnexpected(logger, exception);
            break;
      }
   }

   [LoggerMessage(Level = LogLevel.Debug, Message = "{Method} {Path}: the client went away before it was answered")]
   private static partial void LogAborted(ILogger logger, string method, PathString path);

   [LoggerMessage(Level = LogLevel.Debug, Message = "Refused: {Reason}")]
   private static partial void LogRefused(ILogger logger, string reason);

   [LoggerMessage(Level = LogLevel.Warning, Message = "{Reason}")]
   private static partial void LogTimeout(ILogger logger, string reason);

   [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't connect to {Source}: {Reason}")]
   private static partial void LogUnavailable(ILogger logger, string source, string reason, Exception exception);

   [LoggerMessage(Level = LogLevel.Information, Message = "A query failed: {Reason}")]
   private static partial void LogFailed(ILogger logger, string reason);

   [LoggerMessage(Level = LogLevel.Error, Message = "A request failed unexpectedly")]
   private static partial void LogUnexpected(ILogger logger, Exception exception);
}
