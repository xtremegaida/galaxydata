using System;
using GalaxyData.Web.Hosting;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Auth;

/// <summary>What a password must be: long enough (<see cref="AuthSettings.MinimumPasswordLength"/>), not too long, and not hold the user's name.</summary>
public sealed class PasswordPolicy(IOptions<GalaxyDataOptions> options)
{
   public const int MaximumLength = 256;

   /// <summary>What is wrong with <paramref name="password"/> for <paramref name="userName"/>; null when nothing is.</summary>
   public string? Problem(string password, string userName)
   {
      ArgumentNullException.ThrowIfNull(password);
      ArgumentNullException.ThrowIfNull(userName);
      int minimum = options.Value.Auth.MinimumPasswordLength;
      if (password.Length < minimum) { return $"A password needs at least {minimum} characters"; }
      if (password.Length > MaximumLength) { return $"A password may have at most {MaximumLength} characters"; }
      if (password.Contains(userName, StringComparison.OrdinalIgnoreCase)) { return "A password may not hold the user name"; }
      return null;
   }
}
