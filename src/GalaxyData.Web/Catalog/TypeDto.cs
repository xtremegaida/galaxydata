using GalaxyData.Query.Types;

namespace GalaxyData.Web.Catalog;

/// <summary>A logical type: its kind, and for decimals their precision and scale, for text and binary their length.</summary>
public sealed record TypeDto(ScalarKind Kind, bool Nullable, string Text)
{
   public int? Precision { get; init; }

   public int? Scale { get; init; }

   public int? Length { get; init; }

   public bool? IsAnsi { get; init; }

   public static TypeDto Of(ScalarType type) => new(type.Kind, type.Nullable, type.ToString())
   {
      Precision = type.Kind == ScalarKind.Decimal && type.Precision > 0 ? type.Precision : null,
      Scale = type.Kind == ScalarKind.Decimal && type.Precision > 0 ? type.Scale : null,
      Length = type.Kind is ScalarKind.String or ScalarKind.Binary && type.Length >= 0 ? type.Length : null,
      IsAnsi = type.Kind == ScalarKind.String && type.IsAnsi ? true : null,
   };
}
