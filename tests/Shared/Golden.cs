using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace GalaxyData.Testing;

/// <summary>
/// Golden-file assertions. The expected text lives in a <c>Snapshots</c> folder next to the test source, named
/// <c>{TestClass}.{TestMethod}[.{suffix}].{extension}</c>. On a mismatch the actual text is written alongside as
/// <c>.received.{extension}</c> and the test fails; a missing snapshot is written and fails too, so every new
/// snapshot gets reviewed. Set <c>GDQ_ACCEPT_SNAPSHOTS=1</c> to accept the received text instead.
/// </summary>
internal static class Golden
{
   private static bool Accept => Environment.GetEnvironmentVariable("GDQ_ACCEPT_SNAPSHOTS") == "1";

   public static void Match(string actual, string extension = "txt", string? suffix = null,
                            [CallerFilePath] string sourceFile = "", [CallerMemberName] string testName = "")
   {
      ArgumentNullException.ThrowIfNull(actual);
      string directory = Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots");
      string stem = $"{Path.GetFileNameWithoutExtension(sourceFile)}.{testName}{(suffix == null ? string.Empty : "." + suffix)}";
      string expectedPath = Path.Combine(directory, $"{stem}.{extension}");
      string receivedPath = Path.Combine(directory, $"{stem}.received.{extension}");
      string normalized = Normalize(actual);

      if (File.Exists(expectedPath) && Normalize(File.ReadAllText(expectedPath)) == normalized)
      {
         if (File.Exists(receivedPath)) { File.Delete(receivedPath); }
         return;
      }

      Directory.CreateDirectory(directory);
      if (Accept)
      {
         File.WriteAllText(expectedPath, normalized);
         if (File.Exists(receivedPath)) { File.Delete(receivedPath); }
         return;
      }

      File.WriteAllText(receivedPath, normalized);
      if (!File.Exists(expectedPath))
      {
         Assert.Fail($"No snapshot yet: review {receivedPath} and rename it to {Path.GetFileName(expectedPath)}, or rerun with GDQ_ACCEPT_SNAPSHOTS=1.");
      }
      Assert.Fail($"Snapshot mismatch; see {receivedPath}.{Environment.NewLine}{FirstDifference(Normalize(File.ReadAllText(expectedPath)), normalized)}");
   }

   private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";

   private static string FirstDifference(string expected, string actual)
   {
      string[] a = expected.Split('\n');
      string[] b = actual.Split('\n');
      for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
      {
         string left = i < a.Length ? a[i] : "<end>";
         string right = i < b.Length ? b[i] : "<end>";
         if (left != right) { return $"line {i + 1}:{Environment.NewLine}  expected: {left}{Environment.NewLine}  actual:   {right}"; }
      }
      return "(texts differ only in trailing whitespace)";
   }
}
