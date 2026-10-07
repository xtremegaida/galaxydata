using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Connectors;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Excel;
using GalaxyData.Query.PostgreSql;
using GalaxyData.Query.Sqlite;
using GalaxyData.Query.SqlServer;
using GalaxyData.Web.Connections;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Connections;

/// <summary>The kinds of connection: their fields name keywords as the providers do, secrets are told apart and masked, and read-only is the application's to set.</summary>
public sealed class ConnectionKindTests
{
   public static TheoryData<string> Kinds => new("postgres", "sqlserver", "sqlite", "duckdb", "clickhouse");

   private static readonly ConnectionKinds All = new([new PostgreSqlKind(), new SqlServerKind(), new SqliteKind(), new DuckDbKind(), new ExcelKind(), new ClickHouseKind()]);

   private static ConnectionKind Kind(string id) => All.Find(id)!;

   private static string Sample(FieldDto field) => field.Type switch
   {
      FieldType.Number => "7",
      FieldType.Bool => "True",
      FieldType.Select => field.Choices![^1].Value,
      FieldType.FilePath => @"C:\data\files\x.db",
      _ => "x",
   };

   /// <summary>Each field's key is the keyword the provider's builder writes, so a form's settings and a connection string's are the same.</summary>
   [Theory]
   [MemberData(nameof(Kinds))]
   public void FieldsAreNamedAsTheProviderNamesThem(string id)
   {
      ConnectionKind kind = Kind(id);
      foreach (FieldDto field in kind.Fields.Where(f => f.Type != FieldType.KeyValues))
      {
         List<KeyValuePair<string, string>> normalized = kind.Normalize([new(field.Key, Sample(field))]);
         normalized.Select(p => p.Key).ShouldBe([field.Key], $"{id}: {field.Key}");
         kind.IsSecret(field.Key).ShouldBe(field.Type == FieldType.Password, $"{id}: {field.Key}");
      }
      foreach (FieldDto field in kind.Fields.Where(f => f.VisibleWhen != null))
      {
         kind.Fields.ShouldContain(f => f.Key == field.VisibleWhen!.Field, $"{id}: {field.Key} depends on a field the form has");
      }
   }

   [Theory]
   [InlineData("postgres", "server=h;user id=u;pwd=p;sslmode=require", "Host=h|Username=u|Password=p|SSL Mode=Require")]
   [InlineData("sqlserver", "server=h;database=d;uid=u;pwd=p;trustservercertificate=yes", "Data Source=h|Initial Catalog=d|User ID=u|Password=p|Trust Server Certificate=True")]
   [InlineData("sqlite", "data source=C:/f/x.db;default timeout=5", "Data Source=C:/f/x.db|Default Timeout=5")]
   [InlineData("duckdb", "DataSource=C:/f/x.duckdb;threads=2", "Data Source=C:/f/x.duckdb|threads=2")]
   [InlineData("clickhouse", "host=h;PORT=8443;protocol=HTTPS;SET_max_threads=4", "Host=h|Port=8443|Protocol=https|set_max_threads=4")]
   public void SynonymsBecomeTheProvidersKeywords(string id, string connectionString, string expected) =>
      string.Join("|", Kind(id).Parse(connectionString).Select(p => $"{p.Key}={p.Value}")).ShouldBe(expected);

   [Theory]
   [InlineData("postgres", "Host=h;Bogus=1")]
   [InlineData("sqlserver", "Data Source=h;Bogus=1")]
   [InlineData("sqlite", "Data Source=x.db;Bogus=1")]
   [InlineData("postgres", "Host=h;Port=many")]
   [InlineData("clickhouse", "Host=h;Bogus=1")]
   [InlineData("clickhouse", "Host=h;Port=many")]
   [InlineData("clickhouse", "Host=h;Protocol=ftp")]
   [InlineData("clickhouse", "Host=h;Compression=maybe")]
   public void KeywordsAndValuesTheProviderDoesntTakeAreRefused(string id, string connectionString) =>
      Should.Throw<Exception>(() => Kind(id).Parse(connectionString)).ShouldBeAssignableTo<ArgumentException>();

   [Theory]
   [InlineData("postgres", "Password")]
   [InlineData("postgres", "SSL Password")]
   [InlineData("sqlserver", "Password")]
   [InlineData("duckdb", "motherduck_token")]
   [InlineData("clickhouse", "Password")]
   public void SecretsAreToldApart(string id, string keyword) => Kind(id).IsSecret(keyword).ShouldBeTrue();

   [Theory]
   [InlineData("postgres", true, "Options=\"-c default_transaction_read_only=on\"")]
   [InlineData("sqlite", true, "Mode=ReadOnly")]
   [InlineData("sqlite", false, "Mode=ReadWrite")]
   [InlineData("duckdb", true, "ACCESS_MODE=READ_ONLY")]
   [InlineData("clickhouse", true, "set_readonly=2")]
   [InlineData("clickhouse", false, "set_readonly=2")]
   public void ReadOnlyIsTheApplicationsToSet(string id, bool readOnly, string expected)
   {
      ConnectionKind kind = Kind(id);
      string path = kind.Fields[0].Type == FieldType.FilePath ? "C:/f/x.db" : "h";
      string connectionString = kind.ConnectionString(new Dictionary<string, string> { [kind.Fields[0].Key] = path }, new Dictionary<string, string>(), readOnly);
      connectionString.ShouldContain(expected, Case.Insensitive);
      if (!readOnly) { connectionString.ShouldNotContain("READ_ONLY", Case.Insensitive); }
   }

   [Fact]
   public void AReadOnlyPostgreSqlConnectionKeepsItsOwnOptions()
   {
      PostgreSqlKind kind = new();
      string connectionString = kind.ConnectionString(new Dictionary<string, string> { ["Host"] = "h", ["Options"] = "-c search_path=sales" },
         new Dictionary<string, string>(), readOnly: true);
      kind.Parse(connectionString).Single(p => p.Key == "Options").Value.ShouldBe("-c search_path=sales -c default_transaction_read_only=on");
   }

   /// <summary>ClickHouse sources are always read-only, whatever the connection says, and have no changes to write.</summary>
   [Fact]
   public void AClickHouseSourceIsReadOnlyWithoutChanges()
   {
      ClickHouseKind kind = new();
      kind.AlwaysReadOnly.ShouldBeTrue();
      SourceInfo source = kind.Configure(new SourceInfo("ch", "clickhouse", "default"), new Dictionary<string, string>());
      (source.IsReadOnly, source.SupportsDml, source.TrustForeignKeys).ShouldBe((true, false, false));
      kind.Options.ShouldBeEmpty();
   }

   [Fact]
   public void ShownConnectionStringsMaskSecrets()
   {
      string shown = new PostgreSqlKind().Display(new Dictionary<string, string> { ["Host"] = "h", ["Username"] = "u" }, ["Password"]);
      shown.ShouldBe("Host=h;Username=u;Password=********");
   }

   [Theory]
   [InlineData("Host=h;Password='a;b'", "Host=h|Password=a;b")]
   [InlineData("Host=h;Password=\"x\"\"y\"", "Host=h|Password=x\"y")]
   [InlineData("Host = h ; Port=1;", "Host=h|Port=1")]
   [InlineData("", "")]
   public void ConnectionStringsSplitAsBuildersWriteThem(string connectionString, string expected) =>
      string.Join("|", ConnectionStrings.Pairs(connectionString).Select(p => $"{p.Key}={p.Value}")).ShouldBe(expected);

   [Fact]
   public void SecretsAreScrubbedFromMessages() =>
      ConnectionStrings.Scrub("login failed for hunter2 (pw hunter2)", ["hunter2", "x"]).ShouldBe("login failed for ******** (pw ********)");

   [Fact]
   public void AFolderOfWorkbooksHasOneSettingAndNoConnectionString()
   {
      ExcelKind kind = new();
      kind.Normalize([new("folder", "C:/f")]).ShouldBe([new KeyValuePair<string, string>("Folder", "C:/f")]);
      Should.Throw<ArgumentException>(() => kind.Normalize([new("Path", "C:/f")]));
      kind.SupportsRaw.ShouldBeFalse();
      kind.AlwaysReadOnly.ShouldBeTrue();
   }
}
