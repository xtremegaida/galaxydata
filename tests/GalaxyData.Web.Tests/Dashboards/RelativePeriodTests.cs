using System;
using System.Globalization;
using System.Text.Json;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Queries;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Dashboards;

/// <summary>
/// The days of periods relative to today, from the first to the day after the last: those that leave today out are
/// as they were at the end of yesterday, and the periods that don't end today are as they are.
/// </summary>
public sealed class RelativePeriodTests
{
   private static DateOnly Day(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

   [Theory]
   // 2026-10-19 is a Monday; 2026-03-01 the first of a month.
   [InlineData(RelativeUnit.Day, 30, RelativeMode.Last, false, "2026-10-19", "2026-09-20", "2026-10-20")]
   [InlineData(RelativeUnit.Day, 30, RelativeMode.Last, true, "2026-10-19", "2026-09-19", "2026-10-19")]
   [InlineData(RelativeUnit.Day, 1, RelativeMode.Last, true, "2026-10-19", "2026-10-18", "2026-10-19")]
   [InlineData(RelativeUnit.Month, 3, RelativeMode.Last, true, "2026-10-19", "2026-07-19", "2026-10-19")]
   [InlineData(RelativeUnit.Month, 1, RelativeMode.ToDate, true, "2026-10-19", "2026-10-01", "2026-10-19")]
   [InlineData(RelativeUnit.Month, 1, RelativeMode.ToDate, true, "2026-03-01", "2026-02-01", "2026-03-01")]
   [InlineData(RelativeUnit.Week, 1, RelativeMode.ToDate, true, "2026-10-19", "2026-10-12", "2026-10-19")]
   [InlineData(RelativeUnit.Year, 1, RelativeMode.ToDate, true, "2026-01-01", "2025-01-01", "2026-01-01")]
   [InlineData(RelativeUnit.Month, 1, RelativeMode.This, true, "2026-10-19", "2026-10-01", "2026-11-01")]
   [InlineData(RelativeUnit.Month, 1, RelativeMode.Previous, true, "2026-03-01", "2026-02-01", "2026-03-01")]
   public void APeriodLeavingTodayOutIsAsItWasYesterday(RelativeUnit unit, int count, RelativeMode mode, bool excludeToday, string today, string start, string end)
   {
      ConditionWriter.Period(new RelativeRange(unit, count, mode, excludeToday), Day(today)).ShouldBe((Day(start), (DateOnly?)Day(end)));
   }

   [Theory]
   [InlineData(RelativeUnit.Day, 30, RelativeMode.Last, true, "2026-10-19", "2026-08-20", "2026-09-19")]
   // To date, compared up to the same day of the period before as its last: yesterday's.
   [InlineData(RelativeUnit.Month, 1, RelativeMode.ToDate, true, "2026-10-19", "2026-09-01", "2026-09-19")]
   [InlineData(RelativeUnit.Month, 1, RelativeMode.ToDate, true, "2026-03-01", "2026-01-01", "2026-01-29")]
   [InlineData(RelativeUnit.Month, 1, RelativeMode.ToDate, false, "2026-03-01", "2026-02-01", "2026-02-02")]
   public void ThePeriodBeforeOneLeavingTodayOutIsBeforeIt(RelativeUnit unit, int count, RelativeMode mode, bool excludeToday, string today, string start, string end)
   {
      ConditionWriter.Before(new RelativeRange(unit, count, mode, excludeToday), Day(today)).ShouldBe((Day(start), (DateOnly?)Day(end)));
   }

   [Fact]
   public void LeavingTodayOutIsWrittenOnlyWhenItIs()
   {
      JsonSerializer.Serialize(new RelativeRange(RelativeUnit.Day, 30, RelativeMode.Last), DefinitionJson.Options)
         .ShouldBe("""{"unit":"day","count":30,"mode":"last"}""");
      JsonSerializer.Serialize(new RelativeRange(RelativeUnit.Day, 30, RelativeMode.Last, true), DefinitionJson.Options)
         .ShouldBe("""{"unit":"day","count":30,"mode":"last","excludeToday":true}""");
      JsonSerializer.Deserialize<RelativeRange>("""{"unit":"month","count":1,"mode":"toDate","excludeToday":true}""", DefinitionJson.Options)
         .ShouldBe(new RelativeRange(RelativeUnit.Month, 1, RelativeMode.ToDate, true));
      // False is as left out.
      RelativeRange given = JsonSerializer.Deserialize<RelativeRange>("""{"unit":"day","count":7,"mode":"last","excludeToday":false}""", DefinitionJson.Options)!;
      given.ShouldBe(new RelativeRange(RelativeUnit.Day, 7, RelativeMode.Last));
      JsonSerializer.Serialize(given, DefinitionJson.Options).ShouldBe("""{"unit":"day","count":7,"mode":"last"}""");
   }
}
