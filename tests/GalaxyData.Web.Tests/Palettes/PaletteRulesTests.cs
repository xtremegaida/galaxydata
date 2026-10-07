using System.Collections.Generic;
using System.Linq;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Palettes;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Palettes;

public sealed class PaletteRulesTests
{
   private static readonly PaletteRules Rules = new(new PaletteSettings());

   /// <summary>Two colours, by label, ignoring case and brackets, with an override of open's and of no value's.</summary>
   public static PaletteDefinition Statuses() => PaletteDefinition.New([new("#2A78D6", "#3987E5"), new("#eb6834", null)]) with
   {
      Matching = new LabelMatching(IgnoreCase: true, IgnoreWhitespace: false, IgnoreBrackets: true, IgnoreAccents: false),
      Overrides = [new PaletteOverride("Open", new PaletteColor("#1BAF7A", null)), new PaletteOverride(null, new PaletteColor("#9a9ca3", null))],
   };

   private static (PaletteDefinition? Checked, Dictionary<string, string[]> Errors) Check(PaletteDefinition? definition, PaletteRules? rules = null)
   {
      Dictionary<string, string[]> errors = [];
      return ((rules ?? Rules).Check(definition, "definition.", errors), errors);
   }

   private static string[] Fields(PaletteDefinition? definition, PaletteRules? rules = null) => [.. Check(definition, rules).Errors.Keys.Order()];

   [Fact]
   public void APaletteIsKeptWithItsColoursInLowerCase()
   {
      (PaletteDefinition? palette, Dictionary<string, string[]> errors) = Check(Statuses());
      errors.ShouldBeEmpty();
      palette!.Colors.ShouldBe([new PaletteColor("#2a78d6", "#3987e5"), new PaletteColor("#eb6834", null)]);
      palette.Overrides[0].Color.Light.ShouldBe("#1baf7a");
      string json = PaletteJson.Write(palette);
      PaletteJson.Write(PaletteJson.Read(json)).ShouldBe(json, "written as it reads back");
      json.ShouldContain("\"assign\":\"label\"");
   }

   [Fact]
   public void MistakesAreNamedByTheirFields()
   {
      Fields(null).ShouldBe(["definition"]);
      Fields(Statuses() with { Colors = [] }).ShouldBe(["definition.colors"]);
      Fields(Statuses() with { Colors = [new("blue", null), new("#12345", "#ABCDEFG")] }).ShouldBe(["definition.colors[0].light", "definition.colors[1].dark", "definition.colors[1].light"]);
      Fields(Statuses() with { Colors = [.. Enumerable.Repeat(new PaletteColor("#000000", null), 3)] }, new PaletteRules(new PaletteSettings { MaxColors = 2 }))
         .ShouldBe(["definition.colors"]);
      Fields(Statuses() with { Overrides = null! }).ShouldBe(["definition.overrides"]);
      Fields(Statuses() with { Matching = null! }).ShouldBe(["definition.matching"]);
      Fields(Statuses() with { Overrides = [new PaletteOverride(new string('x', 201), new PaletteColor("#000000", null)), new PaletteOverride("a", null!)] })
         .ShouldBe(["definition.overrides[0].label", "definition.overrides[1].color"]);
      Fields(Statuses() with { Overrides = [.. Enumerable.Range(0, 3).Select(i => new PaletteOverride($"l{i}", new PaletteColor("#000000", null)))] },
         new PaletteRules(new PaletteSettings { MaxOverrides = 2 })).ShouldBe(["definition.overrides"]);
   }

   [Fact]
   public void OverridesMatchSomethingAndNoTwoTheSame()
   {
      PaletteColor black = new("#000000", null);
      (_, Dictionary<string, string[]> errors) = Check(Statuses() with
      {
         Overrides = [new("Open", black), new("open (old)", black), new("(EMEA)", black), new(null, black), new(null, black), new("Shipped", black)],
      });
      errors.Keys.Order().ShouldBe(["definition.overrides[1].label", "definition.overrides[2].label", "definition.overrides[4].label"]);
      errors["definition.overrides[1].label"].ShouldBe(["It matches what overrides[0] matches"]);
      errors["definition.overrides[2].label"][0].ShouldStartWith("Nothing of this label is left");
      // Matched exactly, they are different labels.
      Check(Statuses() with { Matching = new LabelMatching(false, false, false, false), Overrides = [new("Open", black), new("open", black), new("(EMEA)", black)] })
         .Errors.ShouldBeEmpty();
   }

   [Fact]
   public void TooLongAPaletteIsRefused()
   {
      PaletteDefinition many = Statuses() with { Overrides = [.. Enumerable.Range(0, 100).Select(i => new PaletteOverride($"label {i:D3} " + new string('x', 150), new PaletteColor("#000000", null)))] };
      Fields(many, new PaletteRules(new PaletteSettings { MaxDefinitionLength = 4096 })).ShouldBe(["definition"]);
   }
}
