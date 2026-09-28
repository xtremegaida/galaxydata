using System;
using GalaxyData.Query.Types;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Types;

public sealed class TypeRulesTests
{
   [Theory]
   [InlineData("int16", "int16", "int16")]
   [InlineData("int16", "int32", "int32")]
   [InlineData("int64", "int32?", "int64?")]
   [InlineData("int64", "decimal(10,2)", "decimal")]
   [InlineData("decimal(10,2)", "decimal(10,2)", "decimal(10,2)")]
   [InlineData("decimal(10,2)", "decimal(12,4)", "decimal")]
   [InlineData("decimal(10,2)", "double", "double")]
   [InlineData("single", "single", "single")]
   [InlineData("single", "int32", "double")]
   public void NumbersPromote(string a, string b, string expected)
   {
      TypeRules.PromoteNumeric(ScalarType.Parse(a), ScalarType.Parse(b)).ToString().ShouldBe(expected);
   }

   [Fact]
   public void OnlyNumbersPromote()
   {
      TypeRules.PromoteNumeric(ScalarType.Int32, ScalarType.Text()).ShouldBeNull();
   }

   [Theory]
   [InlineData("string(10)", "string(10)", "string(10)")]
   [InlineData("string(10)", "string(20)?", "string?")]
   [InlineData("date", "datetime", "datetime")]
   [InlineData("datetime", "datetimeoffset", "datetimeoffset")]
   [InlineData("unknown?", "int32", "int32?")]
   [InlineData("int32", "decimal(5,1)", "decimal")]
   public void BranchesUnify(string a, string b, string expected)
   {
      TypeRules.Unify(ScalarType.Parse(a), ScalarType.Parse(b)).ToString().ShouldBe(expected);
   }

   [Theory]
   [InlineData("int32", "string", false)]
   [InlineData("date", "datetimeoffset", true)]
   [InlineData("boolean", "boolean", true)]
   [InlineData("json", "json", false)]
   public void Comparability(string a, string b, bool expected)
   {
      TypeRules.AreComparable(ScalarType.Parse(a), ScalarType.Parse(b), ordering: false, out _).ShouldBe(expected);
   }

   [Fact]
   public void GuidsCompareForEqualityButNotOrder()
   {
      TypeRules.AreComparable(ScalarType.Guid, ScalarType.Guid, ordering: false, out _).ShouldBeTrue();
      TypeRules.AreComparable(ScalarType.Guid, ScalarType.Guid, ordering: true, out string? reason).ShouldBeFalse();
      reason.ShouldNotBeNull();
   }

   [Fact]
   public void IntegersNarrowOnlyWhenTheyFit()
   {
      TypeRules.TryConvertConstant(40000L, ScalarType.Int64, ScalarType.Int16, out _, out _, out _).ShouldBeFalse();
      TypeRules.TryConvertConstant(400L, ScalarType.Int64, ScalarType.Int16, out object? small, out ScalarType type, out _).ShouldBeTrue();
      small.ShouldBe((short)400);
      type.ToString().ShouldBe("int16");
   }

   [Fact]
   public void TextParsesToTheTargetKind()
   {
      TypeRules.TryConvertConstant("2026-01-05", ScalarType.Text(), ScalarType.Date, out object? date, out _, out _).ShouldBeTrue();
      date.ShouldBe(new DateOnly(2026, 1, 5));
      TypeRules.TryConvertConstant("2026-01-05 10:30", ScalarType.Text(), ScalarType.DateTime, out object? dateTime, out _, out _).ShouldBeTrue();
      dateTime.ShouldBe(new DateTime(2026, 1, 5, 10, 30, 0, DateTimeKind.Unspecified));
      TypeRules.TryConvertConstant("10:15:00", ScalarType.Text(), ScalarType.Time, out object? time, out _, out _).ShouldBeTrue();
      time.ShouldBe(new TimeOnly(10, 15));
      TypeRules.TryConvertConstant("2026-01-05T10:00:00+02:00", ScalarType.Text(), ScalarType.DateTimeOffset, out object? offset, out _, out _).ShouldBeTrue();
      offset.ShouldBe(new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.FromHours(2)));
   }

   [Theory]
   [InlineData("05/01/2026", "date")]
   [InlineData("2026-01-05T10:00:00Z", "datetime")]
   [InlineData("2026-01-05T10:00:00+02:00", "datetime")]
   [InlineData("not a guid", "guid")]
   public void BadTextIsAnError(string text, string target)
   {
      TypeRules.TryConvertConstant(text, ScalarType.Text(), ScalarType.Parse(target), out _, out _, out string? error).ShouldBeFalse();
      error.ShouldNotBeNull();
   }

   [Fact]
   public void TextAdoptsTheTargetLength()
   {
      TypeRules.TryConvertConstant("open", ScalarType.Text().AsNonNullable(), ScalarType.Text(20, ansi: true), out _, out ScalarType type, out _).ShouldBeTrue();
      type.ToString().ShouldBe("string(20,ansi)");
   }

   [Fact]
   public void NullTakesTheTargetType()
   {
      TypeRules.TryConvertConstant(null, ScalarType.Unknown, ScalarType.Date, out object? value, out ScalarType type, out _).ShouldBeTrue();
      value.ShouldBeNull();
      type.ToString().ShouldBe("date?");
   }
}
