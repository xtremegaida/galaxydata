using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GalaxyData.Query.Execution;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests;

/// <summary>
/// The engine names no database: each one's dialect, provider and connection live in its connector's project, which
/// the engine never references, and the engine's code (comments aside) has no database's name in it.
/// </summary>
public sealed partial class ArchitectureTests
{
   [Fact]
   public void TheEngineReferencesNoConnector() =>
      typeof(QueryEngine).Assembly.GetReferencedAssemblies().Select(a => a.Name!)
         .Where(name => name.StartsWith("GalaxyData.", StringComparison.Ordinal) && name != "GalaxyData.Common")
         .ShouldBeEmpty();

   [GeneratedRegex(@"Sqlite|DuckDb|Postgre|SqlServer|Excel|ClickHouse|Npgsql", RegexOptions.IgnoreCase)]
   private static partial Regex DatabaseName();

   [Fact]
   public void TheEnginesCodeNamesNoDatabase()
   {
      string source = Path.Combine(Root(), "src", "GalaxyData.Query");
      Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
         .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
         .SelectMany(path => File.ReadLines(path).Select((line, i) => (Path: Path.GetRelativePath(source, path), Line: i + 1, Code: Code(line))))
         .Where(line => DatabaseName().IsMatch(line.Code))
         .Select(line => $"{line.Path}:{line.Line}: {line.Code.Trim()}")
         .ShouldBeEmpty();
   }

   /// <summary>A line without its comment: what follows <c>//</c> outside a string.</summary>
   private static string Code(string line)
   {
      bool inString = false;
      for (int i = 0; i + 1 < line.Length; i++)
      {
         if (line[i] == '"') { inString = !inString; }
         else if (!inString && line[i] == '/' && line[i + 1] == '/') { return line[..i]; }
      }
      return line;
   }

   private static string Root()
   {
      DirectoryInfo? directory = new(AppContext.BaseDirectory);
      while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GalaxyData.slnx"))) { directory = directory.Parent; }
      return directory?.FullName ?? throw new DirectoryNotFoundException("The repository's root isn't above the tests");
   }
}
