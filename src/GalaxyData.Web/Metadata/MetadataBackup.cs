using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace GalaxyData.Web.Metadata;

/// <summary>
/// Copies of the metadata database, made before migrations change it, in the data directory's <c>backups</c>; the
/// newest <see cref="Kept"/> are kept. SQLite's online backup copies the database as it is, its write-ahead log too.
/// </summary>
public static class MetadataBackup
{
   public const string DirectoryName = "backups";

   public const int Kept = 10;

   /// <summary>Copies the database to <c>galaxydata-{when}-{label}.db</c> in <paramref name="directory"/>; the copy's path.</summary>
   public static string Create(string databasePath, string directory, string label, DateTime now)
   {
      Directory.CreateDirectory(directory);
      string path = Path.Combine(directory, $"galaxydata-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{label}.db");
      using (SqliteConnection source = new(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
      using (SqliteConnection target = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
      {
         source.Open();
         source.BackupDatabase(target);
      }
      foreach (FileInfo old in new DirectoryInfo(directory).GetFiles("galaxydata-*.db").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Kept))
      {
         old.Delete();
      }
      return path;
   }
}
