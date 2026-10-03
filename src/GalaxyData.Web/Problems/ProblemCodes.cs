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
