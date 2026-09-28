using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Results;

public sealed class ResultColumn
{
   internal ResultColumn(int ordinal, string name, ScalarType type, ColumnLineage lineage)
   {
      Ordinal = ordinal;
      Name = name;
      Type = type;
      Lineage = lineage;
   }

   /// <summary>The position of the column's value in each row.</summary>
   public int Ordinal { get; }

   /// <summary>The name the query gave it; any text, so rows are positional rather than keyed by name.</summary>
   public string Name { get; }

   public ScalarType Type { get; }

   public ColumnLineage Lineage { get; }

   public override string ToString() => $"{Name} {Type}";
}

/// <summary>The columns of a query's rows, with their types and lineage.</summary>
public sealed class ResultSchema
{
   internal ResultSchema(IReadOnlyList<ResultColumn> columns, EntityDef? entity)
   {
      Columns = columns;
      Entity = entity;
   }

   public IReadOnlyList<ResultColumn> Columns { get; }

   /// <summary>The entity the rows belong to, when the query only filters, sorts or pages one entity's rows.</summary>
   public EntityDef? Entity { get; }

   /// <summary>The column with this exact name; null when there is none.</summary>
   public ResultColumn? Find(string name) => Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

   public override string ToString() => "[" + string.Join(", ", Columns) + "]";
}
