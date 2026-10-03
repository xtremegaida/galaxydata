using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Features.Audit;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GalaxyData.Web.Features.Users;

/// <summary>A user, as administrators see them; <see cref="LockedOutUntil"/> only while they are.</summary>
public sealed record UserDto(int Id, string UserName, string? DisplayName, UserRole Role, bool IsDisabled, bool MustChangePassword,
                             DateTime? LockedOutUntil, DateTime CreatedAt, DateTime? LastSignInAt, DateTime PasswordChangedAt, int Version);

/// <summary>A new user, who must change the password given at their first sign-in.</summary>
public sealed record CreateUserRequest(
   [Required, RegularExpression(UserNames.Pattern, ErrorMessage = "A user name has letters, digits and . _ @ -, starts with a letter or digit, and has at most 64 characters")]
   string UserName,
   UserRole Role,
   [Required] string Password,
   [StringLength(200)] string? DisplayName = null);

/// <summary>A user's new display name, role and state, made to <see cref="Version"/> of them.</summary>
public sealed record UpdateUserRequest(UserRole Role, bool IsDisabled, int Version, [StringLength(200)] string? DisplayName = null);

public sealed record ResetPasswordRequest([Required] string Password);

/// <summary>
/// Users, for administrators. Changing a user's role, or disabling them, ends their sessions, as resetting their
/// password does. No administrator may demote, disable or delete themselves, nor the last enabled administrator.
/// Every change is in the admin audit.
/// </summary>
public static class UserEndpoints
{
   public static RouteGroupBuilder MapUsers(this RouteGroupBuilder api)
   {
      RouteGroupBuilder users = api.MapGroup("/users")
         .WithTags("Users")
         .RequireAuthorization(Policies.CanAdmin)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      users.MapGet("/", ListAsync).WithName("ListUsers");
      users.MapGet("/{id:int}", GetAsync).WithName("GetUser").ProducesProblem(StatusCodes.Status404NotFound);
      users.MapPost("/", CreateAsync).WithName("CreateUser")
         .ProducesProblem(StatusCodes.Status409Conflict)
         .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
      users.MapPut("/{id:int}", UpdateAsync).WithName("UpdateUser")
         .ProducesProblem(StatusCodes.Status404NotFound)
         .ProducesProblem(StatusCodes.Status409Conflict);
      users.MapDelete("/{id:int}", DeleteAsync).WithName("DeleteUser")
         .ProducesProblem(StatusCodes.Status404NotFound)
         .ProducesProblem(StatusCodes.Status409Conflict);
      users.MapPost("/{id:int}/reset-password", ResetPasswordAsync).WithName("ResetUserPassword")
         .WithSummary("Sets a user's password, which they must change at their next sign-in; their sessions end")
         .ProducesProblem(StatusCodes.Status404NotFound)
         .ProducesProblem(StatusCodes.Status409Conflict)
         .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
      users.MapPost("/{id:int}/unlock", UnlockAsync).WithName("UnlockUser")
         .WithSummary("Lets a user locked out by failed sign-ins sign in again")
         .ProducesProblem(StatusCodes.Status404NotFound);
      return api;
   }

   private static async Task<Ok<List<UserDto>>> ListAsync(MetadataDb db, TimeProvider clock, CancellationToken cancellationToken)
   {
      DateTime now = clock.GetUtcNow().UtcDateTime;
      List<AppUser> users = await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(cancellationToken);
      return TypedResults.Ok(users.Select(u => Dto(u, now)).ToList());
   }

   private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> GetAsync(int id, MetadataDb db, TimeProvider clock, CancellationToken cancellationToken) =>
      await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken) is { } user
         ? TypedResults.Ok(Dto(user, clock.GetUtcNow().UtcDateTime))
         : NoSuchUser(id);

   private static async Task<Results<Created<UserDto>, ProblemHttpResult>> CreateAsync(CreateUserRequest request, ClaimsPrincipal me, MetadataDb db,
      IPasswordHasher<AppUser> hasher, PasswordPolicy policy, TimeProvider clock, CancellationToken cancellationToken)
   {
      if (await db.Users.AnyAsync(u => u.UserName == request.UserName, cancellationToken)) { return NameTaken(request.UserName); }
      if (policy.Problem(request.Password, request.UserName) is { } problem) { return WeakPassword(problem); }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      AppUser user = new()
      {
         UserName = request.UserName,
         DisplayName = Blank(request.DisplayName),
         Role = request.Role,
         MustChangePassword = true,
         CreatedAt = now,
         PasswordChangedAt = now,
      };
      user.PasswordHash = hasher.HashPassword(user, request.Password);
      db.Users.Add(user);
      AdminAudit.Add(db, me, "user.created", AdminAudit.User(user.UserName), new { role = user.Role, displayName = user.DisplayName }, now);
      try
      {
         await db.SaveChangesAsync(cancellationToken);
      }
      catch (DbUpdateException e) when (e.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
      {
         // Made by another administrator at the same moment.
         return NameTaken(request.UserName);
      }
      return TypedResults.Created($"/api/users/{user.Id}", Dto(user, now));
   }

   private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> UpdateAsync(int id, UpdateUserRequest request, ClaimsPrincipal me, MetadataDb db,
      SessionValidator sessions, TimeProvider clock, CancellationToken cancellationToken)
   {
      // The checks of the other administrators and the change are one transaction, which SQLite gives one writer at a time.
      await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
      AppUser? user = await db.Users.FindAsync([id], cancellationToken);
      if (user == null) { return NoSuchUser(id); }
      if (user.Version != request.Version) { return Changed(user); }
      bool demoting = request.Role != UserRole.Admin || request.IsDisabled;
      if (demoting && user.Id == me.RequiredUserId())
      {
         return ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.OwnAccount, "You can't demote or disable yourself", "Another administrator can");
      }
      if (demoting && await IsLastAdminAsync(db, user, cancellationToken)) { return LastAdmin(user, "demoted or disabled"); }

      Dictionary<string, object?> changes = [];
      string? displayName = Blank(request.DisplayName);
      if (!string.Equals(displayName, user.DisplayName, StringComparison.Ordinal)) { changes["displayName"] = new { from = user.DisplayName, to = displayName }; }
      if (request.Role != user.Role) { changes["role"] = new { from = user.Role, to = request.Role }; }
      if (request.IsDisabled != user.IsDisabled) { changes["isDisabled"] = new { from = user.IsDisabled, to = request.IsDisabled }; }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      if (changes.Count == 0) { return TypedResults.Ok(Dto(user, now)); }
      bool endSessions = request.Role != user.Role || request.IsDisabled != user.IsDisabled;
      user.DisplayName = displayName;
      user.Role = request.Role;
      user.IsDisabled = request.IsDisabled;
      if (endSessions) { user.SecurityStamp = AppUser.NewStamp(); }
      AdminAudit.Add(db, me, "user.updated", AdminAudit.User(user.UserName), changes, now);
      await db.SaveChangesAsync(cancellationToken);
      await transaction.CommitAsync(cancellationToken);
      if (endSessions) { sessions.Forget(user.Id); }
      return TypedResults.Ok(Dto(user, now));
   }

   private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(int id, int? version, ClaimsPrincipal me, MetadataDb db, SessionValidator sessions,
      TimeProvider clock, CancellationToken cancellationToken)
   {
      await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
      AppUser? user = await db.Users.FindAsync([id], cancellationToken);
      if (user == null) { return NoSuchUser(id); }
      if (version is { } read && user.Version != read) { return Changed(user); }
      if (user.Id == me.RequiredUserId())
      {
         return ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.OwnAccount, "You can't delete yourself", "Another administrator can");
      }
      if (await IsLastAdminAsync(db, user, cancellationToken)) { return LastAdmin(user, "deleted"); }
      db.Users.Remove(user);
      AdminAudit.Add(db, me, "user.deleted", AdminAudit.User(user.UserName), new { role = user.Role }, clock.GetUtcNow().UtcDateTime);
      await db.SaveChangesAsync(cancellationToken);
      await transaction.CommitAsync(cancellationToken);
      sessions.Forget(user.Id);
      return TypedResults.NoContent();
   }

   private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> ResetPasswordAsync(int id, ResetPasswordRequest request, ClaimsPrincipal me, MetadataDb db,
      IPasswordHasher<AppUser> hasher, PasswordPolicy policy, SessionValidator sessions, TimeProvider clock, CancellationToken cancellationToken)
   {
      AppUser? user = await db.Users.FindAsync([id], cancellationToken);
      if (user == null) { return NoSuchUser(id); }
      if (user.Id == me.RequiredUserId())
      {
         return ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.OwnAccount, "You can't reset your own password",
            "Change it instead (POST /api/auth/change-password)");
      }
      if (policy.Problem(request.Password, user.UserName) is { } problem) { return WeakPassword(problem); }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      user.PasswordHash = hasher.HashPassword(user, request.Password);
      user.MustChangePassword = true;
      user.PasswordChangedAt = now;
      user.FailedSignIns = 0;
      user.LockedOutUntil = null;
      user.SecurityStamp = AppUser.NewStamp();
      AdminAudit.Add(db, me, "user.password-reset", AdminAudit.User(user.UserName), null, now);
      await db.SaveChangesAsync(cancellationToken);
      sessions.Forget(user.Id);
      return TypedResults.Ok(Dto(user, now));
   }

   private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> UnlockAsync(int id, ClaimsPrincipal me, MetadataDb db, TimeProvider clock,
      CancellationToken cancellationToken)
   {
      AppUser? user = await db.Users.FindAsync([id], cancellationToken);
      if (user == null) { return NoSuchUser(id); }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      if (user.LockedOutUntil != null || user.FailedSignIns != 0)
      {
         user.LockedOutUntil = null;
         user.FailedSignIns = 0;
         AdminAudit.Add(db, me, "user.unlocked", AdminAudit.User(user.UserName), null, now);
         await db.SaveChangesAsync(cancellationToken);
      }
      return TypedResults.Ok(Dto(user, now));
   }

   /// <summary>Whether <paramref name="user"/> is the only enabled administrator.</summary>
   private static async Task<bool> IsLastAdminAsync(MetadataDb db, AppUser user, CancellationToken cancellationToken) =>
      user is { Role: UserRole.Admin, IsDisabled: false }
      && !await db.Users.AnyAsync(u => u.Id != user.Id && u.Role == UserRole.Admin && !u.IsDisabled, cancellationToken);

   internal static UserDto Dto(AppUser user, DateTime now) =>
      new(user.Id, user.UserName, user.DisplayName, user.Role, user.IsDisabled, user.MustChangePassword, user.LockedOutUntil > now ? user.LockedOutUntil : null,
         user.CreatedAt, user.LastSignInAt, user.PasswordChangedAt, user.Version);

   private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

   private static ProblemHttpResult NoSuchUser(int id) =>
      ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such user", $"There is no user {id}");

   private static ProblemHttpResult NameTaken(string name) =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.UserNameTaken, "The user name is taken", $"There is a user {name} already (user names ignore case)");

   private static ProblemHttpResult WeakPassword(string problem) =>
      ApiProblems.Result(StatusCodes.Status422UnprocessableEntity, ProblemCodes.WeakPassword, "The password won't do", problem);

   private static ProblemHttpResult LastAdmin(AppUser user, string what) =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.LastAdmin, $"{user.UserName} is the last enabled administrator",
         $"Make another user an administrator before {user.UserName} is {what}");

   private static ProblemHttpResult Changed(AppUser user) =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict, $"{user.UserName} was changed since it was read",
         "Read it again, and make the change again");
}
