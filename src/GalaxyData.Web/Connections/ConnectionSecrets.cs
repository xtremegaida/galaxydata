using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using GalaxyData.Web.Hosting;
using Microsoft.AspNetCore.DataProtection;

namespace GalaxyData.Web.Connections;

/// <summary>
/// Connections' secrets, protected with the application's data protection keys for the connection's alias alone
/// (a copy put in another connection's row doesn't read). Without the keys that protected them they can't be read
/// again, and must be entered again. Each secret protected or read is masked in the log (<see cref="SecretRedactor"/>).
/// </summary>
public sealed class ConnectionSecrets(IDataProtectionProvider protection, SecretRedactor redactor)
{
   private const string Purpose = "GalaxyData.Connections.Secrets.v1";

   private IDataProtector For(string alias) => protection.CreateProtector(Purpose, alias.ToUpperInvariant());

   /// <summary>The secrets, protected; null when there are none.</summary>
   public string? Protect(string alias, IReadOnlyDictionary<string, string> secrets)
   {
      ArgumentNullException.ThrowIfNull(alias);
      ArgumentNullException.ThrowIfNull(secrets);
      foreach (string secret in secrets.Values) { redactor.Add(secret); }
      return secrets.Count == 0 ? null : For(alias).Protect(JsonSerializer.Serialize(secrets));
   }

   /// <summary>The secrets protected; null when they can't be read (the keys are gone, or it isn't this alias's).</summary>
   public Dictionary<string, string>? Unprotect(string alias, string? protectedSecrets)
   {
      ArgumentNullException.ThrowIfNull(alias);
      if (protectedSecrets == null) { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
      try
      {
         Dictionary<string, string> secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(For(alias).Unprotect(protectedSecrets)) ?? [];
         foreach (string secret in secrets.Values) { redactor.Add(secret); }
         return new Dictionary<string, string>(secrets, StringComparer.OrdinalIgnoreCase);
      }
      catch (CryptographicException)
      {
         return null;
      }
   }
}
