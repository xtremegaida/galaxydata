namespace GalaxyData.Query.Diagnostics;

/// <summary>GDQ1xxx parse, GDQ2xxx bind (GDQ21xx warnings), GDQ3xxx plan, GDQ5xxx catalog.</summary>
public static class DiagnosticCodes
{
   public const string SyntaxError = "GDQ1001";

   public const string UnknownName = "GDQ2001";
   public const string AmbiguousName = "GDQ2002";
   public const string UnknownMethod = "GDQ2003";
   public const string UnknownFunction = "GDQ2004";
   public const string WrongArgumentCount = "GDQ2005";
   public const string TypeMismatch = "GDQ2006";
   public const string NotBoolean = "GDQ2007";
   public const string UnsupportedSyntax = "GDQ2008";
   public const string AssignmentInExpression = "GDQ2009";
   public const string DuplicateColumn = "GDQ2010";
   public const string InvalidCount = "GDQ2011";
   public const string UnknownParameter = "GDQ2012";
   public const string InvalidLiteral = "GDQ2013";
   public const string NotARecord = "GDQ2014";
   public const string NotAScalar = "GDQ2015";
   public const string NotAValue = "GDQ2016";
   public const string NamedArgumentNotAllowed = "GDQ2017";
   public const string SpreadNotAllowed = "GDQ2018";
   public const string LambdaParameters = "GDQ2019";
   public const string InvalidDefinition = "GDQ2020";
   public const string MissingResult = "GDQ2021";
   public const string StatementNotDefinition = "GDQ2022";
   public const string ThenByWithoutOrderBy = "GDQ2023";
   public const string NotSortable = "GDQ2024";
   public const string BrokenVirtualEntity = "GDQ2025";
   public const string VirtualEntityCycle = "GDQ2026";
   public const string NotGroupKey = "GDQ2027";
   public const string SetOperationColumns = "GDQ2028";
   public const string NotSupportedYet = "GDQ2099";
   public const string ShadowedName = "GDQ2101";

   public const string CrossSourceQuery = "GDQ3001";
   public const string NoProvider = "GDQ3002";
   public const string NotTranslatable = "GDQ3003";
   public const string NoSource = "GDQ3004";

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
   public const string InvalidEntityPath = "GDQ5015";
}
