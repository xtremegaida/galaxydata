using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.OpenApi;

namespace GalaxyData.Web.Hosting;

/// <summary>What the API's document says of endpoints beyond what their handlers' parameters tell.</summary>
public static class OpenApiConventions
{
   /// <summary>
   /// Says in the API's document that query parameters are required: those a handler takes as nullable, to answer a
   /// problem of its own (by field) when one is missing, but needs all the same. Clients' types then need them.
   /// </summary>
   public static TBuilder RequiresQuery<TBuilder>(this TBuilder builder, params string[] names) where TBuilder : IEndpointConventionBuilder =>
      builder.AddOpenApiOperationTransformer((operation, _, _) =>
      {
         foreach (IOpenApiParameter parameter in operation.Parameters ?? [])
         {
            if (parameter is OpenApiParameter query && query.In == ParameterLocation.Query && names.Contains(query.Name, StringComparer.Ordinal))
            {
               query.Required = true;
            }
         }
         return Task.CompletedTask;
      });
}
