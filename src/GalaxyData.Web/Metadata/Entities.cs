using System;

namespace GalaxyData.Web.Metadata;

/// <summary>An entity people edit, which they may have read before another changed it: its version says which they read.</summary>
public interface IVersioned
{
   /// <summary>Goes up by one each time the entity is saved changed; a change made to another version fails (409).</summary>
   int Version { get; set; }
}

public enum UserRole
{
   /// <summary>Reads data and runs queries.</summary>
   Read,

   /// <summary>Reads, and changes data.</summary>
   DataManager,

   /// <summary>Everything, users and connections included.</summary>
   Admin,
}

public sealed class AppUser : IVersioned
{
   public int Id { get; set; }

   /// <summary>Unique, ignoring case.</summary>
   public string UserName { get; set; } = string.Empty;

   public string? DisplayName { get; set; }

   public UserRole Role { get; set; }

   public string PasswordHash { get; set; } = string.Empty;

   /// <summary>Changes whenever the user's sessions must end: a password, role or enabled change.</summary>
   public string SecurityStamp { get; set; } = NewStamp();

   public bool MustChangePassword { get; set; }

   public bool IsDisabled { get; set; }

   /// <summary>Failed sign-ins since the last that succeeded, or the last lockout.</summary>
   public int FailedSignIns { get; set; }

   public DateTime? LockedOutUntil { get; set; }

   public DateTime CreatedAt { get; set; }

   public DateTime? LastSignInAt { get; set; }

   public DateTime PasswordChangedAt { get; set; }

   public int Version { get; set; }

   public static string NewStamp() => Guid.NewGuid().ToString("N");
}

/// <summary>Something an administrator did (or the bootstrap did): who, what, to what, and the details (JSON).</summary>
public sealed class AdminAuditEvent
{
   public long Id { get; set; }

   public DateTime At { get; set; }

   /// <summary>The user who did it; null for the application itself, or a user since deleted.</summary>
   public int? ActorId { get; set; }

   /// <summary>The actor's name when it was done, which outlives the user.</summary>
   public string ActorName { get; set; } = string.Empty;

   /// <summary><c>user.created</c>, <c>user.updated</c>, ...</summary>
   public string Action { get; set; } = string.Empty;

   /// <summary><c>user:alice</c></summary>
   public string Target { get; set; } = string.Empty;

   public string? Details { get; set; }
}

/// <summary>How an administrator edits a connection's settings: field by field, or as a connection string.</summary>
public enum ConnectionMode
{
   Form,
   Raw,
}

/// <summary>Whether a connection's schema has been read (B3).</summary>
public enum SchemaStatus
{
   NotLoaded,
   Loading,
   Ready,
   Failed,
}

/// <summary>
/// A source the application queries: a database, or a folder of workbooks. Its settings are the provider's
/// keywords and values, as its connection string builder names them, without secrets; the secrets (passwords,
/// tokens) are kept apart, protected with the application's data protection keys, for this alias alone.
/// </summary>
public sealed class SourceConnection : IVersioned
{
   public int Id { get; set; }

   /// <summary>How queries name the source (<c>shop</c>); unique ignoring case, and never changed.</summary>
   public string Alias { get; set; } = string.Empty;

   /// <summary>The connection kind: <c>postgres</c>, <c>sqlserver</c>, <c>sqlite</c>, <c>duckdb</c>, <c>excel</c>.</summary>
   public string Kind { get; set; } = string.Empty;

   public string? DisplayName { get; set; }

   public ConnectionMode Mode { get; set; }

   /// <summary>The settings without secrets: a JSON object of keywords and values.</summary>
   public string SettingsJson { get; set; } = "{}";

   /// <summary>The secrets: a JSON object of keywords and values, protected; null when there are none.</summary>
   public string? ProtectedSecrets { get; set; }

   /// <summary>The source's options (foreign keys, a folder's sheets): a JSON object of names and values.</summary>
   public string OptionsJson { get; set; } = "{}";

   public bool IsReadOnly { get; set; }

   public SchemaStatus SchemaStatus { get; set; }

   public DateTime CreatedAt { get; set; }

   public DateTime UpdatedAt { get; set; }

   public int Version { get; set; }
}

/// <summary>A value the application keeps for itself.</summary>
public sealed class MetadataSetting
{
   public string Key { get; set; } = string.Empty;

   public string Value { get; set; } = string.Empty;
}
