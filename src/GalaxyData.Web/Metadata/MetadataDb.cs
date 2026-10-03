using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GalaxyData.Web.Metadata;

/// <summary>
/// The application's own database (SQLite, in the data directory): users, and what the other features keep. Its
/// schema is EF Core's migrations, applied at startup after a backup. Date-times are UTC; entities people edit are
/// <see cref="IVersioned"/>.
/// </summary>
public sealed class MetadataDb(DbContextOptions<MetadataDb> options) : DbContext(options)
{
   public const string FileName = "galaxydata.db";

   public DbSet<AppUser> Users => Set<AppUser>();

   public DbSet<AdminAuditEvent> AdminAuditEvents => Set<AdminAuditEvent>();

   public DbSet<MetadataSetting> Settings => Set<MetadataSetting>();

   public DbSet<SourceConnection> Connections => Set<SourceConnection>();

   /// <summary>The database's path in a data directory.</summary>
   public static string PathIn(DataDirectory data)
   {
      ArgumentNullException.ThrowIfNull(data);
      return System.IO.Path.Combine(data.Path, FileName);
   }

   /// <summary>
   /// The connection string for the database at <paramref name="path"/>. Connections aren't pooled, so nothing holds
   /// the file once the application has stopped (to back it up, or restore it).
   /// </summary>
   public static string ConnectionString(string path) =>
      new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString();

   protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
   {
      ArgumentNullException.ThrowIfNull(configurationBuilder);
      configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();
      configurationBuilder.Properties<DateTime?>().HaveConversion<UtcConverter>();
      configurationBuilder.Properties<UserRole>().HaveConversion<string>().HaveMaxLength(20);
      configurationBuilder.Properties<ConnectionMode>().HaveConversion<string>().HaveMaxLength(20);
      configurationBuilder.Properties<SchemaStatus>().HaveConversion<string>().HaveMaxLength(20);
   }

   protected override void OnModelCreating(ModelBuilder modelBuilder)
   {
      ArgumentNullException.ThrowIfNull(modelBuilder);
      modelBuilder.Entity<AppUser>(user =>
      {
         user.ToTable("Users");
         user.Property(u => u.UserName).HasMaxLength(64).UseCollation("NOCASE");
         user.HasIndex(u => u.UserName).IsUnique();
         user.Property(u => u.DisplayName).HasMaxLength(200);
         user.Property(u => u.SecurityStamp).HasMaxLength(64);
         user.Property(u => u.Version).IsConcurrencyToken();
      });
      modelBuilder.Entity<AdminAuditEvent>(audit =>
      {
         audit.ToTable("AdminAuditEvents");
         audit.Property(e => e.ActorName).HasMaxLength(64);
         audit.Property(e => e.Action).HasMaxLength(64);
         audit.Property(e => e.Target).HasMaxLength(200);
         audit.HasIndex(e => e.At);
      });
      modelBuilder.Entity<SourceConnection>(connection =>
      {
         connection.ToTable("Connections");
         connection.Property(c => c.Alias).HasMaxLength(64).UseCollation("NOCASE");
         connection.HasIndex(c => c.Alias).IsUnique();
         connection.Property(c => c.Kind).HasMaxLength(20);
         connection.Property(c => c.DisplayName).HasMaxLength(200);
         connection.Property(c => c.Version).IsConcurrencyToken();
      });
      modelBuilder.Entity<MetadataSetting>(setting =>
      {
         setting.ToTable("Settings");
         setting.HasKey(s => s.Key);
         setting.Property(s => s.Key).HasMaxLength(100);
      });
   }

   public override int SaveChanges(bool acceptAllChangesOnSuccess)
   {
      NextVersions();
      return base.SaveChanges(acceptAllChangesOnSuccess);
   }

   public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
   {
      NextVersions();
      return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
   }

   private void NextVersions()
   {
      foreach (var entry in ChangeTracker.Entries<IVersioned>())
      {
         if (entry.State == EntityState.Modified) { entry.Entity.Version++; }
      }
   }

   /// <summary>Date-times are written as UTC, and read back as UTC.</summary>
   private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
      v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
      v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}

/// <summary>For <c>dotnet ef</c>: the database, without the application around it.</summary>
internal sealed class DesignTimeMetadataDb : IDesignTimeDbContextFactory<MetadataDb>
{
   public MetadataDb CreateDbContext(string[] args) =>
      new(new DbContextOptionsBuilder<MetadataDb>().UseSqlite(MetadataDb.ConnectionString(Path.Combine(Path.GetTempPath(), "galaxydata-design.db"))).Options);
}
