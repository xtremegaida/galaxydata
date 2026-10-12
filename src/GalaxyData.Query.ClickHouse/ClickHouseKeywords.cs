using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GalaxyData.Query.ClickHouse;

/// <summary>
/// ClickHouse.Driver's connection-string keywords, as it names them. Its builder keeps keywords as written and takes
/// any, so a kind of connection over it names them itself: <see cref="Canonical"/> writes a keyword as the driver
/// names it, of those the kind takes, and refuses others, and values the driver would ignore.
/// </summary>
public static class ClickHouseKeywords
{
   /// <summary>The prefix of a server setting given as a keyword: <c>set_max_threads=4</c>.</summary>
   public const string SettingPrefix = "set_";

   public const string SkipCertificateValidation = "SkipServerCertificateValidation";

   /// <summary>The keywords ClickHouse.Driver takes, as it names them.</summary>
   public static IReadOnlyList<string> All { get; } =
   [
      "Host", "Port", "Database", "Username", "Password", "Protocol", "Path", "Compression", "Timeout", SkipCertificateValidation, "UseSession",
      "SessionId", "UseCustomDecimals", "UseFormDataParameters", "ReadStringsAsByteArrays", "ReadBufferSize", "Roles", "JsonReadMode",
      "JsonWriteMode", "MapReadMode", "AcceptEncoding", "AllowDuplicateJsonKeys",
   ];

   private static readonly string[] Flags = ["Compression", SkipCertificateValidation, "UseSession", "UseCustomDecimals", "UseFormDataParameters", "ReadStringsAsByteArrays", "AllowDuplicateJsonKeys"];

   /// <summary>
   /// The keyword and value as the driver names them: one of <paramref name="keywords"/>, or a server setting (prefixed
   /// <c>set_</c>) when <paramref name="settings"/> lets the kind take them. Any other keyword is an
   /// <see cref="ArgumentException"/> naming <paramref name="kind"/>, as is a value the driver would ignore.
   /// </summary>
   public static KeyValuePair<string, string> Canonical(KeyValuePair<string, string> pair, IReadOnlyCollection<string> keywords, bool settings, string kind)
   {
      ArgumentNullException.ThrowIfNull(keywords);
      string key = pair.Key.Trim();
      if (settings && key.StartsWith(SettingPrefix, StringComparison.OrdinalIgnoreCase) && key.Length > SettingPrefix.Length)
      {
         return new(SettingPrefix + key[SettingPrefix.Length..], pair.Value);
      }
      string keyword = keywords.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase))
         ?? throw new ArgumentException(settings
            ? $"{kind} connections take no keyword '{key}' (a server setting is written set_{key})"
            : $"{kind} connections take no keyword '{key}'");
      string value = pair.Value.Trim();
      bool valid = keyword switch
      {
         "Port" => ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ushort port) && port > 0,
         "Timeout" or "ReadBufferSize" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0,
         "Protocol" => value.Equals("http", StringComparison.OrdinalIgnoreCase) || value.Equals("https", StringComparison.OrdinalIgnoreCase),
         _ when Flags.Contains(keyword) => bool.TryParse(value, out _),
         _ => true,
      };
      if (!valid) { throw new ArgumentException($"'{pair.Value}' isn't a value {keyword} takes"); }
      return new(keyword, keyword == "Protocol" ? value.ToLowerInvariant() : pair.Value);
   }
}
