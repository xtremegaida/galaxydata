using System;
using System.Numerics;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Types;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Execution;

public sealed class ValueConverterTests
{
   private static object? Read(object? value, string type) => ValueConverter.ToLogical(value, ScalarType.Parse(type));

   [Fact]
   public void DecimalsGetTheirScale()
   {
      Read(250L, "decimal(10,2)").ShouldBe(250.00m);
      Read(250L, "decimal(10,2)")!.ToString().ShouldBe("250.00");
      Read(99.5d, "decimal(10,2)")!.ToString().ShouldBe("99.50");
      Read("12.25", "decimal(10,2)")!.ToString().ShouldBe("12.25");
      Read(1.005m, "decimal")!.ToString().ShouldBe("1.005");
      Read(200.00m, "decimal")!.ToString().ShouldBe("200");
   }

   [Fact]
   public void WholeNumbersNarrowWhenTheyFit()
   {
      Read(7L, "int32").ShouldBe(7);
      Read(new BigInteger(3), "int64").ShouldBe(3L);
      Read((sbyte)-1, "int32").ShouldBe(-1);
      Should.Throw<OverflowException>(() => Read(70000L, "int16"));
      Should.Throw<InvalidCastException>(() => Read(2.5d, "int64"));
   }

   [Fact]
   public void SqliteTextBecomesTemporalValues()
   {
      Read("2026-01-05", "date").ShouldBe(new DateOnly(2026, 1, 5));
      Read("2026-01-05 10:30:00", "datetime").ShouldBe(new DateTime(2026, 1, 5, 10, 30, 0));
      Read("2026-01-05 10:30:00", "date").ShouldBe(new DateOnly(2026, 1, 5));
      Read("10:15:00", "time").ShouldBe(new TimeOnly(10, 15));
      Should.Throw<FormatException>(() => Read("5 Jan 2026", "date"));
   }

   [Fact]
   public void BooleansComeFromNumbersAndText()
   {
      Read(1L, "boolean").ShouldBe(true);
      Read(0L, "boolean").ShouldBe(false);
      Read("true", "boolean").ShouldBe(true);
      Should.Throw<FormatException>(() => Read("maybe", "boolean"));
   }

   [Fact]
   public void GuidsComeFromTextAndBytes()
   {
      Guid guid = Guid.Parse("2f1c0000-0000-4000-8000-000000000001");
      Read("2F1C0000-0000-4000-8000-000000000001", "guid").ShouldBe(guid);
      Read(guid.ToByteArray(), "guid").ShouldBe(guid);
   }

   [Fact]
   public void UtcDateTimesBecomeOffsets()
   {
      DateTime utc = new(2026, 1, 5, 8, 30, 0, DateTimeKind.Utc);
      Read(utc, "datetimeoffset").ShouldBe(new DateTimeOffset(2026, 1, 5, 8, 30, 0, TimeSpan.Zero));
   }

   [Fact]
   public void NullStaysNull()
   {
      Read(null, "int32").ShouldBeNull();
      Read(DBNull.Value, "string").ShouldBeNull();
   }
}
