namespace GalaxyData.Web.Problems;

/// <summary>
/// The <c>code</c> of every problem the API answers with (RFC 9457 problem details): what clients tell problems
/// apart by. Problems the application describes have codes of their own; the rest have their status's.
/// </summary>
public static class ProblemCodes
{
   /// <summary>400: the query's text doesn't parse; its diagnostics say where.</summary>
   public const string QuerySyntax = "query-syntax";

   /// <summary>422: the query parses, but doesn't bind or plan; its diagnostics say where.</summary>
   public const string QueryInvalid = "query-invalid";

   /// <summary>422: the query failed as it ran (a database error, or a value that didn't convert).</summary>
   public const string QueryFailed = "query-failed";

   /// <summary>504: the query ran longer than it may, and was stopped.</summary>
   public const string QueryTimeout = "query-timeout";

   /// <summary>502: a source couldn't be connected to.</summary>
   public const string SourceUnavailable = "source-unavailable";

   /// <summary>422: an edited script can't run; its problems say where.</summary>
   public const string ScriptInvalid = "script-invalid";

   /// <summary>409: what was changed had been changed by someone else since it was read (its version).</summary>
   public const string ConcurrencyConflict = "concurrency-conflict";

   /// <summary>409: a preview can't be committed: it expired or was replaced, or the changes or the catalog changed since; preview again.</summary>
   public const string PlanStale = "plan-stale";

   /// <summary>409: the user's changes are being committed: they can't be previewed or committed again until that has finished.</summary>
   public const string CommitInProgress = "commit-in-progress";

   /// <summary>400: a request that changes anything came without a valid anti-forgery token.</summary>
   public const string XsrfTokenInvalid = "xsrf-token-invalid";

   /// <summary>400: the request's values aren't valid; <c>errors</c> says which.</summary>
   public const string InvalidRequest = "invalid-request";

   /// <summary>401: no user has that name and password.</summary>
   public const string InvalidCredentials = "invalid-credentials";

   /// <summary>401: too many failed sign-ins; the user may try again later.</summary>
   public const string LockedOut = "locked-out";

   /// <summary>403: the user is disabled.</summary>
   public const string AccountDisabled = "account-disabled";

   /// <summary>403: the user must change their password before anything else.</summary>
   public const string PasswordChangeRequired = "password-change-required";

   /// <summary>422: the current password given to change it isn't right.</summary>
   public const string WrongPassword = "wrong-password";

   /// <summary>422: a new password doesn't meet the policy.</summary>
   public const string WeakPassword = "weak-password";

   /// <summary>409: another user has the name.</summary>
   public const string UserNameTaken = "user-name-taken";

   /// <summary>409: the owner has a saved query of the name.</summary>
   public const string QueryNameTaken = "query-name-taken";

   /// <summary>409: another connection has the alias.</summary>
   public const string AliasTaken = "alias-taken";

   /// <summary>409: the overlay has an item for the same thing: settings for the entity, an override of the navigation, a virtual entity of the name.</summary>
   public const string OverlayItemExists = "overlay-item-exists";

   /// <summary>409: administrators can't demote, disable, delete or reset themselves.</summary>
   public const string OwnAccount = "own-account";

   /// <summary>409: the change would leave no enabled administrator.</summary>
   public const string LastAdmin = "last-admin";

   public const string BadRequest = "bad-request";

   public const string Unauthenticated = "unauthenticated";

   public const string Forbidden = "forbidden";

   public const string NotFound = "not-found";

   public const string MethodNotAllowed = "method-not-allowed";

   public const string Conflict = "conflict";

   public const string UnsupportedMediaType = "unsupported-media-type";

   public const string Unprocessable = "unprocessable";

   public const string TooManyRequests = "too-many-requests";

   public const string InternalError = "internal-error";

   /// <summary>The code of a problem with no code of its own: its status's.</summary>
   public static string ForStatus(int status) => status switch
   {
      400 => BadRequest,
      401 => Unauthenticated,
      403 => Forbidden,
      404 => NotFound,
      405 => MethodNotAllowed,
      409 => Conflict,
      415 => UnsupportedMediaType,
      422 => Unprocessable,
      429 => TooManyRequests,
      >= 500 => InternalError,
      _ => "error",
   };
}
