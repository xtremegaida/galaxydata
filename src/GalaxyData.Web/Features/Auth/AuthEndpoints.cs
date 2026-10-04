using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Features.Auth;

/// <summary>Who is signed in: whether anyone is, and if so who, with what role, and what they may do now.</summary>
public sealed record SessionDto(bool SignedIn, SessionUserDto? User);

public sealed record SessionUserDto(int Id, string UserName, string? DisplayName, UserRole Role, bool MustChangePassword, PermissionsDto Permissions);

/// <summary>What the user may do now: nothing, while their password must be changed.</summary>
public sealed record PermissionsDto(bool CanRead, bool CanEditData, bool CanAdmin);

public sealed record SignInRequest([Required] string UserName, [Required] string Password);

public sealed record ChangePasswordRequest([Required] string CurrentPassword, [Required] string NewPassword);

/// <summary>What a password must be: as long as these say, and not holding the user's name.</summary>
public sealed record PasswordPolicyDto(int MinimumLength, int MaximumLength);

/// <summary>
/// Signing in and out, and changing one's own password. The session (anyone may ask) gives the anti-forgery token
/// every request that changes anything needs, sign-in included.
/// </summary>
public static partial class AuthEndpoints
{
   private static readonly SessionDto SignedOut = new(false, null);

   /// <summary>A hash to check passwords of unknown users against, so they take as long as known users' do.</summary>
   private static string? unknownUserHash;

   public static RouteGroupBuilder MapAuth(this RouteGroupBuilder api)
   {
      RouteGroupBuilder auth = api.MapGroup("/auth").WithTags("Auth");
      auth.MapGet("/session", GetSessionAsync)
         .WithName("GetSession")
         .WithSummary("Who is signed in; gives the anti-forgery token (the XSRF-TOKEN cookie)")
         .AllowAnonymous();
      auth.MapPost("/sign-in", SignInAsync)
         .WithName("SignIn")
         .WithSummary("Signs in, starting a session (the gd.auth cookie)")
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden)
         .ProducesProblem(StatusCodes.Status429TooManyRequests)
         .AllowAnonymous()
         .RequireRateLimiting(AuthSetup.SignInLimit);
      auth.MapPost("/sign-out", SignOutAsync)
         .WithName("SignOut")
         .WithSummary("Ends the session")
         .AllowAnonymous();
      auth.MapPost("/change-password", ChangePasswordAsync)
         .WithName("ChangePassword")
         .WithSummary("Changes the signed-in user's password; their other sessions end")
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
         .ProducesProblem(StatusCodes.Status429TooManyRequests)
         .RequireAuthorization(Policies.SignedIn)
         .RequireRateLimiting(AuthSetup.SignInLimit);
      auth.MapGet("/password-policy", GetPasswordPolicy)
         .WithName("GetPasswordPolicy")
         .WithSummary("What a password must be, for forms to say before it is sent")
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .RequireAuthorization(Policies.SignedIn);
      return api;
   }

   private static async Task<Ok<SessionDto>> GetSessionAsync(HttpContext context, MetadataDb db, IAntiforgery antiforgery, CancellationToken cancellationToken)
   {
      SessionDto session = SignedOut;
      if (context.User.UserId() is int id && await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken) is { } user)
      {
         session = Session(user);
      }
      Xsrf.Issue(context, antiforgery);
      context.Response.Headers.CacheControl = "no-store";
      return TypedResults.Ok(session);
   }

   private static async Task<Results<Ok<SessionDto>, ProblemHttpResult>> SignInAsync(SignInRequest request, HttpContext context, MetadataDb db,
      IPasswordHasher<AppUser> hasher, IAntiforgery antiforgery, IOptions<GalaxyDataOptions> options, TimeProvider clock, ILoggerFactory logging,
      CancellationToken cancellationToken)
   {
      ILogger logger = logging.CreateLogger(typeof(AuthEndpoints));
      AuthSettings settings = options.Value.Auth;
      DateTime now = clock.GetUtcNow().UtcDateTime;
      string name = request.UserName.Trim();
      AppUser? user = await db.Users.SingleOrDefaultAsync(u => u.UserName == name, cancellationToken);
      if (user == null)
      {
         unknownUserHash ??= hasher.HashPassword(new AppUser(), Guid.NewGuid().ToString());
         hasher.VerifyHashedPassword(new AppUser(), unknownUserHash, request.Password);
         LogUnknownUser(logger, UserNames.ForLog(name));
         return InvalidCredentials();
      }
      if (user.LockedOutUntil is { } until && until > now)
      {
         LogLockedOut(logger, user.UserName);
         return ApiProblems.Result(StatusCodes.Status401Unauthorized, ProblemCodes.LockedOut, "Too many failed sign-ins",
            $"Try again in {Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes))} minutes, or ask an administrator to unlock the account");
      }
      PasswordVerificationResult verified = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
      if (verified == PasswordVerificationResult.Failed)
      {
         // Counted in the database, so sign-ins at once all count; the version isn't changed, as an administrator's edits aren't.
         await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedSignIns, u => u.FailedSignIns + 1), cancellationToken);
         int locked = await db.Users.Where(u => u.Id == user.Id && u.FailedSignIns >= settings.MaxFailedSignIns)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedSignIns, 0).SetProperty(u => u.LockedOutUntil, now + settings.LockoutDuration), cancellationToken);
         if (locked > 0) { LogLockingOut(logger, user.UserName, settings.LockoutDuration); }
         else { LogWrongPassword(logger, user.UserName); }
         return InvalidCredentials();
      }
      if (user.IsDisabled)
      {
         LogDisabled(logger, user.UserName);
         return ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.AccountDisabled, "The account is disabled", "Ask an administrator to enable it");
      }
      string hash = verified == PasswordVerificationResult.SuccessRehashNeeded ? hasher.HashPassword(user, request.Password) : user.PasswordHash;
      await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s
         .SetProperty(u => u.FailedSignIns, 0)
         .SetProperty(u => u.LockedOutUntil, (DateTime?)null)
         .SetProperty(u => u.LastSignInAt, now)
         .SetProperty(u => u.PasswordHash, hash), cancellationToken);
      ClaimsPrincipal principal = UserClaims.Principal(user);
      await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = false });
      Xsrf.Issue(context, antiforgery, principal);
      LogSignedIn(logger, user.UserName);
      return TypedResults.Ok(Session(user));
   }

   private static async Task<Ok<SessionDto>> SignOutAsync(HttpContext context, IAntiforgery antiforgery)
   {
      await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
      Xsrf.Issue(context, antiforgery, new ClaimsPrincipal(new ClaimsIdentity()));
      return TypedResults.Ok(SignedOut);
   }

   private static Ok<PasswordPolicyDto> GetPasswordPolicy(PasswordPolicy policy) =>
      TypedResults.Ok(new PasswordPolicyDto(policy.MinimumLength, PasswordPolicy.MaximumLength));

   private static async Task<Results<Ok<SessionDto>, ProblemHttpResult>> ChangePasswordAsync(ChangePasswordRequest request, HttpContext context, MetadataDb db,
      IPasswordHasher<AppUser> hasher, PasswordPolicy policy, SessionValidator sessions, IAntiforgery antiforgery, TimeProvider clock, ILoggerFactory logging,
      CancellationToken cancellationToken)
   {
      int id = context.User.RequiredUserId();
      AppUser? user = await db.Users.FindAsync([id], cancellationToken);
      if (user == null) { return ApiProblems.Result(StatusCodes.Status401Unauthorized, ProblemCodes.Unauthenticated, "No one is signed in"); }
      if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
      {
         return ApiProblems.Result(StatusCodes.Status422UnprocessableEntity, ProblemCodes.WrongPassword, "The current password isn't right");
      }
      if (policy.Problem(request.NewPassword, user.UserName) is { } problem)
      {
         return ApiProblems.Result(StatusCodes.Status422UnprocessableEntity, ProblemCodes.WeakPassword, "The new password won't do", problem);
      }
      if (string.Equals(request.NewPassword, request.CurrentPassword, StringComparison.Ordinal))
      {
         return ApiProblems.Result(StatusCodes.Status422UnprocessableEntity, ProblemCodes.WeakPassword, "The new password won't do", "It must differ from the current one");
      }
      user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
      user.MustChangePassword = false;
      user.PasswordChangedAt = clock.GetUtcNow().UtcDateTime;
      user.SecurityStamp = AppUser.NewStamp();
      await db.SaveChangesAsync(cancellationToken);
      sessions.Forget(id);
      // This session goes on, with the new stamp; the user's others end.
      ClaimsPrincipal principal = UserClaims.Principal(user);
      await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = false });
      Xsrf.Issue(context, antiforgery, principal);
      LogPasswordChanged(logging.CreateLogger(typeof(AuthEndpoints)), user.UserName);
      return TypedResults.Ok(Session(user));
   }

   internal static SessionDto Session(AppUser user)
   {
      (bool canRead, bool canEditData, bool canAdmin) = Policies.Of(user.Role, user.MustChangePassword);
      return new SessionDto(true, new SessionUserDto(user.Id, user.UserName, user.DisplayName, user.Role, user.MustChangePassword,
         new PermissionsDto(canRead, canEditData, canAdmin)));
   }

   private static ProblemHttpResult InvalidCredentials() =>
      ApiProblems.Result(StatusCodes.Status401Unauthorized, ProblemCodes.InvalidCredentials, "The user name or password isn't right");

   [LoggerMessage(Level = LogLevel.Information, Message = "{UserName} signed in")]
   private static partial void LogSignedIn(ILogger logger, string userName);

   [LoggerMessage(Level = LogLevel.Warning, Message = "A sign-in as {UserName}, who isn't a user, failed")]
   private static partial void LogUnknownUser(ILogger logger, string userName);

   [LoggerMessage(Level = LogLevel.Warning, Message = "A sign-in as {UserName} failed: the password wasn't right")]
   private static partial void LogWrongPassword(ILogger logger, string userName);

   [LoggerMessage(Level = LogLevel.Warning, Message = "{UserName} is locked out for {Duration}, after too many failed sign-ins")]
   private static partial void LogLockingOut(ILogger logger, string userName, TimeSpan duration);

   [LoggerMessage(Level = LogLevel.Warning, Message = "A sign-in as {UserName}, who is locked out, was refused")]
   private static partial void LogLockedOut(ILogger logger, string userName);

   [LoggerMessage(Level = LogLevel.Warning, Message = "A sign-in as {UserName}, who is disabled, was refused")]
   private static partial void LogDisabled(ILogger logger, string userName);

   [LoggerMessage(Level = LogLevel.Information, Message = "{UserName} changed their password")]
   private static partial void LogPasswordChanged(ILogger logger, string userName);
}
