using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GalaxyData.Web.Palettes;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Palettes;

/// <summary>Labels as palettes match them, by the cases the client is held to as well (<c>tests/fixtures/palette-labels.json</c>).</summary>
public sealed class LabelTextTests
{
   private static readonly JsonElement Cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "palette-labels.json"))).RootElement;

   private static LabelMatching Ignoring(JsonElement ignore)
   {
      HashSet<string> ignored = [.. ignore.EnumerateArray().Select(i => i.GetString()!)];
      return new LabelMatching(ignored.Contains("case"), ignored.Contains("whitespace"), ignored.Contains("brackets"), ignored.Contains("accents"));
   }

   [Fact]
   public void LabelsAreNormalisedAsThePaletteSays()
   {
      List<string> wrong = [];
      foreach (JsonElement item in Cases.GetProperty("normalize").EnumerateArray())
      {
         string text = item.GetProperty("text").GetString()!;
         string expected = item.GetProperty("expected").GetString()!;
         string normalized = LabelText.Normalize(text, Ignoring(item.GetProperty("ignore")));
         if (normalized != expected) { wrong.Add($"{JsonSerializer.Serialize(text)} {item.GetProperty("ignore").GetRawText()}: {JsonSerializer.Serialize(normalized)}, not {JsonSerializer.Serialize(expected)}"); }
      }
      wrong.ShouldBeEmpty();
   }

   [Fact]
   public void ValuesAreTheirTextAsAnswersCarryThem()
   {
      List<string> wrong = [];
      foreach (JsonElement item in Cases.GetProperty("values").EnumerateArray())
      {
         JsonElement value = item.GetProperty("value");
         string? expected = item.GetProperty("text").GetString();
         // As the JSON reads, and as rows hold them (ValueCodec's: whole numbers, doubles, text, booleans).
         object? held = value.ValueKind switch
         {
            JsonValueKind.Number when value.TryGetInt32(out int whole) && value.GetRawText().All(c => char.IsAsciiDigit(c) || c == '-') && value.GetRawText() != "-0" => whole,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
         };
         foreach (object? given in (object?[])[value, held])
         {
            string? text = LabelText.Of(given);
            if (text != expected) { wrong.Add($"{value.GetRawText()} ({given?.GetType().Name ?? "null"}): {text ?? "null"}, not {expected ?? "null"}"); }
         }
      }
      wrong.ShouldBeEmpty();
   }

   [Fact]
   public void NumbersAreWrittenAsJavaScriptWritesThem()
   {
      (double Value, string Text)[] cases =
      [
         (0.5, "0.5"), (-0.0, "0"), (1234.5678, "1234.5678"), (1e-6, "0.000001"), (1.234e-6, "0.000001234"), (9.999e20, "999900000000000000000"),
         (123e18, "123000000000000000000"), (1.5e300, "1.5e+300"), (double.NaN, "NaN"), (double.NegativeInfinity, "-Infinity"),
      ];
      cases.Select(c => LabelText.Number(c.Value)).ShouldBe(cases.Select(c => c.Text));
   }
}
