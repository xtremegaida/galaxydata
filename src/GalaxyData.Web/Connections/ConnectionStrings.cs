using System;
using System.Collections.Generic;
using System.Text;

namespace GalaxyData.Web.Connections;

/// <summary>Connection strings as ADO.NET's builders write them, and secrets kept out of what is shown.</summary>
public static class ConnectionStrings
{
   /// <summary>What a secret's value is shown as; given back, it keeps the secret as it was.</summary>
   public const string Mask = "********";

   /// <summary>
   /// The keywords and values of a connection string a builder wrote (<c>Host=h;Password='a;b'</c>), in order:
   /// pairs split at semicolons outside quotes, values unquoted (a doubled quote inside is one).
   /// </summary>
   public static List<KeyValuePair<string, string>> Pairs(string connectionString)
   {
      ArgumentNullException.ThrowIfNull(connectionString);
      List<KeyValuePair<string, string>> pairs = [];
      int i = 0;
      while (i < connectionString.Length)
      {
         int equals = connectionString.IndexOf('=', i);
         if (equals < 0) { break; }
         string key = connectionString[i..equals].Trim();
         i = equals + 1;
         while (i < connectionString.Length && connectionString[i] == ' ') { i++; }
         StringBuilder value = new();
         if (i < connectionString.Length && connectionString[i] is '"' or '\'')
         {
            char quote = connectionString[i++];
            while (i < connectionString.Length)
            {
               if (connectionString[i] == quote)
               {
                  if (i + 1 < connectionString.Length && connectionString[i + 1] == quote)
                  {
                     value.Append(quote);
                     i += 2;
                     continue;
                  }
                  i++;
                  break;
               }
               value.Append(connectionString[i++]);
            }
            while (i < connectionString.Length && connectionString[i] != ';') { i++; }
         }
         else
         {
            int end = connectionString.IndexOf(';', i);
            if (end < 0) { end = connectionString.Length; }
            value.Append(connectionString[i..end].Trim());
            i = end;
         }
         i++;
         if (key.Length > 0) { pairs.Add(new KeyValuePair<string, string>(key, value.ToString())); }
      }
      return pairs;
   }

   /// <summary><paramref name="text"/> with each secret's value masked, as a database's message might repeat one.</summary>
   public static string Scrub(string text, IEnumerable<string> secrets)
   {
      ArgumentNullException.ThrowIfNull(text);
      ArgumentNullException.ThrowIfNull(secrets);
      foreach (string secret in secrets)
      {
         if (secret.Length >= 3) { text = text.Replace(secret, Mask, StringComparison.Ordinal); }
      }
      return text;
   }
}
