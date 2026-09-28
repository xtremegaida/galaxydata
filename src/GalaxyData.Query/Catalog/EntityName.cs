using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using GalaxyData.Common;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Language;

namespace GalaxyData.Query.Catalog;

/// <summary>
/// A path through the catalog, e.g. <c>shop.main.orders</c> or <c>xl["Budget 2024"]["Sheet 1"]</c>. Each entity has a
/// canonical name (alias, schema, table); lookups also accept the default-schema shortcut <c>shop.orders</c>.
/// Equality is exact (ordinal), since sources may hold names that differ only by case.
/// </summary>
public sealed class EntityName : IEquatable<EntityName>
{
   private readonly string[] parts;

   public EntityName(params IEnumerable<string> parts)
   {
      ArgumentNullException.ThrowIfNull(parts);
      this.parts = [.. parts];
      if (this.parts.Length == 0) { throw new ArgumentException("A name needs at least one part", nameof(parts)); }
      foreach (string part in this.parts)
      {
         if (string.IsNullOrEmpty(part)) { throw new ArgumentException("Name parts cannot be empty", nameof(parts)); }
      }
   }

   public IReadOnlyList<string> Parts => parts;

   public string Last => parts[^1];

   public int Count => parts.Length;

   public static EntityName Parse(string text)
   {
      return TryParse(text, out EntityName? name) ? name : throw new FormatException($"'{text}' is not an entity name");
   }

   /// <summary>Parses a dotted path written in query syntax; <c>a["b c"]</c> quotes a part.</summary>
   public static bool TryParse(string? text, [NotNullWhen(true)] out EntityName? name)
   {
      name = null;
      if (string.IsNullOrWhiteSpace(text)) { return false; }
      ParseResult parsed = QueryParser.Default.Parse(text);
      if (parsed.Root == null) { return false; }
      List<string> path = new(4);
      if (!Collect(parsed.Root, path)) { return false; }
      name = new EntityName(path);
      return true;
   }

   private static bool Collect(SyntaxNode node, List<string> path)
   {
      switch (node)
      {
         case IdentifierSyntax identifier:
            path.Add(identifier.Name);
            return true;
         case BinarySyntax { Op: ".", Right: IdentifierSyntax member } binary:
            if (!Collect(binary.Left, path)) { return false; }
            path.Add(member.Name);
            return true;
         case CallSyntax { Kind: SyntaxKind.Index, Arguments: [LiteralSyntax literal] } index
            when literal.Value.Type == DynamicNodeType.String && literal.Value.GetString() is { Length: > 0 } part:
            if (!Collect(index.Target, path)) { return false; }
            path.Add(part);
            return true;
         default:
            return false;
      }
   }

   public EntityName Append(string part) => new(parts.Append(part));

   public bool Equals(EntityName? other) =>
      other != null && parts.AsSpan().SequenceEqual(other.parts);

   public override bool Equals(object? obj) => obj is EntityName other && Equals(other);

   public override int GetHashCode()
   {
      HashCode hash = new();
      foreach (string part in parts) { hash.Add(part, StringComparer.Ordinal); }
      return hash.ToHashCode();
   }

   public override string ToString() =>
      QueryText.IsBareIdentifier(parts[0]) ? QueryText.FormatPath(parts) : string.Join('.', parts);

   public static bool operator ==(EntityName? left, EntityName? right) => left?.Equals(right) ?? right is null;

   public static bool operator !=(EntityName? left, EntityName? right) => !(left == right);
}
