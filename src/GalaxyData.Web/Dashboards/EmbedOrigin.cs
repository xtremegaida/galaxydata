using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GalaxyData.Web.Dashboards;

/// <summary>
/// Sites that may frame a public dashboard, as a content security policy's <c>frame-ancestors</c> writes them: a
/// scheme (http or https), a host (or <c>*.</c> and a domain, for its subdomains) and a port, if not the scheme's.
/// They are written back as read here, never as given, so nothing else gets into the header.
/// </summary>
public static partial class EmbedOrigin
{
   [GeneratedRegex(@"^(?<scheme>https?)://(?<wild>\*\.)?(?<host>[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*)(:(?<port>[0-9]{1,5}))?$")]
   private static partial Regex Pattern();

   /// <summary>The origin in lower case (international names in their ASCII form), without a trailing slash; null when it isn't one.</summary>
   public static string? Normalize(string? text)
   {
      if (string.IsNullOrWhiteSpace(text) || text.Length > 300) { return null; }
      string trimmed = text.Trim().TrimEnd('/');
      int scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
      if (scheme < 0) { return null; }
      string rest = trimmed[(scheme + 3)..];
      bool wild = rest.StartsWith("*.", StringComparison.Ordinal);
      string host = wild ? rest[2..] : rest;
      string port = string.Empty;
      int colon = host.LastIndexOf(':');
      if (colon >= 0)
      {
         port = host[colon..];
         host = host[..colon];
      }
      try
      {
         host = new IdnMapping().GetAscii(host);
      }
      catch (ArgumentException)
      {
         return null;
      }
      string candidate = (trimmed[..scheme] + "://" + (wild ? "*." : string.Empty) + host + port).ToLowerInvariant();
      Match match = Pattern().Match(candidate);
      if (!match.Success) { return null; }
      if (match.Groups["port"].Success && (!int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number is < 1 or > 65535))
      {
         return null;
      }
      return candidate;
   }
}
