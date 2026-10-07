using System.Collections.Generic;

namespace GalaxyData.Web.Palettes;

/// <summary>
/// A palette's colours and how labels get them: overrides first (by label, matched as <see cref="Matching"/> says),
/// then by the order charts meet labels in, or by their label (a hash, so a label has its colour everywhere).
/// <see cref="Distinct"/>: within a chart, what would take a colour already taken takes the next one free;
/// <see cref="WhenOut"/>: what past the last colour (or with none free) gets.
/// </summary>
public sealed record PaletteDefinition(
   List<PaletteColor> Colors,
   PaletteAssign Assign,
   bool Distinct,
   PaletteWhenOut WhenOut,
   LabelMatching Matching,
   List<PaletteOverride> Overrides)
{
   /// <summary>A new palette's ways, with <paramref name="colors"/>.</summary>
   public static PaletteDefinition New(List<PaletteColor> colors) =>
      new(colors, PaletteAssign.Label, Distinct: true, PaletteWhenOut.Repeat, new LabelMatching(IgnoreCase: true, IgnoreWhitespace: false, IgnoreBrackets: false, IgnoreAccents: false), []);
}

/// <summary>A colour (<c>#rrggbb</c>), and another for dark schemes (null: the same).</summary>
public sealed record PaletteColor(string Light, string? Dark);

public enum PaletteAssign
{
   /// <summary>In the order a chart meets its labels.</summary>
   Order,

   /// <summary>By a hash of the label, so a label has one colour in every chart.</summary>
   Label,
}

public enum PaletteWhenOut
{
   /// <summary>The colours again, from the first.</summary>
   Repeat,

   /// <summary>Grey, as "Other" is.</summary>
   Neutral,
}

/// <summary>What labels are matched without: case, whitespace, accents, and text in brackets.</summary>
public sealed record LabelMatching(bool IgnoreCase, bool IgnoreWhitespace, bool IgnoreBrackets, bool IgnoreAccents);

/// <summary>The colour of a label, whichever way the palette assigns the others; null is the label of no value.</summary>
public sealed record PaletteOverride(string? Label, PaletteColor Color);
