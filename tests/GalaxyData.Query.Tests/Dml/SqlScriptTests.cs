using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Tests.Sql;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Dml;

/// <summary>Splitting scripts into statements, as each dialect quotes and comments, and the guard that keeps edited scripts to data changes.</summary>
public sealed class SqlScriptTests
{
   private static SqlDialect Dialect(string providerKind) => TestDialects.All.Single(d => d.ProviderKind == providerKind);

   private static string[] Statements(string providerKind, string script)
   {
      SplitScript split = SqlScriptSplitter.Split(script, Dialect(providerKind));
      split.Problems.ShouldBeEmpty();
      return split.Statements.Select(s => s.Text).ToArray();
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   [InlineData("postgres")]
   [InlineData("sqlserver")]
   public void SplitsAtSemicolonsOutsideStringsQuotedNamesAndComments(string providerKind) =>
      Statements(providerKind, """
         -- a comment; not a statement
         UPDATE "a;b" SET x = 'it''s; fine' /* ; */ WHERE y = 1;
         ;
         DELETE FROM c   -- the end;
         """).ShouldBe(["UPDATE \"a;b\" SET x = 'it''s; fine' /* ; */ WHERE y = 1", "DELETE FROM c"]);

   [Fact]
   public void KnowsEachDialectsQuotes()
   {
      Statements("sqlserver", "UPDATE [a;]]b] SET x = N'y;z'; DELETE FROM t").ShouldBe(["UPDATE [a;]]b] SET x = N'y;z'", "DELETE FROM t"]);
      Statements("sqlite", "UPDATE [a;b] SET `c;d` = 1; DELETE FROM t").ShouldBe(["UPDATE [a;b] SET `c;d` = 1", "DELETE FROM t"]);
      Statements("postgres", @"UPDATE t SET a = E'it\'s; x'; DELETE FROM t").ShouldBe([@"UPDATE t SET a = E'it\'s; x'", "DELETE FROM t"]);
      Statements("duckdb", "UPDATE t SET a = E'\\\\'; DELETE FROM t").ShouldBe(["UPDATE t SET a = E'\\\\'", "DELETE FROM t"]);
      // PostgreSQL's arrays aren't quoted names.
      Statements("postgres", "UPDATE t SET a[1] = 2; DELETE FROM t").ShouldBe(["UPDATE t SET a[1] = 2", "DELETE FROM t"]);
   }

   [Fact]
   public void KeepsDollarQuotedTextWhole()
   {
      Statements("postgres", "DO $$ BEGIN UPDATE a SET x = 1; END $$; UPDATE b SET y = $1;").ShouldBe(["DO $$ BEGIN UPDATE a SET x = 1; END $$", "UPDATE b SET y = $1"]);
      Statements("duckdb", "UPDATE t SET a = $body$ x; $$ y $body$; DELETE FROM t").ShouldBe(["UPDATE t SET a = $body$ x; $$ y $body$", "DELETE FROM t"]);
      Statements("sqlserver", "UPDATE t SET a = $x; UPDATE t SET b = 2$").ShouldBe(["UPDATE t SET a = $x", "UPDATE t SET b = 2$"]);
   }

   [Fact]
   public void NestsBlockCommentsButInSqlite()
   {
      Statements("postgres", "/* a /* b; */ c; */ UPDATE t SET x = 1").ShouldBe(["UPDATE t SET x = 1"]);
      Statements("sqlserver", "/* a /* b; */ c; */ UPDATE t SET x = 1").ShouldBe(["UPDATE t SET x = 1"]);
      SplitScript sqlite = SqlScriptSplitter.Split("/* a /* b; */ c; */ UPDATE t SET x = 1", TestDialects.Sqlite);
      sqlite.Statements.Select(s => s.Text).ShouldBe(["c", "*/ UPDATE t SET x = 1"]);
   }

   [Fact]
   public void SqlServerBatchesEndAtGoLines()
   {
      Statements("sqlserver", "UPDATE a SET x = 'GO'\nGO\nUPDATE b SET go = 2\n  go   -- next\nDELETE FROM c\nGO 1")
         .ShouldBe(["UPDATE a SET x = 'GO'", "UPDATE b SET go = 2", "DELETE FROM c"]);
      Statements("postgres", "UPDATE a SET x = 1\nGO\n").ShouldBe(["UPDATE a SET x = 1\nGO"]);
      SplitScript twice = SqlScriptSplitter.Split("UPDATE a SET x = x + 1\nGO 2\n", TestDialects.SqlServer);
      twice.Problems.Single().ShouldBe(new ScriptProblem("GO with a count would run its batch more than once", 23, 4, 2));
   }

   [Fact]
   public void SqliteTriggersKeepTheirBody() =>
      Statements("sqlite", """
         CREATE TRIGGER t AFTER UPDATE ON a BEGIN
            UPDATE b SET y = CASE WHEN NEW.x > 1 THEN 2 END;
            DELETE FROM c;
         END;
         UPDATE d SET w = 1
         """).Length.ShouldBe(2);

   [Fact]
   public void ReportsWhatIsntClosed()
   {
      SplitScript script = SqlScriptSplitter.Split("UPDATE t SET a = 1;\nUPDATE t SET b = 'x;\n", TestDialects.Sqlite);
      script.Problems.Single().ShouldBe(new ScriptProblem("This string isn't closed", 37, 4, 2));
      SqlScriptSplitter.Split("UPDATE t SET a = 1 /* x", TestDialects.SqlServer).Problems.Single().Message.ShouldBe("This comment isn't closed");
      SqlScriptSplitter.Split("UPDATE t SET a = $q$ x", TestDialects.PostgreSql).Problems.Single().Message.ShouldBe("This $q$ quote isn't closed");
      SqlScriptSplitter.Split("UPDATE [t SET a = 1", TestDialects.SqlServer).Problems.Single().Message.ShouldBe("This quoted name isn't closed");
   }

   [Fact]
   public void StatementsKnowTheirLineAndKeyword()
   {
      SplitScript script = SqlScriptSplitter.Split("""
         update a set x = 1;

         WITH x AS (SELECT 1), y (a, b) AS MATERIALIZED (SELECT 1, 2)
         DELETE FROM t WHERE id IN (SELECT a FROM y);
         with recursive r as (select 1) select * from r;
         (SELECT 1)
         """, TestDialects.PostgreSql);
      script.Statements.Select(s => (s.Line, s.Keyword)).ShouldBe([(1, "UPDATE"), (3, "DELETE"), (5, "SELECT"), (6, null)]);
   }

   public static TheoryData<string, string> Allowed => new()
   {
      { "sqlite", "INSERT INTO t (a) VALUES (1)" },
      { "sqlite", "REPLACE INTO t (a) VALUES (1)" },
      { "sqlite", "WITH x AS (SELECT 1 AS a) UPDATE t SET a = (SELECT a FROM x)" },
      { "sqlite", "UPDATE t SET open = 1, load = 2" },
      { "postgres", "INSERT INTO t (a) VALUES (1) ON CONFLICT (a) DO UPDATE SET b = 2 RETURNING *" },
      { "postgres", "WITH gone AS (DELETE FROM t RETURNING *) INSERT INTO log SELECT * FROM gone" },
      { "duckdb", "INSERT OR REPLACE INTO t SELECT * FROM u" },
      { "sqlserver", "MERGE INTO t USING (SELECT 1 AS a) AS s ON t.a = s.a WHEN NOT MATCHED THEN INSERT (a) VALUES (s.a);" },
      { "sqlserver", "INSERT INTO t (a) SELECT a FROM u WHERE b = 'DROP TABLE x'" },
      { "sqlserver", "UPDATE t SET [drop] = 1 OUTPUT inserted.a INTO log (a) WHERE b = 2" },
      { "postgres", "UPDATE t SET \"pg_read_file\" = 1" },
      { "sqlserver", "UPDATE t SET a = 1\nGO\nDELETE FROM t WHERE a = 2" },
   };

   [Theory]
   [MemberData(nameof(Allowed))]
   public void TheGuardAllowsDataChanges(string providerKind, string script)
   {
      SqlDialect dialect = Dialect(providerKind);
      DmlGuard.Check(SqlScriptSplitter.Split(script, dialect), dialect).ShouldBeEmpty();
   }

   public static TheoryData<string, string, string> Rejected => new()
   {
      { "sqlite", "UPDATE t SET a = 1; DROP TABLE t", "DROP statements can't run here: only those that change data (INSERT, UPDATE, DELETE and MERGE)" },
      { "sqlite", "PRAGMA foreign_keys = OFF", "PRAGMA statements can't run here: only those that change data (INSERT, UPDATE, DELETE and MERGE)" },
      { "sqlite", "ATTACH 'other.db' AS o", "ATTACH statements can't run here: only those that change data (INSERT, UPDATE, DELETE and MERGE)" },
      { "sqlite", "SELECT * FROM t", "SELECT statements can't run here: only those that change data (INSERT, UPDATE, DELETE and MERGE)" },
      { "sqlite", "UPDATE t SET a = load_extension('x')", "load_extension(...) reaches outside the database: it can't be part of a statement that changes data" },
      { "sqlite", "END", "END can't be run here: the changes of each connection run in a transaction of the engine's, committed when every statement has succeeded" },
      { "postgres", "WITH x AS (SELECT 1) SELECT * INTO u FROM x", "SELECT statements can't run here: only those that change data (INSERT, UPDATE, DELETE and MERGE)" },
      { "postgres", "TRUNCATE t", "TRUNCATE statements can't run here: only those that change data (INSERT, UPDATE, DELETE and MERGE)" },
      { "postgres", "UPDATE t SET a = pg_read_file('/etc/passwd')", "pg_read_file(...) reaches outside the database: it can't be part of a statement that changes data" },
      { "postgres", "COMMIT", "COMMIT can't be run here: the changes of each connection run in a transaction of the engine's, committed when every statement has succeeded" },
      { "duckdb", "INSERT INTO t SELECT * FROM read_csv('secret.csv')", "read_csv(...) reaches outside the database: it can't be part of a statement that changes data" },
      { "duckdb", "INSERT INTO t SELECT * FROM \"read_csv\"('secret.csv')", "read_csv(...) reaches outside the database: it can't be part of a statement that changes data" },
      { "postgres", "UPDATE t SET a = \"pg_read_file\"('/etc/passwd')", "pg_read_file(...) reaches outside the database: it can't be part of a statement that changes data" },
      { "postgres", "UPDATE t SET a = pg_catalog.\"PG_READ_FILE\"('/etc/passwd')", "PG_READ_FILE(...) reaches outside the database: it can't be part of a statement that changes data" },
      { "sqlite", "UPDATE t SET a = [load_extension]('x')", "load_extension(...) reaches outside the database: it can't be part of a statement that changes data" },
      { "duckdb", "COPY t TO 'out.csv'", "COPY statements can't run here: only those that change data (INSERT, UPDATE, DELETE and MERGE)" },
      { "sqlserver", "UPDATE t SET a = 1 DROP TABLE t", "DROP can't be part of a statement that changes data" },
      { "sqlserver", "UPDATE t SET a = 1 SELECT * INTO u FROM t", "SELECT ... INTO makes a table: it can't be part of a statement that changes data" },
      { "sqlserver", "INSERT INTO t EXEC ('DROP TABLE t')", "EXEC can't be part of a statement that changes data" },
      { "sqlserver", "DELETE FROM t WAITFOR DELAY '00:10'", "WAITFOR can't be part of a statement that changes data" },
      { "sqlserver", "sp_rename 't', 'u'", "SP_RENAME statements can't run here: only those that change data (INSERT, UPDATE, DELETE and MERGE)" },
      { "sqlserver", "BEGIN TRANSACTION", "BEGIN can't be run here: the changes of each connection run in a transaction of the engine's, committed when every statement has succeeded" },
   };

   [Theory]
   [MemberData(nameof(Rejected))]
   public void TheGuardRejectsAnythingElse(string providerKind, string script, string problem)
   {
      SqlDialect dialect = Dialect(providerKind);
      DmlGuard.Check(SqlScriptSplitter.Split(script, dialect), dialect).Select(p => p.Message).ShouldBe([problem]);
   }

   [Fact]
   public void AnAdministratorMayRunAnyStatementButTransactions()
   {
      SplitScript script = SqlScriptSplitter.Split("CREATE TABLE x (a int); INSERT INTO x VALUES (1); ROLLBACK", TestDialects.Sqlite);
      IReadOnlyList<ScriptProblem> problems = DmlGuard.Check(script, TestDialects.Sqlite, allowAnyStatement: true);
      problems.Select(p => p.Message).ShouldBe(["ROLLBACK can't be run here: the changes of each connection run in a transaction of the engine's, committed when every statement has succeeded"]);
      problems[0].Line.ShouldBe(1);
      problems[0].Start.ShouldBe(50);
   }
}
