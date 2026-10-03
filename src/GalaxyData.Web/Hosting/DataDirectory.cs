using System;
using System.IO;
using Microsoft.Extensions.Hosting;

namespace GalaxyData.Web.Hosting;

/// <summary>
/// The directory the application keeps its data in (<see cref="GalaxyDataOptions.DataDirectory"/>), as a full path.
/// One instance of the application at a time may use it: what it keeps in memory (the catalog, previewed changes)
/// would differ between two, so <see cref="Open"/> holds a lock file in it until the application stops.
/// </summary>
public sealed class DataDirectory : IDisposable
{
   public const string LockFileName = "galaxydata.lock";

   private FileStream? lockFile;

   public DataDirectory(GalaxyDataOptions options, IHostEnvironment environment)
   {
      ArgumentNullException.ThrowIfNull(options);
      ArgumentNullException.ThrowIfNull(environment);
      Path = System.IO.Path.GetFullPath(options.DataDirectory, environment.ContentRootPath);
   }

   public string Path { get; }

   /// <summary>A path in the directory, or <paramref name="path"/> itself when it is absolute.</summary>
   public string Resolve(string path) => System.IO.Path.GetFullPath(path, Path);

   /// <summary>
   /// Makes the directory when it's missing, and holds it for this application until it stops; at startup, so an
   /// application that can't keep its data, or whose data another is using, doesn't start.
   /// </summary>
   public void Open()
   {
      if (lockFile != null) { return; }
      try
      {
         Directory.CreateDirectory(Path);
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException)
      {
         throw new InvalidOperationException($"The data directory {Path} can't be made: {e.Message}", e);
      }
      try
      {
         lockFile = new FileStream(System.IO.Path.Combine(Path, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException)
      {
         throw new InvalidOperationException(
            $"The data directory {Path} can't be held for this application: {e.Message} One instance of the application at a time may use it.", e);
      }
   }

   public void Dispose()
   {
      lockFile?.Dispose();
      lockFile = null;
   }
}
