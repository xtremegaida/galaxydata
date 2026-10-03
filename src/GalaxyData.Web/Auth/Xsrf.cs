using System;
using System.Security.Claims;
using System.Threading.Tasks;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace GalaxyData.Web.Auth;

/// <summary>
/// Requests that change anything carry an anti-forgery token: the client reads it from the <c>XSRF-TOKEN</c> cookie
/// (as Angular's HttpClient does) and sends it back in the <c>X-XSRF-TOKEN</c> header. The token belongs to the
/// user it was made for, so it is made again whenever who is signed in changes: at sign-in, sign-out and password
/// changes, and whenever the client asks for the session.
/// </summary>
public static class Xsrf
{
   public const string CookieName = "XSRF-TOKEN";

   public const string HeaderName = "X-XSRF-TOKEN";

   /// <summary>The cookie antiforgery checks the token against (HttpOnly).</summary>
   public const string AntiforgeryCookieName = "gd.xsrf";

   /// <summary>Gives the client a token for <paramref name="user"/> (who is signed in from now on), or for the request's user.</summary>
   public static void Issue(HttpContext context, IAntiforgery antiforgery, ClaimsPrincipal? user = null)
   {
      ArgumentNullException.ThrowIfNull(context);
      ArgumentNullException.ThrowIfNull(antiforgery);
      if (user != null) { context.User = user; }
      AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
      context.Response.Cookies.Append(CookieName, tokens.RequestToken!, new CookieOptions
      {
         HttpOnly = false,
         SameSite = SameSiteMode.Strict,
         Secure = context.Request.IsHttps,
         Path = "/",
         IsEssential = true,
      });
   }
}

/// <summary>Refuses a request that changes anything (POST, PUT, PATCH, DELETE) without a valid anti-forgery token: 400 <c>xsrf-token-invalid</c>.</summary>
internal sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
   public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
   {
      ArgumentNullException.ThrowIfNull(context);
      ArgumentNullException.ThrowIfNull(next);
      string method = context.HttpContext.Request.Method;
      if ((HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method))
          && !await antiforgery.IsRequestValidAsync(context.HttpContext))
      {
         return ApiProblems.Result(StatusCodes.Status400BadRequest, ProblemCodes.XsrfTokenInvalid, "The request's anti-forgery token is missing or isn't valid",
            $"Ask for the session (GET /api/auth/session), which gives a token in the {Xsrf.CookieName} cookie, and send it in the {Xsrf.HeaderName} header");
      }
      return await next(context);
   }
}
