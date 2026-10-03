using System;
using System.Linq;
using System.Threading.Tasks;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace GalaxyData.Web.Auth;

/// <summary>
/// Who may do what. Every endpoint says which policy it needs, or that anyone may call it; one that says neither
/// gets the fallback, a signed-in user with no password to change. A user who must change their password may only
/// do that (<see cref="SignedIn"/>), and is answered 403 <c>password-change-required</c> elsewhere.
/// </summary>
public static class Policies
{
   /// <summary>Signed in, even with a password to change.</summary>
   public const string SignedIn = "SignedIn";

   /// <summary>Reads data and runs queries: every role.</summary>
   public const string CanRead = "CanRead";

   /// <summary>Changes data: data managers and administrators.</summary>
   public const string CanEditData = "CanEditData";

   /// <summary>Users, connections, the overlay and the audit: administrators.</summary>
   public const string CanAdmin = "CanAdmin";

   public static void Add(AuthorizationOptions options)
   {
      ArgumentNullException.ThrowIfNull(options);
      AuthorizationPolicy current = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new CurrentPasswordRequirement()).Build();
      options.FallbackPolicy = current;
      options.AddPolicy(SignedIn, p => p.RequireAuthenticatedUser());
      options.AddPolicy(CanRead, p => p.Combine(current).RequireRole(nameof(UserRole.Read), nameof(UserRole.DataManager), nameof(UserRole.Admin)));
      options.AddPolicy(CanEditData, p => p.Combine(current).RequireRole(nameof(UserRole.DataManager), nameof(UserRole.Admin)));
      options.AddPolicy(CanAdmin, p => p.Combine(current).RequireRole(nameof(UserRole.Admin)));
   }

   /// <summary>What a role may do; nothing while its password must be changed.</summary>
   public static (bool CanRead, bool CanEditData, bool CanAdmin) Of(UserRole role, bool mustChangePassword) =>
      mustChangePassword ? (false, false, false) : (true, role is UserRole.DataManager or UserRole.Admin, role == UserRole.Admin);
}

/// <summary>The user has no password to change.</summary>
internal sealed class CurrentPasswordRequirement : AuthorizationHandler<CurrentPasswordRequirement>, IAuthorizationRequirement
{
   protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CurrentPasswordRequirement requirement)
   {
      ArgumentNullException.ThrowIfNull(context);
      if (context.User.HasClaim(c => c.Type == UserClaims.MustChangePassword))
      {
         context.Fail(new AuthorizationFailureReason(this, "The password must be changed first"));
      }
      else
      {
         context.Succeed(requirement);
      }
      return Task.CompletedTask;
   }
}

/// <summary>Answers a user refused for a password still to change with a problem that says so; the rest as usual (401, 403).</summary>
internal sealed class AuthorizationProblems(IProblemDetailsService problems) : IAuthorizationMiddlewareResultHandler
{
   private readonly AuthorizationMiddlewareResultHandler usual = new();

   public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
   {
      ArgumentNullException.ThrowIfNull(context);
      ArgumentNullException.ThrowIfNull(authorizeResult);
      if (authorizeResult.Forbidden && authorizeResult.AuthorizationFailure?.FailureReasons.Any(r => r.Handler is CurrentPasswordRequirement) == true)
      {
         context.Response.StatusCode = StatusCodes.Status403Forbidden;
         // A client that takes no JSON gets the status alone.
         await problems.TryWriteAsync(new ProblemDetailsContext
         {
            HttpContext = context,
            ProblemDetails = ApiProblems.Create(StatusCodes.Status403Forbidden, ProblemCodes.PasswordChangeRequired, "The password must be changed first",
               "Change it (POST /api/auth/change-password), then try again"),
         });
         return;
      }
      await usual.HandleAsync(next, context, policy, authorizeResult);
   }
}
