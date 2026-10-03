using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Dml;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace GalaxyData.Web.Problems;

/// <summary>A diagnostic of a query, located by the half-open character range [start, end) of its text.</summary>
public sealed record DiagnosticDto(string Code, DiagnosticSeverity Severity, string Message, int Start, int End)
{
   public static DiagnosticDto From(QueryDiagnostic diagnostic)
   {
      ArgumentNullException.ThrowIfNull(diagnostic);
      return new DiagnosticDto(diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Start, diagnostic.End);
   }
}

/// <summary>A problem in the text of an edited script: its line, and the range [start, start + length) of the text.</summary>
public sealed record ScriptProblemDto(string Message, int Line, int Start, int Length);

/// <summary>
/// The problems the API answers with, as RFC 9457 problem details. Each has a <c>code</c> (<see cref="ProblemCodes"/>);
/// problems with a query have its <c>diagnostics</c>, and with a script its <c>problems</c>.
/// </summary>
public static class ApiProblems
{
   public static ProblemDetails Create(int status, string code, string title, string? detail = null) => new()
   {
      Status = status,
      Title = title,
      Detail = detail,
      Extensions = { ["code"] = code },
   };

   /// <summary>A problem to answer with.</summary>
   public static ProblemHttpResult Result(int status, string code, string title, string? detail = null) =>
      TypedResults.Problem(Create(status, code, title, detail));

   /// <summary>
   /// A query that doesn't parse (400, <see cref="ProblemCodes.QuerySyntax"/>) or doesn't bind or plan (422,
   /// <see cref="ProblemCodes.QueryInvalid"/>), with every diagnostic, warnings too.
   /// </summary>
   public static ProblemDetails ForDiagnostics(IReadOnlyList<QueryDiagnostic> diagnostics)
   {
      ArgumentNullException.ThrowIfNull(diagnostics);
      List<QueryDiagnostic> errors = diagnostics.Where(d => d.IsError).ToList();
      bool syntax = errors.Any(d => d.Code.StartsWith("GDQ1", StringComparison.Ordinal));
      ProblemDetails problem = syntax
         ? Create(StatusCodes.Status400BadRequest, ProblemCodes.QuerySyntax, "The query can't be read", errors.FirstOrDefault()?.Message)
         : Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.QueryInvalid, "The query can't run as written", errors.FirstOrDefault()?.Message);
      problem.Extensions["diagnostics"] = diagnostics.Select(DiagnosticDto.From).ToList();
      return problem;
   }

   /// <inheritdoc cref="ForDiagnostics"/>
   public static ProblemHttpResult Diagnostics(IReadOnlyList<QueryDiagnostic> diagnostics) => TypedResults.Problem(ForDiagnostics(diagnostics));

   /// <summary>An edited script that can't run (422, <see cref="ProblemCodes.ScriptInvalid"/>), with its problems.</summary>
   public static ProblemDetails ForScript(DmlScriptException exception)
   {
      ArgumentNullException.ThrowIfNull(exception);
      ProblemDetails problem = Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.ScriptInvalid, $"The script for {exception.Target.Alias} can't run",
         exception.Problems.FirstOrDefault()?.Message);
      problem.Extensions["source"] = exception.Target.Alias;
      problem.Extensions["problems"] = exception.Problems.Select(p => new ScriptProblemDto(p.Message, p.Line, p.Start, p.Length)).ToList();
      return problem;
   }

   /// <summary>A request whose values aren't valid (400, <see cref="ProblemCodes.InvalidRequest"/>): what is wrong, by field as the request names it.</summary>
   public static ValidationProblem Invalid(IDictionary<string, string[]> errors) =>
      TypedResults.ValidationProblem(errors, extensions: new Dictionary<string, object?> { ["code"] = ProblemCodes.InvalidRequest });

   /// <summary>
   /// Gives a problem the code of its status when it has none, so every problem has one. A validation problem
   /// without one is the framework's (ours are <see cref="Invalid"/>), whose fields are named as the request's types
   /// name them (<c>Relation.ToColumns</c>): they are named as the request's JSON does (<c>relation.toColumns</c>).
   /// </summary>
   internal static void Complete(ProblemDetails problem)
   {
      if (problem.Extensions.ContainsKey("code")) { return; }
      if (problem is HttpValidationProblemDetails validation)
      {
         validation.Errors = validation.Errors.ToDictionary(e => CamelCase(e.Key), e => e.Value);
         problem.Extensions["code"] = ProblemCodes.InvalidRequest;
         return;
      }
      problem.Extensions["code"] = ProblemCodes.ForStatus(problem.Status ?? StatusCodes.Status500InternalServerError);
   }

   /// <summary><c>Columns[0].Name</c> as <c>columns[0].name</c>.</summary>
   private static string CamelCase(string path) =>
      string.Join('.', path.Split('.').Select(part => part.Length > 0 && char.IsUpper(part[0]) ? char.ToLowerInvariant(part[0]) + part[1..] : part));
}
