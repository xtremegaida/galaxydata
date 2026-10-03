using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Providers;
using GalaxyData.Query.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>
/// A source that can't be connected to is <see cref="SourceUnavailableException"/>, naming it, whether a query
/// reads it alone or with others; changes report it as a connection that failed.
/// </summary>
public sealed class SourceUnavailableTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   [Fact]
   public async Task ASourceThatCantBeConnectedToIsUnavailable()
   {
      string memory = $"Data Source=gdq_gone_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
      await using SqliteConnection keeper = new(memory);
      await keeper.OpenAsync(Token);
      await keeper.ExecuteAsync("CREATE TABLE things (id INTEGER PRIMARY KEY, name TEXT NOT NULL); INSERT INTO things VALUES (1, 'one');", Token);
      string missing = Path.Combine(Path.GetTempPath(), $"gdq-missing-{Guid.NewGuid():N}", "gone.db");
      bool down = false;
      await using TestSources sources = await (await TestSources.SqliteShopAsync())
         .AddAsync("gone", () => new SqliteConnection(down ? $"Data Source={missing};Mode=ReadOnly" : memory), SqliteSourceProvider.Instance);
      QueryEngine engine = sources.Engine();
      down = true;

      SourceUnavailableException alone = await Should.ThrowAsync<SourceUnavailableException>(async () =>
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest("gone.things.select(name)"), Token);
      });
      alone.Target.Alias.ShouldBe("gone");
      alone.Reason.ShouldContain("unable to open database file");
      alone.Message.ShouldBe("Couldn't connect to gone: " + alone.Reason);
      alone.InnerException.ShouldBeOfType<SqliteException>();

      SourceUnavailableException across = await Should.ThrowAsync<SourceUnavailableException>(async () =>
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest("shop.customers.select(name, things: gone.things.count())"), Token);
         await result.ToListAsync(Token);
      });
      across.Target.Alias.ShouldBe("gone");
      sources.Merge.ActiveSessions.ShouldBe(0);

      DmlScript script = engine.ParseScript(engine.Catalog.FindSource("gone")!, "UPDATE things SET name = 'two'");
      DmlResult changes = await engine.CommitAsync([script], Token);
      changes.Outcome.ShouldBe(DmlOutcome.RolledBack);
      changes.Failure!.Kind.ShouldBe(DmlFailureKind.Connection);
      changes.Failure.Message.ShouldBe("gone (SQLite) couldn't be opened to write the changes: " + alone.Reason);
   }
}
