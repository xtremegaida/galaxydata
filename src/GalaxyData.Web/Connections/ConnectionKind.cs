using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GalaxyData.Web.Connections;

public enum FieldType
{
   Text,
   Number,

   /// <summary>A secret: never shown again, kept, set or cleared.</summary>
   Password,
   Bool,

   /// <summary>One of <see cref="FieldDto.Choices"/>.</summary>
   Select,

   /// <summary>A file on the application's machine, in an allowed folder.</summary>
   FilePath,

   /// <summary>A folder on the application's machine, in an allowed folder.</summary>
   FolderPath,

   /// <summary>The keywords the other fields don't cover, and their values.</summary>
   KeyValues,
}

public sealed record ChoiceDto(string Value, string Label);

/// <summary>A field shown only while <see cref="Field"/>'s value (its default, when it has none) is one of <see cref="Values"/>.</summary>
public sealed record VisibleWhenDto(string Field, IReadOnlyList<string> Values);

public sealed record GroupDto(string Key, string Label, bool Collapsed);

/// <summary>
/// A field of a connection's form. For settings, <see cref="Key"/> is the provider's keyword, as its connection
/// string builder names it; values are text, as in a connection string (<c>5432</c>, <c>true</c>).
/// </summary>
public sealed record FieldDto(string Key, string Label, FieldType Type)
{
   public bool Required { get; init; }

   public string? Default { get; init; }

   public string? Placeholder { get; init; }

   public string? Help { get; init; }

   public string? Group { get; init; }

   public IReadOnlyList<ChoiceDto>? Choices { get; init; }

   public VisibleWhenDto? VisibleWhen { get; init; }

   public int? Min { get; init; }

   public int? Max { get; init; }
}

/// <summary>A kind of connection, as its form shows it: its settings' fields, the source's options, and whether it has a connection string.</summary>
public sealed record ConnectionKindDto(string Id, string DisplayName, bool SupportsRaw, bool AlwaysReadOnly, string? RawExample,
                                       IReadOnlyList<GroupDto> Groups, IReadOnlyList<FieldDto> Fields, IReadOnlyList<FieldDto> Options);

/// <summary>
/// A kind of source the application connects to: its form (<see cref="Fields"/>, <see cref="Options"/>), which of
/// its keywords are secrets, paths, or the application's own to set, how its connection string is written, and how
/// a connection is tried. Settings are the keywords and values the provider's connection string builder names;
/// one that it doesn't take is refused with its message.
/// </summary>
public abstract class ConnectionKind
{
   public const string OtherSettings = "otherSettings";

   public const string TrustForeignKeysOption = "trustForeignKeys";

   protected static readonly IReadOnlyList<GroupDto> StandardGroups =
   [
      new("connection", "Connection", false),
      new("security", "Security", false),
      new("advanced", "Advanced", true),
   ];

   protected static readonly FieldDto TrustForeignKeys = new(TrustForeignKeysOption, "Trust foreign keys", FieldType.Bool)
   {
      Default = "false",
      Help = "Treat the source's foreign keys as kept even where the database doesn't check them, so a navigation along one finds exactly one row",
   };

   protected static readonly FieldDto Other = new(OtherSettings, "Other settings", FieldType.KeyValues)
   {
      Group = "advanced",
      Help = "Any other keyword the provider takes, and its value",
   };

   private static readonly string[] SecretWords = ["password", "pwd", "token", "secret"];

   public abstract string Id { get; }

   public abstract string DisplayName { get; }

   /// <summary>Whether the settings may be edited as a connection string.</summary>
   public virtual bool SupportsRaw => true;

   /// <summary>Whether the source is always read-only (a folder of workbooks).</summary>
   public virtual bool AlwaysReadOnly => false;

   public virtual string? RawExample => null;

   public virtual IReadOnlyList<GroupDto> Groups => StandardGroups;

   public abstract IReadOnlyList<FieldDto> Fields { get; }

   public virtual IReadOnlyList<FieldDto> Options => [TrustForeignKeys];

   /// <summary>The keywords whose values are files or folders on the application's machine, which must be in an allowed folder.</summary>
   public virtual IReadOnlyList<string> PathKeywords => Fields.Where(f => f.Type is FieldType.FilePath or FieldType.FolderPath).Select(f => f.Key).ToList();

   /// <summary>Keywords the application sets itself (from whether the connection is read-only), or won't have set.</summary>
   protected virtual IReadOnlyList<string> Reserved => [];

   /// <summary>The kind's form; groups no field is in are left out.</summary>
   public ConnectionKindDto Describe() =>
      new(Id, DisplayName, SupportsRaw, AlwaysReadOnly, RawExample, Groups.Where(g => Fields.Any(f => f.Group == g.Key)).ToList(), Fields, Options);

   /// <summary>Whether a keyword's value is a secret: a password, a token.</summary>
   public virtual bool IsSecret(string keyword)
   {
      ArgumentNullException.ThrowIfNull(keyword);
      return SecretWords.Any(w => keyword.Contains(w, StringComparison.OrdinalIgnoreCase));
   }

   /// <summary>The reserved keyword among <paramref name="keywords"/>, if there is one.</summary>
   public string? ReservedIn(IEnumerable<string> keywords) => keywords.FirstOrDefault(k => Reserved.Contains(k, StringComparer.OrdinalIgnoreCase));

   /// <summary>The label a keyword's field has, or the keyword itself.</summary>
   public string Label(string keyword) => Fields.FirstOrDefault(f => string.Equals(f.Key, keyword, StringComparison.OrdinalIgnoreCase))?.Label ?? keyword;

   protected abstract DbConnectionStringBuilder NewBuilder();

   /// <summary>The keywords and values, as the provider names them; ArgumentException (or FormatException) for one it doesn't take.</summary>
   public virtual List<KeyValuePair<string, string>> Normalize(IEnumerable<KeyValuePair<string, string>> pairs)
   {
      ArgumentNullException.ThrowIfNull(pairs);
      DbConnectionStringBuilder builder = NewBuilder();
      foreach ((string key, string value) in pairs) { builder[key] = value; }
      return ConnectionStrings.Pairs(builder.ConnectionString);
   }

   /// <summary>A connection string's keywords and values, as the provider names them; ArgumentException for what it doesn't take.</summary>
   public virtual List<KeyValuePair<string, string>> Parse(string connectionString)
   {
      DbConnectionStringBuilder builder = NewBuilder();
      builder.ConnectionString = connectionString;
      return ConnectionStrings.Pairs(builder.ConnectionString);
   }

   /// <summary>The settings as a connection string to show, each of <paramref name="secretKeywords"/> masked.</summary>
   public virtual string Display(IReadOnlyDictionary<string, string> settings, IEnumerable<string> secretKeywords)
   {
      ArgumentNullException.ThrowIfNull(settings);
      ArgumentNullException.ThrowIfNull(secretKeywords);
      DbConnectionStringBuilder builder = NewBuilder();
      foreach ((string key, string value) in settings) { builder[key] = value; }
      foreach (string key in secretKeywords) { builder[key] = ConnectionStrings.Mask; }
      return builder.ConnectionString;
   }

   /// <summary>The connection string to connect with: the settings, the secrets, and what the application sets.</summary>
   public virtual string ConnectionString(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> secrets, bool readOnly)
   {
      ArgumentNullException.ThrowIfNull(settings);
      ArgumentNullException.ThrowIfNull(secrets);
      DbConnectionStringBuilder builder = NewBuilder();
      foreach ((string key, string value) in settings) { builder[key] = value; }
      foreach ((string key, string value) in secrets) { builder[key] = value; }
      Restrict(builder, readOnly);
      return builder.ConnectionString;
   }

   /// <summary>Sets what the application sets: read-only access, where the provider has it.</summary>
   protected virtual void Restrict(DbConnectionStringBuilder builder, bool readOnly)
   {
   }

   /// <summary>Why the source can't be opened as it is set (its file isn't there); null when it may be.</summary>
   public virtual string? Missing(IReadOnlyDictionary<string, string> settings)
   {
      ArgumentNullException.ThrowIfNull(settings);
      foreach (FieldDto field in Fields.Where(f => f.Type == FieldType.FilePath && f.Required))
      {
         if (settings.TryGetValue(field.Key, out string? path) && !File.Exists(path)) { return $"There is no file {path}"; }
      }
      foreach (FieldDto field in Fields.Where(f => f.Type == FieldType.FolderPath && f.Required))
      {
         if (settings.TryGetValue(field.Key, out string? path) && !Directory.Exists(path)) { return $"There is no folder {path}"; }
      }
      return null;
   }

   /// <summary>Connects with <paramref name="connectionString"/> and runs a statement; what was found, or the failure thrown.</summary>
   public abstract Task<string> ProbeAsync(string connectionString, CancellationToken cancellationToken);
}

/// <summary>The kinds of connection the application has.</summary>
public sealed class ConnectionKinds(IEnumerable<ConnectionKind> kinds)
{
   public IReadOnlyList<ConnectionKind> All { get; } = kinds.ToList();

   public ConnectionKind? Find(string? id) => All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));
}
