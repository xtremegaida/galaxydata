using System;
using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Web.Catalog;

/// <summary>How an insert takes a column: it needs a value, may have one, or takes none.</summary>
public enum InsertMode
{
   Required,
   Optional,
   Never,
}

/// <summary>What a user may do with an entity's rows; each reason is null when they may.</summary>
public sealed record CapabilitiesDto(bool CanInsert, bool CanUpdate, bool CanDelete)
{
   /// <summary>Why rows can't be inserted.</summary>
   public string? InsertReason { get; init; }

   /// <summary>Why rows can't be changed or deleted.</summary>
   public string? ChangeReason { get; init; }
}

/// <summary>What a user may do with a column; <see cref="Reason"/> is the column's own, when it has one (computed, part of the key).</summary>
public sealed record ColumnCapabilities(bool CanUpdate, InsertMode Insert, string? Reason);

/// <summary>
/// Who may change what: the engine's rules (<see cref="DmlRules"/>: tables with a primary key of their own, in
/// sources that are writable; columns the database doesn't give values) and the user's role, which must be able to
/// edit data. Binary values aren't edited in the application yet, so their columns are read-only too.
/// </summary>
public static class EntityCapabilities
{
   public const string ReadOnlyRole = "Your role reads data; it doesn't change it";

   public static CapabilitiesDto Of(EntityDef entity, SqlDialect? dialect, bool canEditData)
   {
      ArgumentNullException.ThrowIfNull(entity);
      string? noInserts = DmlRules.WhyNoInserts(entity);
      string? noChanges = DmlRules.WhyNoChanges(entity);
      if (noInserts == null && dialect != null)
      {
         foreach (ColumnDef column in entity.Columns.Where(DmlRules.NeedsValue))
         {
            if (WhyNotInserted(column, dialect) is { } why)
            {
               noInserts = $"{why}, and every new row needs a value for it";
               break;
            }
         }
      }
      if (!canEditData)
      {
         noInserts ??= ReadOnlyRole;
         noChanges ??= ReadOnlyRole;
      }
      return new CapabilitiesDto(noInserts == null, noChanges == null, noChanges == null) { InsertReason = noInserts, ChangeReason = noChanges };
   }

   public static ColumnCapabilities Of(ColumnDef column, SqlDialect? dialect, CapabilitiesDto entity)
   {
      ArgumentNullException.ThrowIfNull(column);
      ArgumentNullException.ThrowIfNull(entity);
      string? notUpdated = Binary(column) ?? DmlRules.WhyNotUpdated(column);
      string? notInserted = dialect == null ? null : WhyNotInserted(column, dialect);
      InsertMode insert = !entity.CanInsert || dialect == null || notInserted != null ? InsertMode.Never
         : DmlRules.NeedsValue(column) ? InsertMode.Required
         : InsertMode.Optional;
      return new ColumnCapabilities(entity.CanUpdate && notUpdated == null, insert, notUpdated ?? notInserted);
   }

   private static string? WhyNotInserted(ColumnDef column, SqlDialect dialect) => Binary(column) ?? DmlRules.WhyNotInserted(column, dialect);

   private static string? Binary(ColumnDef column) =>
      column.Type.Kind == ScalarKind.Binary ? $"'{column.Name}' holds binary values, which can't be edited here" : null;
}
