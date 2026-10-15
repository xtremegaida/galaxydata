using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Tests.Catalog;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Dashboards;

/// <summary>
/// Charts limited from their rows read whole, in memory, give what their queries give: every case is asked of an
/// application that reads them whole and of one that doesn't (<c>MaxWholeRows</c> 0), and the answers are the same
/// but for when they were read. Over generated visits: skewed channels with ties, countries and channels that may be
/// null, text that differs only by case.
/// </summary>
public sealed class WholeRowsTests
{
   private static readonly Listens All = Definitions.All;

   private static Dimension D(string column, Bucket? bucket = null) => new(new FieldRef([], column), bucket, column);

   private static Measure Count() => new(Aggregate.Count, null, "Visits", null);

   private static Measure Of(Aggregate aggregate, string column) => new(aggregate, new FieldRef([], column), column, null);

   private static BarConfig Bar(Dimension dimension, Dimension? series, Measure measure, int limit = 25, WidgetSort? sort = null, int? seriesLimit = null, bool other = false) =>
      new("visits", [], true, All, dimension, series, [measure], sort ?? new WidgetSort(SortTarget.Measure, 0, true), limit, BarOrientation.Vertical, BarStack.None, false,
         LegendPosition.Bottom, null, null, SeriesLimit: seriesLimit, SeriesOther: seriesLimit == null ? null : other);

   private static LineConfig Line(Dimension x, Dimension series, Measure measure, int seriesLimit, bool other = true, int limit = 1000) =>
      new("visits", [], true, All, x, series, [measure], limit, false, LineGaps.Break, false, LegendPosition.Bottom, null, null, SeriesLimit: seriesLimit, SeriesOther: other);

   private static PieConfig Pie(Dimension dimension, Measure measure, int limit) =>
      new("visits", [], true, All, dimension, measure, limit, true, false, true, LegendPosition.Right);

   /// <summary>The charts, by id: each with the steps into its "Other" asked.</summary>
   private static readonly (string Id, WidgetConfig Config, int[] Steps)[] Charts =
   [
      ("weeks-by-channel", Bar(D("day", Bucket.Week), D("channel"), Count()), [0]),
      ("weeks-top-channels", Bar(D("day", Bucket.Week), D("channel"), Count(), seriesLimit: 3, other: true), [0, 3, 6, 9]),
      ("countries-pages", Bar(D("country"), D("channel"), Of(Aggregate.Sum, "pages"), limit: 5, sort: new WidgetSort(SortTarget.Measure, 0, false), seriesLimit: 2, other: true), [0, 2, 4]),
      ("countries-by-name", Bar(D("country"), D("device"), Count(), limit: 6, sort: new WidgetSort(SortTarget.Dimension, 0, false), seriesLimit: 1, other: true), [0, 1, 2]),
      ("countries-by-name-desc", Bar(D("country"), D("device"), Count(), limit: 6, sort: new WidgetSort(SortTarget.Dimension, 0, true), seriesLimit: 2, other: true), [0, 2]),
      ("channels-by-country", Bar(D("channel"), D("country"), Count(), limit: 4, seriesLimit: 4, other: true), [0, 4, 8]),
      ("first-day", Bar(D("device"), D("channel"), Of(Aggregate.Min, "day"), seriesLimit: 3, other: true), [0, 3]),
      ("longest", Bar(D("device"), D("country"), Of(Aggregate.Max, "seconds"), seriesLimit: 3, other: true), [0, 3]),
      ("amounts", Bar(D("device"), D("channel"), Of(Aggregate.Sum, "amount"), seriesLimit: 2, other: true), [0, 2]),
      ("average", Bar(D("device"), D("channel"), Of(Aggregate.Avg, "pages"), seriesLimit: 2, other: true), [0, 2]),
      ("days-top-channels", Line(D("day"), D("channel"), Count(), 3), [0, 3, 6]),
      ("days-cut", Line(D("day"), D("channel"), Count(), 2, limit: 30), [0, 2]),
      ("months-countries", Line(D("day", Bucket.Month), D("country"), Of(Aggregate.Sum, "pages"), 2), [0, 2, 4]),
      ("channels-pie", Pie(D("channel"), Count(), 3), [0, 3, 6, 9]),
      ("countries-pie", Pie(D("country"), Of(Aggregate.Sum, "amount"), 4), [0, 4, 8]),
   ];

   private static DashboardDefinition Slice() => Definitions.Blank() with
   {
      Sources = [new DashboardSource("visits", "web.visits", "Visits")],
      Widgets = [.. Charts.Select(c => new DashboardWidget(c.Id, null, c.Config))],
      Layout = DashboardLayout.Default() with { Items = Charts.Select((c, i) => (c.Id, new Placement(0, i, 12, 1))).ToDictionary(p => p.Id, p => p.Item2) },
   };

   /// <summary>
   /// 600 visits, the same every time: channels skewed (and two that differ by case), some null; countries, some null;
   /// whole numbers of pages and amounts (some null), seconds as doubles.
   /// </summary>
   private static string Visits()
   {
      Random random = new(7);
      string[] channels = ["paid", "paid", "paid", "direct", "direct", "organic", "organic", "social", "email", "referral", "Campaign", "campaign", "internal"];
      string[] countries = ["ZA", "ZA", "ZA", "US", "KE", "ZM", "ZW", "SG", "MW", "TZ", "ET", "NG", "GB", "DE", "FR"];
      string[] devices = ["mobile", "mobile", "mobile", "desktop", "desktop", "tablet", "tv"];
      StringBuilder sql = new("""
         CREATE TABLE visits (
            id INTEGER PRIMARY KEY,
            day DATE NOT NULL,
            channel TEXT,
            country TEXT,
            device TEXT NOT NULL,
            pages INTEGER NOT NULL,
            amount INTEGER,
            seconds REAL
         );

         """);
      static string Text(string? value) => value == null ? "NULL" : $"'{value}'";
      for (int i = 1; i <= 600; i++)
      {
         string day = new DateOnly(2026, 1, 1).AddDays(random.Next(70)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
         string? channel = random.Next(12) == 0 ? null : channels[random.Next(channels.Length)];
         string? country = random.Next(9) == 0 ? null : countries[random.Next(countries.Length)];
         string device = devices[random.Next(devices.Length)];
         string amount = random.Next(4) == 0 ? "NULL" : random.Next(1, 500).ToString(CultureInfo.InvariantCulture);
         string seconds = random.Next(5) == 0 ? "NULL" : (random.Next(1, 100_000) / 8.0).ToString("R", CultureInfo.InvariantCulture);
         sql.Append(CultureInfo.InvariantCulture,
            $"INSERT INTO visits VALUES ({i}, '{day}', {Text(channel)}, {Text(country)}, '{device}', {random.Next(1, 10)}, {amount}, {seconds});\n");
      }
      return sql.ToString();
   }

   private static async Task<(WebAppFactory Factory, TestApi Admin, string Path)> WebAsync(int maxWholeRows)
   {
      WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Dashboards:MaxWholeRows"] = maxWholeRows.ToString(CultureInfo.InvariantCulture) } };
      TestApi admin = await TestApi.SignedInAsync(factory);
      string path = Path.Combine(TestSources.Files(factory), "web.db");
      await TestSources.SqliteAsync(path, Visits());
      await TestSources.AddSqliteAsync(admin, "web", path);
      return (factory, admin, path);
   }

   private static async Task<JsonElement> DataAsync(TestApi api, DashboardDefinition slice, string widget, int offset, bool refresh = false) =>
      await (await api.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget, page = offset == 0 ? null : new { offset }, refresh }))
         .JsonAsync(HttpStatusCode.OK);

   private static async Task<JsonElement> QueryAsync(TestApi api, DashboardDefinition slice, string widget) =>
      await (await api.PostAsync("/api/dashboards/query", new { slice = Definitions.Json(slice), widget })).JsonAsync(HttpStatusCode.OK);

   /// <summary>An answer but for when its rows were read and whether they were kept.</summary>
   private static string Answer(JsonElement data)
   {
      JsonObject answer = JsonNode.Parse(data.GetRawText())!.AsObject();
      answer.Remove("refreshedAt");
      answer.Remove("cached");
      return answer.ToJsonString();
   }

   [Fact]
   public async Task ChartsReadWholeGiveWhatTheirQueriesGive()
   {
      (WebAppFactory queries, TestApi byQueries, _) = await WebAsync(0);
      await using WebAppFactory _q = queries;
      (WebAppFactory whole, TestApi byWhole, _) = await WebAsync(20_000);
      await using WebAppFactory _w = whole;
      DashboardDefinition slice = Slice();
      foreach ((string id, _, int[] steps) in Charts)
      {
         foreach (int step in steps)
         {
            string expected = Answer(await DataAsync(byQueries, slice, id, step));
            JsonElement answer = await DataAsync(byWhole, slice, id, step);
            Answer(answer).ShouldBe(expected, $"{id} at {step}");
            // A step asked for the first time is kept only when it is worked out from rows read whole before (for the first
            // step, or another chart's of the same rows).
            if (step != steps[0]) { answer.GetProperty("cached").GetBoolean().ShouldBe(id != "average", $"{id} at {step}: read whole"); }
         }
         // Steps back, from the rows kept.
         Answer(await DataAsync(byWhole, slice, id, 0)).ShouldBe(Answer(await DataAsync(byQueries, slice, id, 0)), $"{id} back at 0");
      }

      // The charts that are read whole are read in one query, whatever their step; an average doesn't add up from its groups'.
      JsonElement one = await QueryAsync(byWhole, slice, "weeks-top-channels");
      one.GetProperty("main").GetProperty("text").GetString()
         .ShouldBe("web.visits.groupBy(d0: startOfWeek(day), s0: channel).select(d0, s0, m0: count()).orderBy(d0, s0).take(20001)");
      one.GetProperty("categories").ValueKind.ShouldBe(JsonValueKind.Null);
      new[] { "series", "rest" }.Where(name => one.TryGetProperty(name, out _)).ShouldBeEmpty();
      (await QueryAsync(byWhole, slice, "channels-pie")).GetProperty("total").ValueKind.ShouldBe(JsonValueKind.Null);
      (await QueryAsync(byWhole, slice, "average")).TryGetProperty("series", out _).ShouldBeTrue("ranked by queries");
      (await QueryAsync(byQueries, slice, "weeks-top-channels")).TryGetProperty("series", out _).ShouldBeTrue();
   }

   [Fact]
   public async Task StepsAreOfTheRowsReadFirst()
   {
      (WebAppFactory queries, TestApi byQueries, _) = await WebAsync(0);
      await using WebAppFactory _q = queries;
      (WebAppFactory whole, TestApi byWhole, string path) = await WebAsync(20_000);
      await using WebAppFactory _w = whole;
      DashboardDefinition slice = Slice();
      string before = Answer(await DataAsync(byQueries, slice, "channels-pie", 3));

      JsonElement first = await DataAsync(byWhole, slice, "channels-pie", 0);
      first.GetProperty("cached").GetBoolean().ShouldBeFalse();
      // Many more visits of a channel that was small: a ranking read again would change.
      await TestSources.SqliteAsync(path, string.Concat(Enumerable.Range(1001, 300).Select(i =>
         $"INSERT INTO visits VALUES ({i}, '2026-02-01', 'internal', 'ZA', 'mobile', 1, 1, 1.0);\n")));
      JsonElement step = await DataAsync(byWhole, slice, "channels-pie", 3);
      Answer(step).ShouldBe(before, "the rows read for the first step");
      (step.GetProperty("cached").GetBoolean(), step.GetProperty("refreshedAt").GetString())
         .ShouldBe((true, first.GetProperty("refreshedAt").GetString()), "as old as the rows read whole");

      JsonElement refreshed = await DataAsync(byWhole, slice, "channels-pie", 0, refresh: true);
      Answer(refreshed).ShouldContain("\"internal\"", Case.Sensitive, "read again: the largest channel now");
      refreshed.GetProperty("cached").GetBoolean().ShouldBeFalse();
   }

   [Fact]
   public async Task RowsTooManyToReadWholeAreLimitedByQueries()
   {
      (WebAppFactory queries, TestApi byQueries, _) = await WebAsync(0);
      await using WebAppFactory _q = queries;
      (WebAppFactory small, TestApi bySmall, _) = await WebAsync(5);
      await using WebAppFactory _s = small;
      DashboardDefinition slice = Slice();
      (await QueryAsync(bySmall, slice, "weeks-top-channels")).GetProperty("main").GetProperty("text").GetString()!.ShouldEndWith(".take(6)", Case.Sensitive,
         "not yet known to be too many");
      foreach (int step in new[] { 0, 3 })
      {
         Answer(await DataAsync(bySmall, slice, "weeks-top-channels", step)).ShouldBe(Answer(await DataAsync(byQueries, slice, "weeks-top-channels", step)));
      }
      (await QueryAsync(bySmall, slice, "weeks-top-channels")).TryGetProperty("series", out _).ShouldBeTrue("too many, remembered: by queries");
   }
}
