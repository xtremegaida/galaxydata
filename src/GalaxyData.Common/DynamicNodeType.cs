namespace GalaxyData.Common;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "On purpose")]
public enum DynamicNodeType
{
   Undefined,
   Null,
   False,
   True,
   Integer,
   Number,
   Decimal,
   Guid,
   DateTimeUtc,
   DateTimeLocal,
   DateTimeOffset,
   String,
   Binary,
   Array,
   Object,

   Function,
   Enumerable,
}
