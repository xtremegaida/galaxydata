using System;
using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Dml;

/// <summary>
/// What changes an entity's rows and columns take, as <see cref="DmlPlanner"/> checks them, to show before any are
/// made: which entities take inserts, which take updates and deletes, and which columns a change may give a value.
/// Each answer is null when the change may be made, and otherwise says why not.
/// </summary>
public static class DmlRules
{
   /// <summary>Why rows can't be inserted into the entity: it is defined by a query, or is a view, or its source takes no changes or is read-only.</summary>
   public static string? WhyNoInserts(EntityDef entity)
   {
      ArgumentNullException.ThrowIfNull(entity);
      string name = entity.DisplayName;
      if (entity is not TableEntity table) { return $"{name} is defined by a query: only tables can be changed"; }
      SourceInfo source = table.Source;
      if (table.Kind != EntityKind.Table) { return $"{name} is a view: only tables can be changed"; }
      if (!source.SupportsDml) { return $"{name} can't be changed: {source.Alias} ({source.ProviderKind}) takes no changes"; }
      return source.IsReadOnly ? $"{name} can't be changed: {source.Alias} is read-only" : null;
   }

   /// <summary>
   /// Why the entity's rows can't be changed or deleted: as for inserts, or its key doesn't tell them apart. It has
   /// no primary key of its own (a key the overlay declares serves navigation only, since it needn't be unique), or
   /// a column of it is of a type the language has no values for, so a row can't be found by it.
   /// </summary>
   public static string? WhyNoChanges(EntityDef entity) => WhyNoInserts(entity) ?? KeyProblem(entity);

   /// <summary>Why the entity's key doesn't find its rows; null when it does.</summary>
   internal static string? KeyProblem(EntityDef entity)
   {
      const string Only = "so its rows can't be told apart: they can be inserted, but not changed or deleted";
      if (entity.Key is not { IsDeclared: false } key)
      {
         return $"{entity.DisplayName} has no primary key{(entity.Key != null ? " (the key the overlay declares serves navigation only)" : string.Empty)}, {Only}";
      }
      return key.Columns.FirstOrDefault(c => c.Type.Kind == ScalarKind.Unknown) is { } unknown
         ? $"{entity.DisplayName}'s key has a column of a type the language has no values for ('{unknown.Name}', {unknown.NativeType}), {Only}"
         : null;
   }

   /// <summary>
   /// Why an insert can't give the column a value: the database works it out (computed and row version columns, and
   /// identity columns where the database takes no value for them: SQL Server's), or its type has no values in the
   /// language.
   /// </summary>
   public static string? WhyNotInserted(ColumnDef column, SqlDialect dialect)
   {
      ArgumentNullException.ThrowIfNull(dialect);
      return Generated(column, identityToo: !dialect.AcceptsIdentityValues);
   }

   /// <summary>Why an update can't change the column: it is part of the key, or the database gives its value, or its type has no values in the language.</summary>
   public static string? WhyNotUpdated(ColumnDef column)
   {
      ArgumentNullException.ThrowIfNull(column);
      return column.IsKey ? $"'{column.Name}' is part of the key, which can't change: delete the row and insert it again" : Generated(column, identityToo: true);
   }

   /// <summary>Whether an insert must give the column a value: it can't be null, has no default, and the database doesn't give it one.</summary>
   public static bool NeedsValue(ColumnDef column)
   {
      ArgumentNullException.ThrowIfNull(column);
      return !column.Type.Nullable && !column.HasDefault && !column.IsIdentity && !column.IsComputed && !column.IsRowVersion;
   }

   private static string? Generated(ColumnDef column, bool identityToo)
   {
      ArgumentNullException.ThrowIfNull(column);
      return column.IsComputed ? $"'{column.Name}' is computed: the database works out its value"
         : column.IsRowVersion ? $"'{column.Name}' is a row version: the database sets it"
         : column.IsIdentity && identityToo ? $"'{column.Name}' is an identity column: the database gives its value"
         : column.Type.Kind == ScalarKind.Unknown ? $"'{column.Name}' is of a type the language has no values for ({column.NativeType})"
         : null;
   }
}
