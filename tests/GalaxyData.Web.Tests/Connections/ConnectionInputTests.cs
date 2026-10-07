using System;
using System.Collections.Generic;
using System.IO;
using GalaxyData.Connectors;
using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Excel;
using GalaxyData.Query.PostgreSql;
using GalaxyData.Query.Sqlite;
using GalaxyData.Query.SqlServer;
using GalaxyData.Web.Connections;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;
using Xunit;
using Options = Microsoft.Extensions.Options.Options;

namespace GalaxyData.Web.Tests.Connections;

/// <summary>
/// Working out a connection's settings from what an administrator gives: secrets kept, set and cleared in the form
/// and in a connection string, keywords the application sets, files outside the allowed folders, options; and
/// converting between the form and a connection string without sending a secret back that wasn't given.
/// </summary>
public sealed class ConnectionInputTests
{
   private static readonly string Root = Path.Combine(Path.GetTempPath(), "gd-roots");

   private static readonly FileRoots Roots = new(
      Options.Create(new GalaxyDataOptions { Connections = { AllowedFileRoots = [Root] } }),
      new DataDirectory(new GalaxyDataOptions { DataDirectory = Path.GetTempPath() }, new HostingEnvironment { ContentRootPath = Path.GetTempPath() }));

   private static readonly Dictionary<string, string> Stored = new(StringComparer.OrdinalIgnoreCase) { ["Password"] = "stored-secret" };

   private static ConnectionResolution Resolve(ConnectionKind kind, ConnectionInput input, IReadOnlyDictionary<string, string>? stored = null) =>
      ConnectionInputs.Resolve(kind, input, stored ?? Stored, Roots);

   private static ConnectionInput Form(Dictionary<string, string> settings, Dictionary<string, SecretInput>? secrets = null) =>
      new(ConnectionMode.Form, settings, secrets);

   private static ConnectionInput Raw(string connectionString, Dictionary<string, SecretInput>? secrets = null) =>
      new(ConnectionMode.Raw, ConnectionString: connectionString, Secrets: secrets);

   private static readonly Dictionary<string, string> Pg = new() { ["host"] = " db ", ["Database"] = "shop", ["Username"] = "reader", ["Port"] = "" };

   [Fact]
   public void TheFormsSettingsAreTheProvidersAndSecretsAreKeptApart()
   {
      ConnectionResolution resolved = Resolve(new PostgreSqlKind(), Form(Pg));
      resolved.IsValid.ShouldBeTrue();
      resolved.Settings.ShouldBe(new Dictionary<string, string> { ["Host"] = "db", ["Database"] = "shop", ["Username"] = "reader" }, ignoreOrder: true);
      resolved.Secrets.ShouldBe(Stored, ignoreOrder: true, "a secret not mentioned is kept");
   }

   [Theory]
   [InlineData(SecretAction.Keep, null, "stored-secret")]
   [InlineData(SecretAction.Set, "new-secret", "new-secret")]
   [InlineData(SecretAction.Clear, null, null)]
   public void TheFormKeepsSetsOrClearsASecret(SecretAction action, string? value, string? expected)
   {
      ConnectionResolution resolved = Resolve(new PostgreSqlKind(), Form(Pg, new() { ["password"] = new SecretInput(action, value) }));
      resolved.IsValid.ShouldBeTrue();
      resolved.Secrets.GetValueOrDefault("Password").ShouldBe(expected);
   }

   [Fact]
   public void TheFormWantsSecretsInTheSecretsAndAValueToSet()
   {
      ConnectionResolution resolved = Resolve(new PostgreSqlKind(), Form(new(Pg) { ["Password"] = "in the open" }, new() { ["SSL Password"] = new SecretInput(SecretAction.Set) }));
      resolved.Errors.Keys.ShouldBe(["settings.Password", "secrets.SSL Password"], ignoreOrder: true);
   }

   [Theory]
   [InlineData("Host=db;Database=shop;Password=********", null, "stored-secret")]
   [InlineData("Host=db;Database=shop;Password=typed-secret", null, "typed-secret")]
   [InlineData("Host=db;Database=shop", null, null)]
   [InlineData("Host=db;Database=shop;Password=********", "set", "form-secret")]
   [InlineData("Host=db;Database=shop;Password=********", "clear", null)]
   public void AConnectionStringKeepsAMaskedSecretSetsAWrittenOneAndClearsAMissingOne(string connectionString, string? action, string? expected)
   {
      Dictionary<string, SecretInput>? secrets = action switch
      {
         "set" => new() { ["Password"] = new SecretInput(SecretAction.Set, "form-secret") },
         "clear" => new() { ["Password"] = new SecretInput(SecretAction.Clear) },
         _ => null,
      };
      ConnectionResolution resolved = Resolve(new PostgreSqlKind(), Raw(connectionString, secrets));
      resolved.IsValid.ShouldBeTrue();
      resolved.Settings.ShouldBe(new Dictionary<string, string> { ["Host"] = "db", ["Database"] = "shop" }, ignoreOrder: true);
      resolved.Secrets.GetValueOrDefault("Password").ShouldBe(expected);
   }

   [Fact]
   public void AMaskedSecretWithNothingStoredIsAProblem()
   {
      ConnectionResolution resolved = Resolve(new PostgreSqlKind(), Raw("Host=db;Database=shop;Password=********"), new Dictionary<string, string>());
      resolved.Errors["connectionString"].ShouldBe(["There is no Password to keep: write it in place of ********"]);
   }

   [Theory]
   [InlineData("sqlite", "Data Source={root}/a.db;Mode=ReadWriteCreate", "Mode is the application's to set (from whether the connection is read-only)")]
   [InlineData("duckdb", "Data Source={root}/a.duckdb;ACCESS_MODE=READ_WRITE", "access_mode is the application's to set (from whether the connection is read-only)")]
   [InlineData("sqlserver", "Data Source=db;AttachDBFilename=C:/x.mdf", "AttachDbFilename is the application's to set (from whether the connection is read-only)")]
   [InlineData("postgres", "Host=db;Bogus=1", null)]
   [InlineData("sqlite", "Data Source=relative.db", "Database file: Give the full path")]
   [InlineData("sqlite", "Data Source=C:/elsewhere/a.db", null)]
   [InlineData("postgres", "Database=shop", "Host is needed")]
   [InlineData("clickhouse", "Host=db;set_join_use_nulls=0", "set_join_use_nulls is the application's to set (from whether the connection is read-only)")]
   [InlineData("clickhouse", "Host=db;UseSession=true", "UseSession is the application's to set (from whether the connection is read-only)")]
   public void WhatAConnectionStringMayNotHave(string kind, string connectionString, string? message)
   {
      ConnectionKind found = new ConnectionKinds([new PostgreSqlKind(), new SqlServerKind(), new SqliteKind(), new DuckDbKind(), new ClickHouseKind()]).Find(kind)!;
      ConnectionResolution resolved = Resolve(found, Raw(connectionString.Replace("{root}", Root, StringComparison.Ordinal)));
      resolved.Errors.Keys.ShouldBe(["connectionString"]);
      if (message != null) { resolved.Errors["connectionString"].ShouldContain(message); }
   }

   [Fact]
   public void FilesMustBeInTheAllowedFolders()
   {
      SqliteKind kind = new();
      Resolve(kind, Form(new() { ["Data Source"] = Path.Combine(Root, "sub", "a.db") })).IsValid.ShouldBeTrue();
      ConnectionResolution outside = Resolve(kind, Form(new() { ["Data Source"] = Path.Combine(Root + "-not", "a.db") }));
      outside.Errors["settings.Data Source"].ShouldBe([$"Database file: It isn't in a folder connections may use: {Root}"]);
      ConnectionResolution up = Resolve(kind, Form(new() { ["Data Source"] = Path.Combine(Root, "..", "a.db") }));
      up.Errors.Keys.ShouldBe(["settings.Data Source"]);
      Resolve(new PostgreSqlKind(), Form(new(Pg) { ["Root Certificate"] = @"C:\Windows\win.ini" })).Errors.Keys.ShouldBe(["settings.Root Certificate"]);
   }

   [Fact]
   public void OptionsAreTheKindsAndTakeTheirValues()
   {
      ConnectionResolution resolved = Resolve(new SqliteKind(), new ConnectionInput(Settings: new() { ["Data Source"] = Path.Combine(Root, "a.db") },
         Options: new() { ["enforceForeignKeys"] = "false", ["trustForeignKeys"] = "True" }, IsReadOnly: false));
      resolved.IsValid.ShouldBeTrue();
      resolved.Options.ShouldBe(new Dictionary<string, string> { ["enforceForeignKeys"] = "false", ["trustForeignKeys"] = "true" }, ignoreOrder: true);
      resolved.IsReadOnly.ShouldBeFalse();

      ConnectionResolution wrong = Resolve(new SqliteKind(), new ConnectionInput(Settings: new() { ["Data Source"] = Path.Combine(Root, "a.db") },
         Options: new() { ["enforceForeignKeys"] = "maybe", ["trustForeignKeys"] = "yes", ["headerRow"] = "true" }));
      wrong.Errors.Keys.ShouldBe(["options.enforceForeignKeys", "options.trustForeignKeys", "options.headerRow"], ignoreOrder: true);
   }

   [Fact]
   public void AFolderOfWorkbooksIsReadOnlyAndHasNoConnectionString()
   {
      ExcelKind kind = new();
      ConnectionResolution resolved = Resolve(kind, new ConnectionInput(Settings: new() { ["Folder"] = Root }, Options: new() { ["headerRow"] = "false" }, IsReadOnly: false));
      resolved.Errors.ShouldBeEmpty();
      resolved.IsReadOnly.ShouldBeTrue();
      Resolve(kind, Raw("Folder=x")).Errors.Keys.ShouldBe(["mode"]);
   }

   [Fact]
   public void TheFormBecomesAConnectionStringWithSecretsMaskedAndBack()
   {
      PostgreSqlKind kind = new();
      Dictionary<string, SecretInput> secrets = new() { ["Password"] = new SecretInput(SecretAction.Set, "typed-in-the-form") };
      ConnectionInput raw = ConnectionInputs.Convert(kind, Form(Pg, secrets), ConnectionMode.Raw);
      raw.Mode.ShouldBe(ConnectionMode.Raw);
      raw.ConnectionString.ShouldBe("Host=db;Database=shop;Username=reader;Password=********");
      raw.Secrets.ShouldBe(secrets, "the form's actions go with the masked secret");
      // Saved as it is, the masked secret is the one typed in the form.
      Resolve(kind, raw).Secrets["Password"].ShouldBe("typed-in-the-form");

      ConnectionInput form = ConnectionInputs.Convert(kind, raw with { ConnectionString = raw.ConnectionString + ";SSL Password=written-out" }, ConnectionMode.Form);
      form.Settings.ShouldBe(new Dictionary<string, string> { ["Host"] = "db", ["Database"] = "shop", ["Username"] = "reader" }, ignoreOrder: true);
      form.Secrets!["Password"].ShouldBe(new SecretInput(SecretAction.Set, "typed-in-the-form"));
      form.Secrets["SSL Password"].ShouldBe(new SecretInput(SecretAction.Set, "written-out"));

      ConnectionInput cleared = ConnectionInputs.Convert(kind, Raw("Host=db", new() { ["Password"] = new SecretInput(SecretAction.Keep) }), ConnectionMode.Form);
      cleared.Secrets!["Password"].Action.ShouldBe(SecretAction.Clear, "a secret left out of the connection string is cleared");
   }
}
