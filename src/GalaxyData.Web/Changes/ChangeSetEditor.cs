using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Metadata;

namespace GalaxyData.Web.Changes;

/// <summary>
/// Applies operations to a user's pending changes, checking each against the catalog as it stands: the entity takes
/// the change (a table, writable, with a key of its own for changes to rows that are there), the columns may be
/// given values, and the values are of their columns' types. What is wrong goes in the errors, by the operation's
/// field (<c>ops[2].values.total</c>), and then nothing is applied. The changes merge:
/// <list type="bullet">
/// <item>a row has one change: new values for its columns, or its deletion;</item>
/// <item>the value a column had when it was first changed is kept as its original, however often it is changed again;</item>
/// <item>a column set back to its original is no change any more, and a row with none isn't either;</item>
/// <item>deleting a row drops its new values (keeping the originals); deleting a new row drops it;</item>
/// <item>what is shown for the row a navigation leads to goes with the values of the navigation's columns.</item>
/// </list>
/// </summary>
internal sealed class ChangeSetEditor(QueryCatalog catalog, SourceProviders providers, List<ChangeState> changes, Dictionary<string, string[]> errors)
{
   /// <summary>A value of a column, and the name the operation gave the column (to name it so in errors).</summary>
   private readonly record struct Given(string Name, JsonElement Value);

   private string prefix = string.Empty;

   public IReadOnlyList<ChangeState> Changes => changes;

   public void Apply(int index, ChangeOpDto op)
   {
      ArgumentNullException.ThrowIfNull(op);
      prefix = $"ops[{index.ToString(CultureInfo.InvariantCulture)}].";
      if (op.Op is not { } kind)
      {
         Error("op", "Say what to do: set, insert, delete or revert");
         return;
      }
      if (op.Change != null && kind != ChangeOpKind.Revert)
      {
         Error("change", "Only a revert names a change by its id: name the row by its key, or a new row by its tempId");
         return;
      }
      if (kind == ChangeOpKind.Revert && op.Change is { } id)
      {
         if (op.Entity != null || op.Key != null || op.TempId != null)
         {
            Error("change", "Name the change by its id, or by its row: not both");
            return;
         }
         if (changes.FirstOrDefault(c => !c.Removed && c.Row?.Id == id) is { } change) { Revert(change, op.Columns, null); }
         return;
      }

      EntityDef? entity = ChangeRows.Entity(catalog, op.Entity, out string? problem);
      if (entity == null)
      {
         Error("entity", problem!);
         return;
      }
      if (op.Key != null && op.TempId != null)
      {
         Error("key", "Name the row by its key, or a new row by its tempId: not both");
         return;
      }
      if (op.Key == null && op.TempId == null)
      {
         Error(kind == ChangeOpKind.Insert ? "tempId" : "key", kind == ChangeOpKind.Insert
            ? "Name the new row (tempId), to change or drop it later"
            : "Name the row by its key, or a new row by its tempId");
         return;
      }
      SqlDialect? dialect = entity is TableEntity table ? providers.For(table.Source.ProviderKind).Dialect : null;
      CapabilitiesDto can = EntityCapabilities.Of(entity, dialect, canEditData: true);
      switch (kind)
      {
         case ChangeOpKind.Insert:
            Insert(entity, dialect, can, op);
            break;
         case ChangeOpKind.Set when op.TempId != null:
            SetNew(entity, dialect, can, op);
            break;
         case ChangeOpKind.Set:
            Set(entity, dialect, can, op);
            break;
         case ChangeOpKind.Delete when op.TempId != null:
            if (NewRow(entity, op.TempId) is { } added)
            {
               added.Removed = true;
            }
            break;
         case ChangeOpKind.Delete:
            Delete(entity, can, op);
            break;
         case ChangeOpKind.Revert:
            ChangeState? found = op.TempId != null ? NewRow(entity, op.TempId) : Key(entity, op.Key!, out string? rowKey) ? Find(entity, rowKey!) : null;
            if (found != null) { Revert(found, op.Columns, entity); }
            break;
      }
   }

   private void Insert(EntityDef entity, SqlDialect? dialect, CapabilitiesDto can, ChangeOpDto op)
   {
      if (op.Key != null)
      {
         Error("key", "A new row is named by its tempId: its key, if it is given one, is among its values");
         return;
      }
      if (!can.CanInsert)
      {
         Error("entity", can.InsertReason!);
         return;
      }
      string tempId = op.TempId!.Trim();
      if (tempId.Length == 0)
      {
         Error("tempId", "Name the new row (tempId), to change or drop it later");
         return;
      }
      if (changes.Any(c => !c.Removed && c.TempId == tempId))
      {
         Error("tempId", $"A new row is named {tempId} already");
         return;
      }
      int before = errors.Count;
      Dictionary<ColumnDef, Given> values = Values(entity, dialect, can, op.Values, insert: true);
      Dictionary<string, JsonElement> display = Display(entity, op.Display);
      if (errors.Count > before) { return; }
      ChangeState change = Create(PendingChangeKind.Insert, entity, null, tempId);
      foreach ((ColumnDef column, Given value) in values) { change.Values[column.Name] = value.Value; }
      Show(change, display, entity);
   }

   private void SetNew(EntityDef entity, SqlDialect? dialect, CapabilitiesDto can, ChangeOpDto op)
   {
      ChangeState? change = NewRow(entity, op.TempId!);
      if (change == null)
      {
         Error("tempId", $"There is no new row {op.TempId} of {entity.DisplayName}");
         return;
      }
      if (!can.CanInsert)
      {
         Error("entity", can.InsertReason!);
         return;
      }
      int before = errors.Count;
      Dictionary<ColumnDef, Given> values = Values(entity, dialect, can, op.Values, insert: true);
      Dictionary<string, JsonElement> display = Display(entity, op.Display);
      if (values.Count == 0 && display.Count == 0) { Error("values", "Give a value for at least one column"); }
      if (errors.Count > before) { return; }
      foreach ((ColumnDef column, Given value) in values) { change.Values[column.Name] = value.Value; }
      Show(change, display, entity);
   }

   private void Set(EntityDef entity, SqlDialect? dialect, CapabilitiesDto can, ChangeOpDto op)
   {
      if (!can.CanUpdate)
      {
         Error("entity", can.ChangeReason!);
         return;
      }
      if (!Key(entity, op.Key!, out string? rowKey)) { return; }
      ChangeState? change = Find(entity, rowKey!);
      if (change?.Kind == PendingChangeKind.Delete)
      {
         Error("key", "The row is to be deleted: revert that to change it");
         return;
      }
      int before = errors.Count;
      Dictionary<ColumnDef, Given> values = Values(entity, dialect, can, op.Values, insert: false);
      if (values.Count == 0 && errors.Count == before) { Error("values", "Give a value for at least one column"); }
      Dictionary<ColumnDef, Given> original = Originals(entity, op.Original);
      // Needed when a column is first changed: later, the one kept is.
      foreach ((ColumnDef column, Given value) in values)
      {
         if (!original.ContainsKey(column) && change?.Original.ContainsKey(column.Name) != true)
         {
            Error($"original.{value.Name}", $"Give the value '{column.Name}' had when the row was read, to check it still has it when the change is committed");
         }
      }
      Dictionary<string, JsonElement> display = Display(entity, op.Display);
      if (errors.Count > before) { return; }

      change ??= Create(PendingChangeKind.Update, entity, rowKey, null);
      foreach ((ColumnDef column, Given given) in values)
      {
         JsonElement value = given.Value;
         // The value it had when it was first changed.
         if (!change.Original.TryGetValue(column.Name, out JsonElement was))
         {
            was = original[column].Value;
            change.Original[column.Name] = was;
         }
         if (ChangeRows.Same(column, value, was))
         {
            change.Values.Remove(column.Name);
            change.Original.Remove(column.Name);
         }
         else
         {
            change.Values[column.Name] = value;
         }
      }
      Show(change, display, entity);
      if (change.Values.Count == 0) { change.Removed = true; }
   }

   private void Delete(EntityDef entity, CapabilitiesDto can, ChangeOpDto op)
   {
      if (!can.CanDelete)
      {
         Error("entity", can.ChangeReason!);
         return;
      }
      if (op.Values != null)
      {
         Error("values", "A row to delete takes no values: give the values it had (original)");
         return;
      }
      if (!Key(entity, op.Key!, out string? rowKey)) { return; }
      int before = errors.Count;
      Dictionary<ColumnDef, Given> original = Originals(entity, op.Original);
      if (original.Count == 0 && errors.Count == before)
      {
         Error("original", "Give the values the row had when it was read, to check it still has them when it is deleted");
      }
      if (errors.Count > before) { return; }
      ChangeState change = Find(entity, rowKey!) ?? Create(PendingChangeKind.Delete, entity, rowKey, null);
      change.Kind = PendingChangeKind.Delete;
      change.Values.Clear();
      change.Display.Clear();
      // The values the row had when first changed stay.
      foreach ((ColumnDef column, Given value) in original) { change.Original.TryAdd(column.Name, value.Value); }
   }

   /// <summary>Drops a change, or only its values for some columns; with no value left, a change to a row is dropped (a new row stays).</summary>
   private void Revert(ChangeState change, List<string>? columns, EntityDef? entity)
   {
      if (columns == null)
      {
         change.Removed = true;
         return;
      }
      if (change.Kind == PendingChangeKind.Delete)
      {
         Error("columns", "A row to delete is kept whole: revert the change without columns");
         return;
      }
      for (int i = 0; i < columns.Count; i++)
      {
         string? name = columns[i];
         if (name == null)
         {
            Error($"columns[{i.ToString(CultureInfo.InvariantCulture)}]", "A column's name can't be null");
            return;
         }
         // As kept, or ignoring case when that is one; a column without a value has none to revert.
         string? kept = change.Values.ContainsKey(name) ? name : change.Values.Keys.Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList() is [string one] ? one : null;
         if (kept == null) { continue; }
         change.Values.Remove(kept);
         change.Original.Remove(kept);
      }
      Show(change, [], entity ?? catalog.FindEntity(change.Entity));
      if (change.Kind == PendingChangeKind.Update && change.Values.Count == 0) { change.Removed = true; }
   }

   /// <summary>The change made for a row, or one that was dropped (whose stored row it takes again).</summary>
   private ChangeState Create(PendingChangeKind kind, EntityDef entity, string? rowKey, string? tempId)
   {
      string source = entity is TableEntity table ? table.Source.Alias : string.Empty;
      ChangeState? dropped = changes.FirstOrDefault(c => c.Removed && c.Row != null && c.Entity == entity.DisplayName &&
         (rowKey != null ? c.RowKey == rowKey : c.TempId == tempId));
      if (dropped != null)
      {
         dropped.Reset(kind);
         return dropped;
      }
      ChangeState change = new() { Kind = kind, Entity = entity.DisplayName, Source = source, RowKey = rowKey, TempId = tempId };
      changes.Add(change);
      return change;
   }

   private ChangeState? Find(EntityDef entity, string rowKey) =>
      changes.FirstOrDefault(c => !c.Removed && c.RowKey == rowKey && c.Entity == entity.DisplayName);

   private ChangeState? NewRow(EntityDef entity, string tempId)
   {
      ChangeState? change = changes.FirstOrDefault(c => !c.Removed && c.TempId == tempId.Trim());
      if (change != null && change.Entity != entity.DisplayName)
      {
         Error("tempId", $"The new row {tempId} is of {change.Entity}, not {entity.DisplayName}");
         return null;
      }
      return change;
   }

   /// <summary>A row's key as it is kept (its values in key order, as rows' ids are); false, with errors, when it isn't one of the entity's.</summary>
   private bool Key(EntityDef entity, List<object?> given, out string? rowKey)
   {
      rowKey = null;
      if (entity.Key is not { } key)
      {
         Error("key", $"{entity.DisplayName} has no key, so its rows can't be told apart");
         return false;
      }
      if (given.Count != key.Columns.Count)
      {
         Error("key", $"The key of {entity.DisplayName} is {string.Join(", ", key.Columns.Select(c => c.Name))}: give a value of each, in that order");
         return false;
      }
      List<(object? Value, ScalarType Type)> values = [];
      for (int i = 0; i < given.Count; i++)
      {
         ColumnDef column = key.Columns[i];
         string field = $"key[{i.ToString(CultureInfo.InvariantCulture)}]";
         if (!ChangeRows.TryRead(column, given[i], out object? value, out _, out string? problem))
         {
            Error(field, problem!);
            return false;
         }
         if (value == null)
         {
            Error(field, $"A key has no nulls: '{column.Name}' needs a value");
            return false;
         }
         values.Add((value, column.Type));
      }
      rowKey = ValueCodec.RowId(values);
      return true;
   }

   /// <summary>New values by column, kept as they are sent; each column must take a value (in a new row, or an update), and each value be of its type.</summary>
   private Dictionary<ColumnDef, Given> Values(EntityDef entity, SqlDialect? dialect, CapabilitiesDto can, Dictionary<string, object?>? given, bool insert)
   {
      Dictionary<ColumnDef, Given> values = [];
      foreach ((string name, object? raw) in given ?? [])
      {
         string field = "values." + name;
         if (ChangeRows.Column(entity, name, out string? problem) is not { } column)
         {
            Error(field, problem!);
            continue;
         }
         ColumnCapabilities allowed = EntityCapabilities.Of(column, dialect, can);
         if (insert ? allowed.Insert == InsertMode.Never : !allowed.CanUpdate)
         {
            Error(field, allowed.Reason ?? (insert ? $"'{column.Name}' takes no value in a new row" : $"'{column.Name}' can't be changed"));
            continue;
         }
         if (values.ContainsKey(column))
         {
            Error(field, $"'{column.Name}' is given twice");
            continue;
         }
         if (!ChangeRows.TryRead(column, raw, out object? value, out JsonElement kept, out problem))
         {
            Error(field, problem!);
            continue;
         }
         if (value == null && !ChangeRows.IsNullable(column))
         {
            Error(field, $"'{column.Name}' can't be null");
            continue;
         }
         values[column] = new Given(name, kept);
      }
      return values;
   }

   /// <summary>The values a row had when it was read, by column, kept as they are sent.</summary>
   private Dictionary<ColumnDef, Given> Originals(EntityDef entity, Dictionary<string, object?>? given)
   {
      Dictionary<ColumnDef, Given> original = [];
      foreach ((string name, object? raw) in given ?? [])
      {
         string field = "original." + name;
         if (ChangeRows.Column(entity, name, out string? problem) is not { } column)
         {
            Error(field, problem!);
            continue;
         }
         if (original.ContainsKey(column))
         {
            Error(field, $"'{column.Name}' is given twice");
            continue;
         }
         if (!ChangeRows.TryRead(column, raw, out _, out JsonElement kept, out problem))
         {
            Error(field, problem!);
            continue;
         }
         original[column] = new Given(name, kept);
      }
      return original;
   }

   /// <summary>What to show for the rows navigations lead to, by navigation (as the catalog names it).</summary>
   private Dictionary<string, JsonElement> Display(EntityDef entity, Dictionary<string, object?>? given)
   {
      Dictionary<string, JsonElement> display = new(StringComparer.Ordinal);
      foreach ((string name, object? raw) in given ?? [])
      {
         string field = "display." + name;
         NameMatch<NavigationDef> match = entity.FindNavigation(name);
         if (match.Item is not { } navigation)
         {
            Error(field, match.Status == MatchStatus.Ambiguous
               ? $"'{name}' could be {string.Join(" or ", match.Candidates.Select(c => "'" + c.Name + "'"))}: give the name as it is spelled"
               : $"{entity.DisplayName} has no navigation '{name}'");
            continue;
         }
         if (navigation.IsInverse)
         {
            Error(field, $"'{navigation.Name}' leads to the rows that refer to this one: only the row a reference leads to is shown");
            continue;
         }
         JsonElement value = ValueCodec.Json(raw);
         if (!ChangeRows.IsDisplayValue(value) || value.GetRawText().Length > ChangeLimits.MaxDisplayLength)
         {
            Error(field, $"What is shown for a row is text, a number, true, false or null, of at most {ChangeLimits.MaxDisplayLength} characters");
            continue;
         }
         display[navigation.Name] = value;
      }
      return display;
   }

   /// <summary>Adds what to show, and keeps only what goes with values of the navigations' columns.</summary>
   private static void Show(ChangeState change, Dictionary<string, JsonElement> display, EntityDef? entity)
   {
      foreach ((string navigation, JsonElement value) in display) { change.Display[navigation] = value; }
      foreach (string navigation in change.Display.Keys.ToList())
      {
         IReadOnlyList<ColumnDef>? columns = entity?.FindNavigation(navigation).Item?.OwnerColumns;
         bool kept = columns == null ? change.Values.Count > 0 : columns.Any(c => change.Values.ContainsKey(c.Name));
         if (!kept) { change.Display.Remove(navigation); }
      }
   }

   private void Error(string field, string message)
   {
      string key = prefix + field;
      errors[key] = errors.TryGetValue(key, out string[]? others) ? [.. others, message] : [message];
   }
}
