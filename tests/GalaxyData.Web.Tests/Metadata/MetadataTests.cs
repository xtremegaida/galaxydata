using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Metadata;

/// <summary>The metadata database: migrated at startup, in WAL mode, in UTC, and the first administrator made or reset.</summary>
public sealed class MetadataTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static async Task<T> WithDbAsync<T>(WebAppFactory factory, Func<MetadataDb, Task<T>> use)
   {
      using IServiceScope scope = factory.Services.CreateScope();
      return await use(scope.ServiceProvider.GetRequiredService<MetadataDb>());
   }

   /// <summary>The model and the migrations agree: a change to the model comes with a migration (<c>dotnet ef migrations add</c>).</summary>
   [Fact]
   public async Task TheMigrationsHaveEveryChangeToTheModel()
   {
      await using WebAppFactory factory = new();
      factory.CreateClient();
      (await WithDbAsync(factory, db => Task.FromResult(db.Database.HasPendingModelChanges()))).ShouldBeFalse();
      (await WithDbAsync(factory, async db => (await db.Database.GetPendingMigrationsAsync(Token)).ToList())).ShouldBeEmpty();
   }

   [Fact]
   public async Task TheDatabaseIsInTheDataDirectoryInWalModeAndKeepsUtc()
   {
      await using WebAppFactory factory = new();
      factory.CreateClient();
      File.Exists(Path.Combine(factory.DataDirectory, MetadataDb.FileName)).ShouldBeTrue();
      (await WithDbAsync(factory, db => db.Database.SqlQueryRaw<string>("PRAGMA journal_mode").ToListAsync(Token))).ShouldBe(["wal"]);
      AppUser admin = await WithDbAsync(factory, db => db.Users.SingleAsync(Token));
      admin.CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);
      admin.CreatedAt.ShouldBe(DateTime.UtcNow, TimeSpan.FromMinutes(1));
   }

   [Fact]
   public async Task WithNoUsersAndNoPasswordTheApplicationDoesntStart()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Bootstrap:AdminPassword"] = null } };
      InvalidOperationException e = Should.Throw<InvalidOperationException>(() => factory.CreateClient());
      e.Message.ShouldStartWith("There are no users yet, and GalaxyData:Bootstrap:AdminPassword isn't set");
   }

   [Fact]
   public async Task SettingsThatDontMakeSenseStopTheApplication()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Auth:MinimumPasswordLength"] = "3" } };
      Exception e = Should.Throw<Exception>(() => factory.CreateClient());
      e.ToString().ShouldContain("MinimumPasswordLength");
   }

   /// <summary>
   /// Resetting the administrator's password lets them in with the configured one, to be changed; it is done once for
   /// each password, so a restart with the setting still there doesn't undo the change they made.
   /// </summary>
   [Fact]
   public async Task TheAdministratorsPasswordIsResetOnceForEachPassword()
   {
      await using SharedData data = new();
      Dictionary<string, string?> reset = new() { ["GalaxyData:Bootstrap:ResetAdminPassword"] = "true", ["GalaxyData:Bootstrap:AdminPassword"] = "the-reset-password" };
      await using (WebAppFactory first = new() { DataDirectory = data.Path })
      {
         TestApi admin = await TestApi.SignedInAsync(first);
         await (await admin.PostAsync("/api/auth/change-password", new { currentPassword = TestApi.AdminPassword, newPassword = "forgotten-password" }))
            .JsonAsync(HttpStatusCode.OK);
         // Disabled by another administrator, too.
         await admin.CreateUserAsync("max", "admin", "first-password-of-a-user");
         TestApi max = await TestApi.SignedInAsync(first, "max", "first-password-of-a-user", changeTo: "second-password-of-a-user");
         await (await max.PutAsync("/api/users/1", new { role = "read", isDisabled = true, version = 1 })).JsonAsync(HttpStatusCode.OK);
      }
      await WebAppFactory.ReleasedAsync(data.Path);
      await using (WebAppFactory second = new() { DataDirectory = data.Path, Settings = reset })
      {
         TestApi admin = await TestApi.SignedInAsync(second, TestApi.AdminName, "the-reset-password", changeTo: "remembered-password");
         (await admin.SessionAsync()).GetProperty("user").GetProperty("role").GetString().ShouldBe("admin");
      }
      await WebAppFactory.ReleasedAsync(data.Path);
      await using (WebAppFactory third = new() { DataDirectory = data.Path, Settings = reset })
      {
         TestApi api = new(third);
         await (await api.SignInAsync(TestApi.AdminName, "the-reset-password")).ProblemAsync(401, ProblemCodes.InvalidCredentials);
         (await api.SignInAsync(TestApi.AdminName, "remembered-password")).StatusCode.ShouldBe(HttpStatusCode.OK);
      }
   }

   [Fact]
   public async Task ADatabaseANewerVersionMadeIsRefused()
   {
      await using SharedData data = new();
      await using (WebAppFactory first = new() { DataDirectory = data.Path })
      {
         first.CreateClient();
         await WithDbAsync(first, db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29991231000000_FromTheFuture', '99.0.0')", Token));
      }
      await WebAppFactory.ReleasedAsync(data.Path);
      await using WebAppFactory second = new() { DataDirectory = data.Path };
      InvalidOperationException e = Should.Throw<InvalidOperationException>(() => second.CreateClient());
      e.Message.ShouldContain("was made by a newer version of GalaxyData: it has the migration 29991231000000_FromTheFuture");
   }

   /// <summary>A backup is the database as it was, written-ahead changes too; the newest ten are kept.</summary>
   [Fact]
   public async Task BackupsCopyTheDatabaseAndKeepTheNewest()
   {
      await using SharedData data = new();
      Directory.CreateDirectory(data.Path);
      string database = Path.Combine(data.Path, "galaxydata.db");
      string backups = Path.Combine(data.Path, MetadataBackup.DirectoryName);
      await using (SqliteConnection connection = new(MetadataDb.ConnectionString(database)))
      {
         await connection.OpenAsync(Token);
         await using SqliteCommand command = connection.CreateCommand();
         command.CommandText = "PRAGMA journal_mode = WAL; CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (42);";
         await command.ExecuteNonQueryAsync(Token);

         DateTime at = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
         string first = MetadataBackup.Create(database, backups, "before-test", at);
         Path.GetFileName(first).ShouldBe("galaxydata-20261003-120000-before-test.db");
         await using (SqliteConnection copy = new(MetadataDb.ConnectionString(first)))
         {
            await copy.OpenAsync(Token);
            await using SqliteCommand read = copy.CreateCommand();
            read.CommandText = "SELECT x FROM t";
            (await read.ExecuteScalarAsync(Token)).ShouldBe(42L);
         }
         for (int i = 1; i <= 11; i++) { MetadataBackup.Create(database, backups, "before-test", at.AddMinutes(i)); }
      }
      string[] kept = Directory.GetFiles(backups).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
      kept.Length.ShouldBe(MetadataBackup.Kept);
      kept[0].ShouldBe("galaxydata-20261003-120200-before-test.db");
   }
}
