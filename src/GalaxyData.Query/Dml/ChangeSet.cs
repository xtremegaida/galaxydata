using System;
using System.Collections.Generic;
using GalaxyData.Query.Catalog;

namespace GalaxyData.Query.Dml;

/// <summary>
/// Changes to rows of tables, in any sources, to be written together: <see cref="DmlPlanner"/> makes the statements
/// each source runs, and <see cref="Execution.QueryEngine.CommitAsync(DmlPlan, System.Threading.CancellationToken)"/>
/// runs them in one transaction on each connection.
/// </summary>
public sealed class ChangeSet
{
   private readonly List<RowChange> changes = [];

   public ChangeSet() { }

   public ChangeSet(IEnumerable<RowChange> changes)
   {
      ArgumentNullException.ThrowIfNull(changes);
      foreach (RowChange change in changes) { Add(change); }
   }

   /// <summary>The changes in the order they were added; issues name them by their index here.</summary>
   public IReadOnlyList<RowChange> Changes => changes;

   public ChangeSet Add(RowChange change)
   {
      ArgumentNullException.ThrowIfNull(change);
      changes.Add(change);
      return this;
   }
}

/// <summary>
/// A change to one row of <see cref="Entity"/>. Values are keyed by column name, and are CLR values of the column's
/// type or values that convert to it as a database's would read (<c>"2026-01-05"</c> for a date, <c>"12.50"</c> for a
/// decimal); a value that doesn't convert, or doesn't fit the column, is an issue of the plan.
/// </summary>
public abstract record RowChange(EntityDef Entity);

/// <summary>A new row. Columns without a value get their default (identity, computed and row version columns get theirs).</summary>
public sealed record InsertRow(EntityDef Entity, IReadOnlyDictionary<string, object?> Values) : RowChange(Entity);

/// <summary>
/// New values for some columns of the row whose key is <see cref="Key"/>. Key columns can't change. The row is only
/// changed if the columns in <see cref="Original"/> still have those values (when they compare exactly in the
/// database), or its row version still has the one given; otherwise nothing is written anywhere.
/// </summary>
public sealed record UpdateRow(EntityDef Entity, IReadOnlyDictionary<string, object?> Key, IReadOnlyDictionary<string, object?> Values) : RowChange(Entity)
{
   /// <summary>The values columns had when the row was read (the changed ones, at least); null to change the row whatever it has.</summary>
   public IReadOnlyDictionary<string, object?>? Original { get; init; }
}

/// <summary>Removes the row whose key is <see cref="Key"/>, if its columns in <see cref="Original"/> still have those values.</summary>
public sealed record DeleteRow(EntityDef Entity, IReadOnlyDictionary<string, object?> Key) : RowChange(Entity)
{
   /// <summary>The values columns had when the row was read; null to delete the row whatever it has.</summary>
   public IReadOnlyDictionary<string, object?>? Original { get; init; }
}
