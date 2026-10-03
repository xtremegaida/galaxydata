using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace GalaxyData.Web.Catalog;

/// <summary>
/// Gives every API answer the catalog's version (<see cref="CatalogService.VersionHeader"/>): the one the answer was
/// made with, or, for answers that didn't read the catalog, the latest built when nothing changed since, so clients
/// learn when to read the catalog again.
/// </summary>
public sealed class CatalogVersionFilter(CatalogService catalog) : IEndpointFilter
{
   public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
   {
      ArgumentNullException.ThrowIfNull(context);
      ArgumentNullException.ThrowIfNull(next);
      object? result = await next(context);
      HttpResponse response = context.HttpContext.Response;
      if (!response.HasStarted && !response.Headers.ContainsKey(CatalogService.VersionHeader) && catalog.Fresh is { } fresh)
      {
         response.Headers[CatalogService.VersionHeader] = fresh.Version;
      }
      return result;
   }
}
