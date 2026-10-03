using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace GalaxyData.Web.Hosting;

/// <summary>
/// Masks secrets in what is logged: the values the application knows to be secret (connections' passwords and
/// tokens, as they are protected or read, and the bootstrap password), wherever they stand alone; the values of
/// secret settings, as connection strings and JSON write them (<c>Password=...</c>, <c>pwd=...</c>,
/// <c>"token": "..."</c>); and passwords in URLs (<c>postgres://user:password@host</c>). It is the last line: what
/// is logged shouldn't hold secrets in the first place.
/// </summary>
public sealed class SecretRedactor
{
   public const string Mask = "********";

   /// <summary>
   /// Shorter values aren't masked where they are found: they would mask too much else, and so tell what they are
   /// (a password <c>1234</c> masked in <c>in 1234 ms</c>). A setting's value is masked whatever its length.
   /// </summary>
   public const int MinLength = 6;

   /// <summary>A secret setting and its value: to its end (<c>;</c>, a line break), or quoted. Linear in the text, as any entry may be long.</summary>
   private static readonly Regex SecretSetting = new(
      """(?<key>(?:password|pwd|passwd|secret|token|access[ _]?key|account[ _]?key|api[ _]?key)"?[ \t]*[:=][ \t]*)(?<value>"(?:[^"\r\n]|"")*"|'(?:[^'\r\n]|'')*'|[^;\r\n]+)""",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

   /// <summary>A URL's password: <c>scheme://user:password@</c>.</summary>
   private static readonly Regex UrlPassword = new(
      """(?<head>[a-z][a-z0-9+.-]*://[^/\s:@]*:)(?<value>[^/\s@]+)@""",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

   private readonly Lock gate = new();
   private readonly HashSet<string> secrets = new(StringComparer.Ordinal);

   /// <summary>The secrets, longest first, so one that holds another is masked whole.</summary>
   private volatile string[] known = [];

   /// <summary>A value to mask wherever it stands alone in what is logged.</summary>
   public void Add(string? secret)
   {
      if (secret == null || secret.Length < MinLength) { return; }
      lock (gate)
      {
         if (secrets.Add(secret)) { known = [.. secrets.OrderByDescending(s => s.Length)]; }
      }
   }

   /// <summary>The text with each secret masked; the same instance when it has none.</summary>
   public string Redact(string text)
   {
      ArgumentNullException.ThrowIfNull(text);
      string redacted = text;
      foreach (string secret in known) { redacted = Masked(redacted, secret); }
      if (redacted.Contains('=', StringComparison.Ordinal) || redacted.Contains(':', StringComparison.Ordinal))
      {
         redacted = Replaced(redacted, SecretSetting, m => m.Groups["value"].Value == Mask ? m.Value : m.Groups["key"].Value + Mask);
         redacted = Replaced(redacted, UrlPassword, m => m.Groups["head"].Value + Mask + "@");
      }
      return redacted;
   }

   /// <summary>The text with the secret masked where it stands alone: not within a longer word or number (<c>1234</c> in <c>51234</c>).</summary>
   private static string Masked(string text, string secret)
   {
      int at = text.IndexOf(secret, StringComparison.Ordinal);
      if (at < 0) { return text; }
      System.Text.StringBuilder masked = new(text.Length);
      int copied = 0;
      for (; at >= 0; at = text.IndexOf(secret, at + 1, StringComparison.Ordinal))
      {
         if (at < copied) { continue; }
         int end = at + secret.Length;
         if ((at > 0 && char.IsLetterOrDigit(text[at - 1]) && char.IsLetterOrDigit(secret[0])) ||
             (end < text.Length && char.IsLetterOrDigit(text[end]) && char.IsLetterOrDigit(secret[^1])))
         {
            continue;
         }
         masked.Append(text, copied, at - copied).Append(Mask);
         copied = end;
      }
      return copied == 0 ? text : masked.Append(text, copied, text.Length - copied).ToString();
   }

   /// <summary>The text with the pattern's matches replaced; the same instance when there are none.</summary>
   private static string Replaced(string text, Regex pattern, MatchEvaluator replace) =>
      pattern.IsMatch(text) ? pattern.Replace(text, replace) : text;
}

/// <summary>Loggers whose entries are masked (<see cref="SecretRedactor"/>) before any provider writes them.</summary>
internal sealed class RedactingLoggerFactory(ILoggerFactory inner, SecretRedactor redactor) : ILoggerFactory
{
   public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName), redactor);

   public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

   public void Dispose() => inner.Dispose();
}

/// <summary>
/// A logger that masks secrets in an entry's message, its values and its exception. An entry without secrets goes
/// on as it was; one with them goes on with its values masked, and its exception as a copy of its text masked.
/// </summary>
internal sealed class RedactingLogger(ILogger inner, SecretRedactor redactor) : ILogger
{
   public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

   public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

   public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
   {
      ArgumentNullException.ThrowIfNull(formatter);
      if (!inner.IsEnabled(logLevel)) { return; }
      string message = formatter(state, exception);
      string masked = redactor.Redact(message);
      Exception? shown = exception == null ? null : RedactedException.Of(exception, redactor);
      if (ReferenceEquals(masked, message) && ReferenceEquals(shown, exception))
      {
         inner.Log(logLevel, eventId, state, exception, formatter);
         return;
      }
      inner.Log(logLevel, eventId, new RedactedState(state as IEnumerable<KeyValuePair<string, object?>>, masked, redactor), shown, static (s, _) => s.Message);
   }
}

/// <summary>An entry's values, masked (its template too, which holds what a message built in place holds), with its message masked.</summary>
internal sealed class RedactedState(IEnumerable<KeyValuePair<string, object?>>? values, string message, SecretRedactor redactor)
   : IReadOnlyList<KeyValuePair<string, object?>>
{
   private readonly List<KeyValuePair<string, object?>> items = [.. (values ?? []).Select(v => Masked(v, redactor))];

   public string Message { get; } = message;

   public int Count => items.Count;

   public KeyValuePair<string, object?> this[int index] => items[index];

   public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => items.GetEnumerator();

   IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

   public override string ToString() => Message;

   private static KeyValuePair<string, object?> Masked(KeyValuePair<string, object?> value, SecretRedactor redactor)
   {
      if (value.Value is null) { return value; }
      string text = value.Value as string ?? value.Value.ToString() ?? string.Empty;
      string masked = redactor.Redact(text);
      return ReferenceEquals(masked, text) ? value : new KeyValuePair<string, object?>(value.Key, masked);
   }
}

/// <summary>An exception's text (its type, message and stack, and those of its inner exceptions) with secrets masked, as it is logged.</summary>
internal sealed class RedactedException : Exception
{
   private readonly string text;

   private RedactedException(string message, string text) : base(message) => this.text = text;

   /// <summary>The exception itself when its text has no secret; a masked copy of its text otherwise.</summary>
   public static Exception Of(Exception exception, SecretRedactor redactor)
   {
      string text = exception.ToString();
      string masked = redactor.Redact(text);
      return ReferenceEquals(masked, text) ? exception : new RedactedException(redactor.Redact(exception.Message), masked);
   }

   public override string ToString() => text;
}
