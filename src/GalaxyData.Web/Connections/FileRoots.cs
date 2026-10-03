using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GalaxyData.Web.Hosting;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Connections;

/// <summary>
/// The folders a connection's files and folders must be in (<see cref="ConnectionSettings.AllowedFileRoots"/>), so
/// an administrator's account can't read whatever else the application's machine has. A link in an allowed folder
/// that leads outside them is refused too; the folders' own links are trusted.
/// </summary>
public sealed class FileRoots
{
   private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
      ? StringComparison.OrdinalIgnoreCase
      : StringComparison.Ordinal;

   private readonly bool isDefault;

   public FileRoots(IOptions<GalaxyDataOptions> options, DataDirectory data)
   {
      ArgumentNullException.ThrowIfNull(options);
      ArgumentNullException.ThrowIfNull(data);
      List<string>? configured = options.Value.Connections.AllowedFileRoots;
      isDefault = configured == null;
      Roots = (configured ?? [ConnectionSettings.DefaultFileRoot]).Select(r => Path.TrimEndingDirectorySeparator(data.Resolve(r))).ToList();
   }

   public IReadOnlyList<string> Roots { get; }

   /// <summary>Makes the default folder when it is the only one, so there is somewhere to put files.</summary>
   public void Prepare()
   {
      if (isDefault) { Directory.CreateDirectory(Roots[0]); }
   }

   /// <summary>Why <paramref name="path"/> can't be a connection's; null when it can.</summary>
   public string? Problem(string path)
   {
      ArgumentNullException.ThrowIfNull(path);
      if (!Path.IsPathFullyQualified(path)) { return "Give the full path"; }
      string full = Path.GetFullPath(path);
      if (!Inside(full)) { return $"It isn't in a folder connections may use: {string.Join(", ", Roots)}"; }
      FileSystemInfo? found = File.Exists(full) ? new FileInfo(full) : Directory.Exists(full) ? new DirectoryInfo(full) : null;
      if (found?.LinkTarget != null && found.ResolveLinkTarget(returnFinalTarget: true) is { } target && !Inside(target.FullName))
      {
         return "It is a link to a place outside the folders connections may use";
      }
      return null;
   }

   private bool Inside(string full) =>
      Roots.Any(root => full.Equals(root, PathComparison) || full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison));
}
