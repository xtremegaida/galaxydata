using System;
using Microsoft.AspNetCore.Http;

namespace GalaxyData.Web.Problems;

/// <summary>A request that can't be answered as asked: the problem to answer with instead (a validation problem, a query's diagnostics).</summary>
public sealed class ProblemResultException(IResult result) : Exception
{
   public IResult Result { get; } = result;
}
