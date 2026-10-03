using System;
using System.Collections.Frozen;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GalaxyData.Web.Hosting;

/// <summary>
/// A path of the client's: not the API's (paths under <c>/api</c> that no endpoint has are the API's 404s and 405s),
/// nor a file of the kinds the client is built into (a script, a style sheet, an image or font), which is a 404 when
/// it's missing. Routing's own <c>nonfile</c> won't do: the client's paths have dots in them
/// (<c>/browse/shop.customers</c>).
/// </summary>
internal sealed class ClientPathConstraint : IRouteConstraint
{
   public const string Name = "client";

   private static readonly FrozenSet<string> FileExtensions = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
      ".js", ".mjs", ".css", ".map", ".json", ".txt", ".webmanifest", ".wasm",
      ".ico", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".avif",
      ".woff", ".woff2", ".ttf", ".otf", ".eot");

   public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
   {
      ArgumentNullException.ThrowIfNull(values);
      // The request's own path keeps a slash written as %2F in a value as it is, where the route value has a slash.
      string? path = httpContext?.Request.Path.Value ?? (values.TryGetValue(routeKey, out object? value) ? value as string : null);
      if (path == null) { return true; }
      path = path.TrimStart('/');
      if (path.Equals("api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) { return false; }
      // A segment with matrix parameters (;f=...;row=...) is the client's, whatever its values end with.
      string last = path[(path.LastIndexOf('/') + 1)..];
      return last.Contains(';', StringComparison.Ordinal) || !FileExtensions.Contains(Path.GetExtension(last));
   }
}
