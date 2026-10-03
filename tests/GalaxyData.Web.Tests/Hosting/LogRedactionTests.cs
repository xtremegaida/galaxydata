using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Tests.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Hosting;

/// <summary>Every entry a provider is given: its message, its values and its exception, as text.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
   public ConcurrentQueue<(string Category, string Message, string Values, string? Exception)> Entries { get; } = new();

   /// <summary>All the text logged.</summary>
   public IEnumerable<string> Texts => Entries.SelectMany(e => new[] { e.Message, e.Values, e.Exception ?? string.Empty });

   public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

   public void Dispose()
   {
   }

   private sealed class Logger(CapturedLogs logs, string category) : ILogger
   {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

      public bool IsEnabled(LogLevel logLevel) => true;

      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      {
         string values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}")) : string.Empty;
         logs.Entries.Enqueue((category, formatter(state, exception), values, exception?.ToString()));
      }
   }
}

public sealed class LogRedactionTests
{
   [Theory]
   [InlineData("Host=db;Username=gd;Password=hunter2;Database=x", "Host=db;Username=gd;Password=********;Database=x")]
   [InlineData("Server=db; User ID=sa; PWD = 'a;b''c'; Encrypt=true", "Server=db; User ID=sa; PWD = ********; Encrypt=true")]
   [InlineData("Data Source=x.duckdb;motherduck_token=abc.def\nnext line", "Data Source=x.duckdb;motherduck_token=********\nnext line")]
   [InlineData("AccountKey=\"k=v;w\";BlobEndpoint=b", "AccountKey=********;BlobEndpoint=b")]
   [InlineData("total = 5 and key = 7", "total = 5 and key = 7")]
   [InlineData("Password : x and the rest", "Password : ********")]
   [InlineData("{\"user\":\"gd\",\"password\":\"x;y\",\"port\":5432}", "{\"user\":\"gd\",\"password\":********,\"port\":5432}")]
   [InlineData("connecting to postgres://gd:hunter2@db.example:5432/shop", "connecting to postgres://gd:********@db.example:5432/shop")]
   [InlineData("Password=\"unterminated;more", "Password=********;more")]
   [InlineData("Password=\r\nnext line", "Password=\r\nnext line")]
   [InlineData("PasswordHash=abc; secrets: set", "PasswordHash=abc; secrets: set")]
   public void SecretSettingsAreMasked(string text, string masked) => new SecretRedactor().Redact(text).ShouldBe(masked);

   /// <summary>A long entry is masked in time linear in its length (a pattern that backtracks would take minutes).</summary>
   [Fact]
   public void LongEntriesAreMaskedQuickly()
   {
      string text = string.Concat(Enumerable.Repeat("word ", 40_000)) + "= x; password=hunter2";
      System.Diagnostics.Stopwatch taken = System.Diagnostics.Stopwatch.StartNew();
      new SecretRedactor().Redact(text).ShouldEndWith("password=********");
      taken.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
   }

   /// <summary>Secrets added at once, as connections are read in parallel, are all kept.</summary>
   [Fact]
   public void SecretsAddedAtOnceAreAllKept()
   {
      SecretRedactor redactor = new();
      string[] secrets = [.. Enumerable.Range(0, 400).Select(i => $"secret-number-{i}")];
      Parallel.ForEach(secrets, new ParallelOptions { MaxDegreeOfParallelism = 8 }, redactor.Add);
      secrets.Where(s => redactor.Redact($"[{s}]") != "[********]").ShouldBeEmpty();
   }

   [Fact]
   public void KnownSecretsAreMaskedAnywhere()
   {
      SecretRedactor redactor = new();
      redactor.Add("s3cr3t");
      redactor.Add("s3cr3t-longer");
      redactor.Add("12345");
      // Short values, and values within longer words or numbers, aren't masked: masking them would tell what they are.
      redactor.Redact("it said s3cr3t-longer, then s3cr3t, about 12345, and xs3cr3t or s3cr3tx")
         .ShouldBe("it said ********, then ********, about 12345, and xs3cr3t or s3cr3tx");
      string clean = "nothing to see";
      redactor.Redact(clean).ShouldBeSameAs(clean);
   }

   [Fact]
   public void LoggersMaskMessagesValuesAndExceptions()
   {
      CapturedLogs logs = new();
      SecretRedactor redactor = new();
      redactor.Add("s3cr3t-value");
      using RedactingLoggerFactory factory = new(LoggerFactory.Create(b => b.AddProvider(logs)), redactor);
      ILogger logger = factory.CreateLogger("test");
#pragma warning disable CA1848, CA2254 // Plain calls, as a library might make.
      logger.LogWarning("Opening {ConnectionString} as {User}", "Host=db;Password=hunter2", "gd");
      logger.LogError(new InvalidOperationException("Signing in with s3cr3t-value failed", new FormatException("inner s3cr3t-value")), "It failed");
      InvalidOperationException plain = new("Nothing secret");
      logger.LogError(plain, "Plain");
#pragma warning restore CA1848, CA2254
      List<(string Category, string Message, string Values, string? Exception)> entries = [.. logs.Entries];
      // A setting's value is masked to its end: in text that isn't a connection string, the rest of the line.
      entries[0].Message.ShouldBe("Opening Host=db;Password=********");
      entries[0].Values.ShouldBe("ConnectionString=Host=db;Password=********; User=gd; {OriginalFormat}=Opening {ConnectionString} as {User}");
      string exception = entries[1].Exception.ShouldNotBeNull();
      exception.ShouldNotContain("s3cr3t-value");
      exception.ShouldStartWith("System.InvalidOperationException: Signing in with ******** failed");
      exception.ShouldContain("System.FormatException: inner ********");
      entries[2].Exception.ShouldBe(plain.ToString());
   }

   /// <summary>Connections' secrets, and the bootstrap password, are masked in whatever is logged, whatever logs them.</summary>
   [Fact]
   public async Task TheApplicationsSecretsAreMaskedInItsLog()
   {
      const string Secret = "log-secret-value-1";
      CapturedLogs logs = new();
      await using WebAppFactory factory = new() { ServiceChanges = services => services.AddSingleton<ILoggerProvider>(logs) };
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = await TestSources.AddAsync(admin, "pg", "postgres",
         new { mode = "raw", connectionString = "Host=127.0.0.1;Port=1;Database=shop;Username=reader;Password=" + Secret });
      await TestSources.SettledAsync(admin, id, "failed");
      (await (await admin.PostAsync($"/api/connections/{id}/test")).JsonAsync(HttpStatusCode.OK)).GetProperty("ok").GetBoolean().ShouldBeFalse();

      ILogger logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test");
#pragma warning disable CA1848, CA2254
      logger.LogWarning($"A library wrote {Secret}, and {TestApi.AdminPassword}");
#pragma warning restore CA1848, CA2254
      logs.Entries.ShouldContain(e => e.Message == "A library wrote ********, and ********");
      logs.Entries.ShouldContain(e => e.Category.Contains("Schema", StringComparison.Ordinal), "the schema's read failed, and was logged");
      logs.Texts.Where(t => t.Contains(Secret, StringComparison.Ordinal) || t.Contains(TestApi.AdminPassword, StringComparison.Ordinal)).ShouldBeEmpty();
   }
}
