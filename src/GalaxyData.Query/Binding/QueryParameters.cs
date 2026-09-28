using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using GalaxyData.Common;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Binding;

public sealed record QueryParameter(string Name, object? Value, ScalarType Type);

/// <summary>
/// Values for the <c>$name</c> parameters of a query. Names may be given with or without the <c>$</c>; values are
/// CLR scalars (or <see cref="DynamicNode"/>s, which are unwrapped) and their type follows from the value.
/// </summary>
public sealed class QueryParameters
{
   private readonly Dictionary<string, QueryParameter> byName = new(StringComparer.Ordinal);

   public static QueryParameters Empty { get; } = new();

   public IEnumerable<QueryParameter> All => byName.Values;

   public int Count => byName.Count;

   public QueryParameters Add(string name, object? value)
   {
      ArgumentException.ThrowIfNullOrEmpty(name);
      string key = Normalize(name);
      object? clr = Unwrap(value);
      byName[key] = new QueryParameter(key, clr, TypeOf(clr));
      return this;
   }

   public QueryParameters Add(string name, object? value, ScalarType type)
   {
      ArgumentException.ThrowIfNullOrEmpty(name);
      string key = Normalize(name);
      byName[key] = new QueryParameter(key, Unwrap(value), type);
      return this;
   }

   public bool TryGet(string name, [NotNullWhen(true)] out QueryParameter? parameter) => byName.TryGetValue(Normalize(name), out parameter);

   private static string Normalize(string name) => name.StartsWith('$') ? name[1..] : name;

   private static object? Unwrap(object? value) => value is DynamicNode node ? FromNode(node) : value;

   private static object? FromNode(DynamicNode node) => node.Type switch
   {
      DynamicNodeType.Null or DynamicNodeType.Undefined => null,
      DynamicNodeType.True => true,
      DynamicNodeType.False => false,
      DynamicNodeType.Integer => node.GetInt64(),
      DynamicNodeType.Number => node.GetFlt64(),
      DynamicNodeType.Decimal => node.GetDecimal(),
      DynamicNodeType.Guid => node.GetGuid(),
      DynamicNodeType.String => node.GetString(),
      DynamicNodeType.DateTimeLocal => node.GetDateTimeLocal(),
      DynamicNodeType.DateTimeUtc => node.GetDateTimeUtc(),
      DynamicNodeType.DateTimeOffset => node.GetDateTimeOffset(),
      DynamicNodeType.Binary => node.GetBinary(),
      _ => throw new ArgumentException($"A {node.Type} can't be a query parameter", nameof(node)),
   };

   /// <summary>The logical type of a CLR value; null gives a nullable unknown, which takes the type of what it meets.</summary>
   public static ScalarType TypeOf(object? value) => (value switch
   {
      null => ScalarType.Unknown,
      bool => ScalarType.Boolean,
      byte or sbyte or short => ScalarType.Int16,
      ushort or int => ScalarType.Int32,
      uint or long => ScalarType.Int64,
      ulong => ScalarType.Decimal(20, 0),
      decimal => ScalarType.Decimal(),
      float => ScalarType.Single,
      double => ScalarType.Double,
      string => ScalarType.Text(),
      char => ScalarType.Text(1),
      Guid => ScalarType.Guid,
      DateOnly => ScalarType.Date,
      TimeOnly or TimeSpan => ScalarType.Time,
      DateTime => ScalarType.DateTime,
      DateTimeOffset => ScalarType.DateTimeOffset,
      byte[] => ScalarType.Binary,
      _ => throw new ArgumentException($"A {value.GetType().Name} can't be a query parameter", nameof(value)),
   }).WithNullable(value == null);
}
