using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Connectors;
using GalaxyData.Web.Metadata;

namespace GalaxyData.Web.Connections;

public enum SecretAction
{
   /// <summary>Keep the secret as it is (none, when there is none).</summary>
   Keep,

   /// <summary>Set it to <see cref="SecretInput.Value"/>.</summary>
   Set,

   /// <summary>Remove it.</summary>
   Clear,
}

/// <summary>What to do with a secret: secrets are never sent back, so an edit keeps, sets or clears each.</summary>
public sealed record SecretInput(SecretAction Action, string? Value = null);

/// <summary>Whether a secret has a value; its value is never sent.</summary>
public sealed record SecretStateDto(bool HasValue);

/// <summary>
/// A connection's settings as an administrator edits them. In the form (<see cref="ConnectionMode.Form"/>),
/// <see cref="Settings"/> holds the keywords and values but secrets, and <see cref="Secrets"/> says what to do with
/// each secret. As a connection string (<see cref="ConnectionMode.Raw"/>), a secret written as <c>********</c>
/// is kept (or set or cleared, as <see cref="Secrets"/> says), one written out is set, and one left out is cleared.
/// </summary>
public sealed record ConnectionInput(
   ConnectionMode Mode = ConnectionMode.Form,
   Dictionary<string, string>? Settings = null,
   Dictionary<string, SecretInput>? Secrets = null,
   string? ConnectionString = null,
   Dictionary<string, string>? Options = null,
   bool IsReadOnly = true);

/// <summary>A connection's settings worked out from what an administrator gave, or the problems with them, by field.</summary>
public sealed class ConnectionResolution
{
   public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

   public Dictionary<string, string> Secrets { get; } = new(StringComparer.OrdinalIgnoreCase);

   public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);

   public bool IsReadOnly { get; set; }

   /// <summary>Problems by field: <c>connectionString</c>, <c>settings</c>, <c>settings.Host</c>, <c>secrets.Password</c>, <c>options.headerRow</c>.</summary>
   public Dictionary<string, string[]> Errors { get; } = new(StringComparer.Ordinal);

   public bool IsValid => Errors.Count == 0;

   internal void Error(string field, string message) =>
      Errors[field] = Errors.TryGetValue(field, out string[]? messages) ? [.. messages, message] : [message];
}

/// <summary>Works out connections' settings from what administrators give, and converts between the form and the connection string.</summary>
public static class ConnectionInputs
{
   /// <summary>
   /// The settings, secrets and options <paramref name="input"/> gives, with the secrets <paramref name="stored"/>
   /// kept where it keeps them; or the problems with them.
   /// </summary>
   public static ConnectionResolution Resolve(ConnectionKind kind, ConnectionInput input, IReadOnlyDictionary<string, string> stored, FileRoots roots)
   {
      ArgumentNullException.ThrowIfNull(kind);
      ArgumentNullException.ThrowIfNull(input);
      ArgumentNullException.ThrowIfNull(stored);
      ArgumentNullException.ThrowIfNull(roots);
      ConnectionResolution resolution = new() { IsReadOnly = kind.AlwaysReadOnly || input.IsReadOnly };
      bool raw = input.Mode == ConnectionMode.Raw;
      string where = raw ? "connectionString" : "settings";
      List<KeyValuePair<string, string>>? pairs = raw ? FromRaw(kind, input, stored, resolution) : FromForm(kind, input, stored, resolution);
      if (pairs == null) { return resolution; }

      List<KeyValuePair<string, string>> normalized;
      try
      {
         normalized = kind.Normalize(pairs);
      }
      catch (Exception e) when (e is ArgumentException or FormatException or InvalidCastException or OverflowException or KeyNotFoundException)
      {
         resolution.Error(where, e.Message);
         return resolution;
      }
      foreach ((string key, string value) in normalized)
      {
         if (kind.IsSecret(key)) { resolution.Secrets[key] = value; }
         else { resolution.Settings[key] = value; }
      }
      if (kind.ReservedIn(normalized.Select(p => p.Key)) is { } reserved)
      {
         resolution.Error(where, $"{reserved} is the application's to set (from whether the connection is read-only)");
      }
      foreach (FieldDto field in kind.Fields.Where(f => f.Required && !resolution.Settings.ContainsKey(f.Key) && !resolution.Secrets.ContainsKey(f.Key)))
      {
         resolution.Error(raw ? where : $"settings.{field.Key}", $"{field.Label} is needed");
      }
      foreach (string keyword in kind.PathKeywords)
      {
         if (resolution.Settings.TryGetValue(keyword, out string? path) && roots.Problem(path) is { } problem)
         {
            resolution.Error(raw ? where : $"settings.{keyword}", $"{kind.Label(keyword)}: {problem}");
         }
      }
      ResolveOptions(kind, input.Options, resolution);
      return resolution;
   }

   private static List<KeyValuePair<string, string>>? FromForm(ConnectionKind kind, ConnectionInput input, IReadOnlyDictionary<string, string> stored,
                                                              ConnectionResolution resolution)
   {
      List<KeyValuePair<string, string>> pairs = [];
      foreach ((string key, string value) in input.Settings ?? [])
      {
         if (kind.IsSecret(key)) { resolution.Error($"settings.{key}", $"{key} is a secret: give it in the secrets"); }
         else if (!string.IsNullOrWhiteSpace(value)) { pairs.Add(new KeyValuePair<string, string>(key.Trim(), value.Trim())); }
      }
      Dictionary<string, SecretInput> given = new(input.Secrets ?? [], StringComparer.OrdinalIgnoreCase);
      foreach (string key in given.Keys.Union(stored.Keys.Where(kind.IsSecret), StringComparer.OrdinalIgnoreCase))
      {
         SecretInput action = given.GetValueOrDefault(key) ?? new SecretInput(SecretAction.Keep);
         switch (action.Action)
         {
            case SecretAction.Set when string.IsNullOrEmpty(action.Value):
               resolution.Error($"secrets.{key}", "Give a value to set, or clear it");
               break;
            case SecretAction.Set:
               pairs.Add(new KeyValuePair<string, string>(key, action.Value!));
               break;
            case SecretAction.Keep when stored.TryGetValue(key, out string? kept):
               pairs.Add(new KeyValuePair<string, string>(key, kept));
               break;
         }
      }
      return resolution.IsValid ? pairs : null;
   }

   private static List<KeyValuePair<string, string>>? FromRaw(ConnectionKind kind, ConnectionInput input, IReadOnlyDictionary<string, string> stored,
                                                             ConnectionResolution resolution)
   {
      if (!kind.SupportsRaw)
      {
         resolution.Error("mode", $"{kind.DisplayName} connections have no connection string");
         return null;
      }
      List<KeyValuePair<string, string>> parsed;
      try
      {
         parsed = kind.Parse(input.ConnectionString ?? string.Empty);
      }
      catch (Exception e) when (e is ArgumentException or FormatException or InvalidCastException or OverflowException or KeyNotFoundException)
      {
         resolution.Error("connectionString", e.Message);
         return null;
      }
      Dictionary<string, SecretInput> given = new(input.Secrets ?? [], StringComparer.OrdinalIgnoreCase);
      List<KeyValuePair<string, string>> pairs = [];
      foreach ((string key, string value) in parsed)
      {
         if (!kind.IsSecret(key) || value != ConnectionStrings.Mask)
         {
            pairs.Add(new KeyValuePair<string, string>(key, value));
            continue;
         }
         SecretInput action = given.GetValueOrDefault(key) ?? new SecretInput(SecretAction.Keep);
         if (action.Action == SecretAction.Set && !string.IsNullOrEmpty(action.Value)) { pairs.Add(new KeyValuePair<string, string>(key, action.Value)); }
         else if (action.Action == SecretAction.Keep && stored.TryGetValue(key, out string? kept)) { pairs.Add(new KeyValuePair<string, string>(key, kept)); }
         else if (action.Action != SecretAction.Clear)
         {
            resolution.Error("connectionString", $"There is no {key} to keep: write it in place of {ConnectionStrings.Mask}");
         }
      }
      return resolution.IsValid ? pairs : null;
   }

   private static void ResolveOptions(ConnectionKind kind, Dictionary<string, string>? options, ConnectionResolution resolution)
   {
      foreach ((string key, string value) in options ?? [])
      {
         FieldDto? field = kind.Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal));
         if (field == null)
         {
            resolution.Error($"options.{key}", $"{kind.DisplayName} connections have no option '{key}'");
            continue;
         }
         switch (field.Type)
         {
            case FieldType.Bool when bool.TryParse(value, out bool flag):
               resolution.Options[key] = flag ? "true" : "false";
               break;
            case FieldType.Select when field.Choices!.Any(c => c.Value == value):
               if (value.Length > 0) { resolution.Options[key] = value; }
               break;
            default:
               resolution.Error($"options.{key}", field.Type == FieldType.Bool
                  ? $"{field.Label} is true or false"
                  : $"{field.Label} is one of {string.Join(", ", field.Choices!.Select(c => c.Value.Length == 0 ? "(none)" : c.Value))}");
               break;
         }
      }
   }

   /// <summary>
   /// The same settings in the other mode: the form's as a connection string (secrets to keep or set written as
   /// <c>********</c>, and their actions kept), or a connection string's as the form's (secrets written out set, those
   /// masked kept, those left out cleared). Secrets come back only as they were given.
   /// </summary>
   public static ConnectionInput Convert(ConnectionKind kind, ConnectionInput input, ConnectionMode to)
   {
      ArgumentNullException.ThrowIfNull(kind);
      ArgumentNullException.ThrowIfNull(input);
      if (!kind.SupportsRaw) { throw new ArgumentException($"{kind.DisplayName} connections have no connection string"); }
      if (input.Mode == to) { return input; }
      Dictionary<string, SecretInput> given = new(input.Secrets ?? [], StringComparer.OrdinalIgnoreCase);
      if (to == ConnectionMode.Raw)
      {
         IEnumerable<KeyValuePair<string, string>> pairs = (input.Settings ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Value) && !kind.IsSecret(p.Key))
            .Select(p => new KeyValuePair<string, string>(p.Key.Trim(), p.Value.Trim()));
         Dictionary<string, string> settings = new(kind.Normalize(pairs), StringComparer.OrdinalIgnoreCase);
         List<string> masked = given.Where(s => s.Value.Action != SecretAction.Clear).Select(s => s.Key).ToList();
         return input with
         {
            Mode = ConnectionMode.Raw,
            Settings = null,
            ConnectionString = kind.Display(settings, masked),
            Secrets = given.Where(s => s.Value.Action != SecretAction.Clear).ToDictionary(s => s.Key, s => s.Value),
         };
      }
      Dictionary<string, string> form = new(StringComparer.OrdinalIgnoreCase);
      Dictionary<string, SecretInput> secrets = new(StringComparer.OrdinalIgnoreCase);
      foreach ((string key, string value) in kind.Parse(input.ConnectionString ?? string.Empty))
      {
         if (!kind.IsSecret(key)) { form[key] = value; }
         else if (value == ConnectionStrings.Mask) { secrets[key] = given.GetValueOrDefault(key) ?? new SecretInput(SecretAction.Keep); }
         else { secrets[key] = new SecretInput(SecretAction.Set, value); }
      }
      foreach (string key in given.Keys.Where(k => !secrets.ContainsKey(k))) { secrets[key] = new SecretInput(SecretAction.Clear); }
      return input with { Mode = ConnectionMode.Form, Settings = form, ConnectionString = null, Secrets = secrets };
   }
}
