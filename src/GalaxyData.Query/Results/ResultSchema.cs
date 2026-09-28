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

   /// <summary>
   /// Not asked for by the query: a key value a link or edit target of another column needs. Hidden columns
   /// come after the visible ones.
   /// </summary>
   public bool IsHidden { get; internal init; }

   /// <summary>Where the value leads, if anywhere.</summary>
   public ColumnLink? Link { get; internal init; }

   /// <summary>Where an edit of the value goes, when it can be traced to one table row's column.</summary>
   public EditTarget? EditTarget { get; internal init; }

   public override string ToString() => $"{Name} {Type}";
}

/// <summary>The columns of a query's rows, with their types, lineage, links and edit targets.</summary>
public sealed class ResultSchema
{
   internal ResultSchema(IReadOnlyList<ResultColumn> columns, EntityDef? entity, RowIdentity? rowIdentity = null)
   {
      Columns = columns;
      Entity = entity;
      RowIdentity = rowIdentity;
      VisibleColumns = columns.Where(c => !c.IsHidden).ToList();
   }

   /// <summary>Every column, in row order: the visible ones, then the hidden ones.</summary>
   public IReadOnlyList<ResultColumn> Columns { get; }

   /// <summary>The columns the query asked for, in order; their ordinals are 0 to n - 1.</summary>
   public IReadOnlyList<ResultColumn> VisibleColumns { get; }

   /// <summary>The entity the rows belong to, when the query only filters, sorts or pages one entity's rows.</summary>
   public EntityDef? Entity { get; }

   /// <summary>The key of each row's entity row and the rows that refer to it; null unless <see cref="Entity"/> has a key.</summary>
   public RowIdentity? RowIdentity { get; }

   /// <summary>The visible column with this exact name; null when there is none.</summary>
   public ResultColumn? Find(string name) => VisibleColumns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

   public override string ToString() => "[" + string.Join(", ", VisibleColumns) + "]";
}
