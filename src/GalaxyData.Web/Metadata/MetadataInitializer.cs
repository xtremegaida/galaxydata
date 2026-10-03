using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Features.Audit;
using GalaxyData.Web.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Metadata;

/// <summary>
/// Readies the metadata database before the application serves requests: refuses one a newer version made, backs
/// it up and migrates it when this version has migrations it hasn't, puts it in WAL mode, and makes the first
/// administrator (or resets one, see <see cref="BootstrapSettings"/>). The application doesn't start when it can't.
/// </summary>
internal sealed partial class MetadataInitializer(IServiceScopeFactory scopes, DataDirectory data, IOptions<GalaxyDataOptions> options,
                                                  IPasswordHasher<AppUser> hasher, TimeProvider clock, ILogger<MetadataInitializer> logger)
   : IHostedService
{
   /// <summary>The setting that remembers the password a reset last set, so a reset is done once for each.</summary>
   internal const string ResetSetting = "bootstrap.reset";

   public async Task StartAsync(CancellationToken cancellationToken)
   {
      await using AsyncServiceScope scope = scopes.CreateAsyncScope();
      MetadataDb db = scope.ServiceProvider.GetRequiredService<MetadataDb>();
      await MigrateAsync(db, cancellationToken);
      await BootstrapAsync(db, cancellationToken);
   }

   public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

   private async Task MigrateAsync(MetadataDb db, CancellationToken cancellationToken)
   {
      string path = MetadataDb.PathIn(data);
      List<string> known = db.Database.GetMigrations().ToList();
      List<string> applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
      if (applied.Except(known, StringComparer.Ordinal).FirstOrDefault() is { } newer)
      {
         throw new InvalidOperationException(
            $"The metadata database {path} was made by a newer version of GalaxyData: it has the migration {newer}, which this version doesn't know. Run the newer version, or restore a backup from {Path.Combine(data.Path, MetadataBackup.DirectoryName)}.");
      }
      List<string> pending = known.Except(applied, StringComparer.Ordinal).ToList();
      if (pending.Count > 0)
      {
         if (applied.Count > 0)
         {
            string backup = MetadataBackup.Create(path, Path.Combine(data.Path, MetadataBackup.DirectoryName), "before-" + pending[0], clock.GetUtcNow().UtcDateTime);
            LogBackedUp(logger, backup);
         }
         await db.Database.MigrateAsync(cancellationToken);
         LogMigrated(logger, path, string.Join(", ", pending));
      }
      // Readers don't wait for a writer, nor a writer for readers.
      await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL", cancellationToken);
   }

   private async Task BootstrapAsync(MetadataDb db, CancellationToken cancellationToken)
   {
      BootstrapSettings bootstrap = options.Value.Bootstrap;
      DateTime now = clock.GetUtcNow().UtcDateTime;
      if (!await db.Users.AnyAsync(cancellationToken))
      {
         if (string.IsNullOrEmpty(bootstrap.AdminPassword))
         {
            throw new InvalidOperationException(
               "There are no users yet, and GalaxyData:Bootstrap:AdminPassword isn't set: set it (and GalaxyData:Bootstrap:AdminUserName, if not 'admin') to make the first administrator.");
         }
         AppUser admin = new()
         {
            UserName = bootstrap.AdminUserName,
            Role = UserRole.Admin,
            MustChangePassword = bootstrap.RequirePasswordChange,
            CreatedAt = now,
            PasswordChangedAt = now,
         };
         admin.PasswordHash = hasher.HashPassword(admin, bootstrap.AdminPassword);
         db.Users.Add(admin);
         // Made with the password a reset would set, the administrator isn't reset to it at the next start.
         if (bootstrap.ResetAdminPassword) { db.Settings.Add(new MetadataSetting { Key = ResetSetting, Value = admin.UserName + "\n" + admin.PasswordHash }); }
         AdminAudit.Add(db, null, "user.created", AdminAudit.User(admin.UserName), new { role = admin.Role, bootstrap = true }, now);
         await db.SaveChangesAsync(cancellationToken);
         LogBootstrapped(logger, admin.UserName);
         return;
      }
      if (!bootstrap.ResetAdminPassword) { return; }
      if (string.IsNullOrEmpty(bootstrap.AdminPassword))
      {
         throw new InvalidOperationException("GalaxyData:Bootstrap:ResetAdminPassword is set, but GalaxyData:Bootstrap:AdminPassword isn't: set the password to reset to.");
      }
      MetadataSetting? done = await db.Settings.FindAsync([ResetSetting], cancellationToken);
      if (done != null && ResetWith(done.Value, bootstrap))
      {
         LogResetAlready(logger, bootstrap.AdminUserName);
         return;
      }
      AppUser? user = await db.Users.SingleOrDefaultAsync(u => u.UserName == bootstrap.AdminUserName, cancellationToken);
      if (user == null)
      {
         user = new AppUser { UserName = bootstrap.AdminUserName, CreatedAt = now };
         db.Users.Add(user);
      }
      user.Role = UserRole.Admin;
      user.IsDisabled = false;
      user.PasswordHash = hasher.HashPassword(user, bootstrap.AdminPassword);
      user.MustChangePassword = true;
      user.PasswordChangedAt = now;
      user.FailedSignIns = 0;
      user.LockedOutUntil = null;
      user.SecurityStamp = AppUser.NewStamp();
      string record = bootstrap.AdminUserName + "\n" + hasher.HashPassword(user, bootstrap.AdminPassword);
      if (done == null) { db.Settings.Add(new MetadataSetting { Key = ResetSetting, Value = record }); }
      else { done.Value = record; }
      AdminAudit.Add(db, null, "user.password-reset", AdminAudit.User(user.UserName), new { bootstrap = true }, now);
      await db.SaveChangesAsync(cancellationToken);
      LogReset(logger, user.UserName);
   }

   /// <summary>Whether the last reset was of the same user to the same password.</summary>
   private bool ResetWith(string record, BootstrapSettings bootstrap)
   {
      int split = record.IndexOf('\n', StringComparison.Ordinal);
      if (split < 0 || !string.Equals(record[..split], bootstrap.AdminUserName, StringComparison.OrdinalIgnoreCase)) { return false; }
      return hasher.VerifyHashedPassword(new AppUser(), record[(split + 1)..], bootstrap.AdminPassword!) != PasswordVerificationResult.Failed;
   }

   [LoggerMessage(Level = LogLevel.Information, Message = "Backed up the metadata database to {Path} before migrating it")]
   private static partial void LogBackedUp(ILogger logger, string path);

   [LoggerMessage(Level = LogLevel.Information, Message = "Migrated the metadata database {Path}: {Migrations}")]
   private static partial void LogMigrated(ILogger logger, string path, string migrations);

   [LoggerMessage(Level = LogLevel.Warning, Message = "Made the first administrator, {UserName}, with the password GalaxyData:Bootstrap:AdminPassword gives; remove it from the configuration")]
   private static partial void LogBootstrapped(ILogger logger, string userName);

   [LoggerMessage(Level = LogLevel.Warning, Message = "Reset the password of {UserName}, who must change it at the next sign-in; remove GalaxyData:Bootstrap:ResetAdminPassword and the password from the configuration")]
   private static partial void LogReset(ILogger logger, string userName);

   [LoggerMessage(Level = LogLevel.Warning, Message = "{UserName}'s password was reset with this password before, so it isn't again; remove GalaxyData:Bootstrap:ResetAdminPassword from the configuration")]
   private static partial void LogResetAlready(ILogger logger, string userName);
}
