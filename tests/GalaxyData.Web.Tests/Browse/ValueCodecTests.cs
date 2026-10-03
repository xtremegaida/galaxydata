using System;
using System.Text.Json;
using GalaxyData.Query.Types;
using GalaxyData.Web.Browse;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Browse;

/// <summary>Values of each type as they travel, both ways.</summary>
public sealed class ValueCodecTests
{
   private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

   private static string Sent(object? value, string type) => JsonSerializer.Serialize(ValueCodec.Encode(value, ScalarType.Parse(type)));

   [Fact]
   public void ValuesAreSentAsJsonHoldsThem()
   {
      Sent(null, "int64?").ShouldBe("null");
      Sent(true, "boolean").ShouldBe("true");
      Sent((short)7, "int16").ShouldBe("7");
      Sent(-12, "int32").ShouldBe("-12");
      Sent(9_007_199_254_740_993L, "int64").ShouldBe("\"9007199254740993\"", "past 2^53, which a JavaScript number can't hold");
      Sent(10.50m, "decimal(10,2)").ShouldBe("\"10.50\"", "with its scale");
      Sent(1.5f, "single").ShouldBe("1.5");
      Sent(0.1f, "single").ShouldBe("0.1", "as short as the single, not as its double");
      Sent(0.1, "double").ShouldBe("0.1");
      Sent(double.NaN, "double").ShouldBe("\"NaN\"");
      Sent(double.NegativeInfinity, "double").ShouldBe("\"-Infinity\"");
      Sent("a 'b'", "string").ShouldBe("\"a \\u0027b\\u0027\"");
      Sent(new byte[] { 1, 2, 255 }, "binary").ShouldBe("\"AQL/\"");
      Sent(new Guid("2f1c0000-0000-4000-8000-000000000001"), "guid").ShouldBe("\"2f1c0000-0000-4000-8000-000000000001\"");
      Sent(new DateOnly(2026, 3, 1), "date").ShouldBe("\"2026-03-01\"");
      Sent(new TimeOnly(9, 5, 0), "time").ShouldBe("\"09:05:00\"");
      Sent(new TimeOnly(9, 5, 0, 250), "time").ShouldBe("\"09:05:00.25\"");
      Sent(new DateTime(2026, 3, 1, 14, 30, 0), "datetime").ShouldBe("\"2026-03-01T14:30:00\"");
      Sent(new DateTime(2026, 3, 1, 14, 30, 0).AddTicks(1234567), "datetime").ShouldBe("\"2026-03-01T14:30:00.1234567\"");
      Sent(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), "datetimeoffset").ShouldBe("\"2026-03-01T12:00:00\\u002B00:00\"");
      Sent(new TimeSpan(1, 2, 3, 4), "interval").ShouldBe("\"1.02:03:04\"");
      Sent(new[] { 1, 2 }, "unknown").ShouldBe("\"[1, 2]\"", "values of unknown types as text");
   }

   [Theory]
   [InlineData("true", "boolean", true)]
   [InlineData("7", "int16", (short)7)]
   [InlineData("\"7\"", "int32", 7)]
   [InlineData("\"9007199254740993\"", "int64", 9_007_199_254_740_993L)]
   [InlineData("42", "int64", 42L)]
   [InlineData("1.5", "double", 1.5)]
   [InlineData("\"Infinity\"", "double", double.PositiveInfinity)]
   [InlineData("\"x\"", "string", "x")]
   [InlineData("null", "string?", null)]
   public void ValuesAreRead(string json, string type, object? expected) => ValueCodec.Decode(Json(json), ScalarType.Parse(type)).ShouldBe(expected);

   [Fact]
   public void ValuesOfEachTypeGoBothWays()
   {
      object[] values =
      [
         10.50m, new byte[] { 0, 9 }, new Guid("2f1c0000-0000-4000-8000-000000000001"), new DateOnly(2026, 3, 1), new TimeOnly(23, 59, 59, 999),
         new DateTime(2026, 3, 1, 1, 2, 3).AddTicks(7), new DateTimeOffset(2026, 3, 1, 1, 2, 3, TimeSpan.FromHours(2)), TimeSpan.FromMinutes(90),
      ];
      string[] types = ["decimal(10,2)", "binary", "guid", "date", "time", "datetime", "datetimeoffset", "interval"];
      for (int i = 0; i < values.Length; i++)
      {
         ScalarType type = ScalarType.Parse(types[i]);
         ValueCodec.Decode(JsonSerializer.SerializeToElement(ValueCodec.Encode(values[i], type)), type).ShouldBe(values[i], types[i]);
      }
      ValueCodec.Decode(Json("12.5"), ScalarType.Decimal(10, 2)).ShouldBe(12.5m, "a number may be sent as one");
      ValueCodec.Decode(Json("\"2026-03-01\""), ScalarType.DateTime).ShouldBe(new DateTime(2026, 3, 1));
      ValueCodec.Decode(Json("\"2026-03-01 10:30\""), ScalarType.DateTime).ShouldBe(new DateTime(2026, 3, 1, 10, 30, 0));
      ValueCodec.Decode(Json("\"2026-03-01T10:30:00\""), ScalarType.DateTimeOffset).ShouldBe(new DateTimeOffset(2026, 3, 1, 10, 30, 0, TimeSpan.Zero), "in UTC unless it says");
      ValueCodec.Decode(Json("\"2026-03-01T10:30:00Z\""), ScalarType.DateTimeOffset).ShouldBe(new DateTimeOffset(2026, 3, 1, 10, 30, 0, TimeSpan.Zero));
      ValueCodec.Decode(Json("\"2026-03-01 10:30-05:00\""), ScalarType.DateTimeOffset).ShouldBe(new DateTimeOffset(2026, 3, 1, 10, 30, 0, TimeSpan.FromHours(-5)));
      ValueCodec.Decode(Json("\"-12.5\""), ScalarType.Decimal(10, 2)).ShouldBe(-12.5m);
   }

   [Theory]
   [InlineData("\"abc\"", "int32", "\"abc\" isn't a whole number")]
   [InlineData("1.5", "int64", "1.5 isn't a whole number")]
   [InlineData("70000", "int16", "70000 isn't a whole number")]
   [InlineData("\"yes\"", "boolean", "\"yes\" isn't true or false")]
   [InlineData("\"1/3/2026\"", "date", "\"1/3/2026\" isn't a date (yyyy-MM-dd)")]
   [InlineData("12", "string", "12 isn't text")]
   [InlineData("\"not base64!\"", "binary", "\"not base64!\" isn't base64 text")]
   [InlineData("\"12,25\"", "decimal(10,2)", "\"12,25\" isn't a number")]
   [InlineData("\"1,000\"", "decimal(10,2)", "\"1,000\" isn't a number")]
   [InlineData("\"01/05/2026 10:00\"", "datetimeoffset", "\"01/05/2026 10:00\" isn't a date-time with an offset (yyyy-MM-ddTHH:mm:ss+02:00)")]
   [InlineData("\"10:30\"", "datetimeoffset", "\"10:30\" isn't a date-time with an offset (yyyy-MM-ddTHH:mm:ss+02:00)")]
   public void ValuesOfTheWrongKindAreSaidToBe(string json, string type, string message) =>
      Should.Throw<ValueFormatException>(() => ValueCodec.Decode(Json(json), ScalarType.Parse(type))).Message.ShouldBe(message);
}
