using System;

namespace GalaxyData.Web.Problems;

/// <summary>
/// A request the application can't carry out, thrown where returning a result would mean passing it up through
/// layers that don't answer requests; the API answers with it as a problem (<see cref="ApiProblems"/>).
/// </summary>
public sealed class ApiException(int status, string code, string title, string? detail = null) : Exception(detail ?? title)
{
   public int Status { get; } = status;

   public string Code { get; } = code;

   public string Title { get; } = title;

   public string? Detail { get; } = detail;
}
