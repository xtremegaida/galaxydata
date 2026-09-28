namespace GalaxyData.Query.Diagnostics;

/// <summary>GDQ1xxx parse, GDQ2xxx bind, GDQ3xxx plan, GDQ5xxx catalog.</summary>
public static class DiagnosticCodes
{
   public const string SyntaxError = "GDQ1001";

   public const string InvalidSourceAlias = "GDQ5001";
   public const string DuplicateSource = "GDQ5002";
   public const string ShortcutSuppressed = "GDQ5003";
   public const string UnknownEntity = "GDQ5004";
   public const string UnknownColumn = "GDQ5005";
   public const string MissingKey = "GDQ5006";
   public const string RelationShape = "GDQ5007";
   public const string RelationNotUnique = "GDQ5008";
   public const string RelationTypeMismatch = "GDQ5009";
   public const string ForeignKeyTargetMissing = "GDQ5010";
   public const string UnknownNavigation = "GDQ5011";
   public const string NavigationNameTaken = "GDQ5012";
   public const string DeclaredKeyIgnored = "GDQ5013";
   public const string VirtualEntityUnsupported = "GDQ5014";
   public const string InvalidEntityPath = "GDQ5015";
}
