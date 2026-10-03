using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Dml;

/// <summary>
/// Plans a <see cref="ChangeSet"/>: checks each change against its table, and writes the statements each source runs.
/// <list type="bullet">
/// <item>Inserts give back the inserted row (identity and defaults included); a value for an identity column is
/// passed on where the database takes one. SQL Server tables with triggers, which <c>OUTPUT</c> doesn't take, read
/// the row back by its key.</item>
/// <item>Updates set only the columns given, and neither can change a key. Updates and deletes find their row by
/// its key, and change it only if the original values given still hold: its row version when it has one and it is
/// given, otherwise every column given whose values compare exactly in the database (not floating-point, binary,
/// JSON or unknown types, nor SQL Server's <c>text</c>; in SQLite only whole numbers and text), and nulls.</item>
/// <item>Each statement must change one row. Each source's statements run in order: inserts with the rows they
/// refer to first, then updates, then deletes with the rows that refer to them first.</item>
/// </list>
/// </summary>
public static class DmlPlanner
{
   private static readonly IReadOnlyDictionary<string, object?> None = new Dictionary<string, object?>();

   /// <summary>Stands for an original value of a column of unknown type, which isn't compared.</summary>
   private static readonly object Unknown = new();

   /// <summary>Plans the changes; <paramref name="dialects"/> gives each source's dialect, or null when no provider serves it.</summary>
   public static DmlPlan Plan(ChangeSet changes, Func<SourceInfo, SqlDialect?> dialects)
   {
      ArgumentNullException.ThrowIfNull(changes);
      ArgumentNullException.ThrowIfNull(dialects);
      List<DmlIssue> issues = [];
      List<PlannedChange> planned = [];
      for (int i = 0; i < changes.Changes.Count; i++)
      {
         if (Check(i, changes.Changes[i], dialects, issues) is { } change) { planned.Add(change); }
      }
      List<DmlScript> scripts = [];
      foreach (IGrouping<string, PlannedChange> source in planned.GroupBy(c => c.Table.Source.Alias, StringComparer.Ordinal))
      {
         PlannedChange first = source.First();
         List<DmlStatement> statements = Order(source.ToList()).Select(Statement).ToList();
         scripts.Add(new DmlScript(first.Table.Source, first.Dialect, statements, isEdited: false));
      }
      return new DmlPlan(scripts, issues);
   }

   #region Checking

   private sealed record PlannedChange(int Index, RowChange Change, TableEntity Table, SqlDialect Dialect)
   {
      public IReadOnlyList<(ColumnDef Column, object? Value)> Key { get; init; } = [];

      public IReadOnlyList<(ColumnDef Column, object? Value)> Values { get; init; } = [];

      public IReadOnlyList<(ColumnDef Column, object? Value)> Original { get; init; } = [];
   }

   private static PlannedChange? Check(int index, RowChange change, Func<SourceInfo, SqlDialect?> dialects, List<DmlIssue> issues)
   {
      ArgumentNullException.ThrowIfNull(change);
      int before = issues.Count;
      void Issue(string message, ColumnDef? column = null) => issues.Add(new DmlIssue(index, change, message) { Column = column?.Name });

      EntityDef entity = change.Entity ?? throw new ArgumentException($"Change {index} has no entity", nameof(change));
      if (DmlRules.WhyNoInserts(entity) is { } why) { Issue(why); }
      if (entity is not TableEntity table) { return null; }
      SourceInfo source = table.Source;
      SqlDialect? dialect = dialects(source);
      if (dialect == null) { Issue($"{source.Alias} is a {source.ProviderKind} source, and no provider for those is registered"); }
      if (issues.Count > before) { return null; }

      PlannedChange planned = new(index, change, table, dialect!);
      switch (change)
      {
         case InsertRow insert:
            List<(ColumnDef Column, object? Value)> values = Values(table, dialect!, insert.Values ?? None, original: false, Issue);
            foreach ((ColumnDef column, _) in values)
            {
               if (DmlRules.WhyNotInserted(column, dialect!) is { } generated) { Issue(generated, column); }
            }
            // A column named, whether or not its value converts, isn't missing.
            HashSet<ColumnDef> named = (insert.Values ?? None).Keys.Select(k => table.FindColumn(k).Item).OfType<ColumnDef>().ToHashSet();
            foreach (ColumnDef column in table.Columns)
            {
               if (!named.Contains(column) && DmlRules.NeedsValue(column))
               {
                  Issue($"'{column.Name}' needs a value: it can't be null, and has no default", column);
               }
            }
            planned = planned with { Values = values };
            break;
         case UpdateRow update:
            IReadOnlyList<(ColumnDef Column, object? Value)> key = Key(table, dialect!, update.Key, Issue);
            List<(ColumnDef Column, object? Value)> changed = Values(table, dialect!, update.Values ?? None, original: false, Issue);
            if ((update.Values ?? None).Count == 0) { Issue("The update changes no column"); }
            foreach ((ColumnDef column, _) in changed)
            {
               if (DmlRules.WhyNotUpdated(column) is { } fixedValue) { Issue(fixedValue, column); }
            }
            planned = planned with { Key = key, Values = changed, Original = Values(table, dialect!, update.Original ?? None, original: true, Issue) };
            break;
         case DeleteRow delete:
            planned = planned with
            {
               Key = Key(table, dialect!, delete.Key, Issue),
               Original = Values(table, dialect!, delete.Original ?? None, original: true, Issue),
            };
            break;
         default:
            throw new ArgumentException($"Change {index} is a {change.GetType().Name}, which isn't a change the planner knows", nameof(change));
      }
      return issues.Count > before ? null : planned;
   }

   /// <summary>
   /// The key values of the row a change is for. Only a table's own primary key tells its rows apart: a key the
   /// overlay declares serves navigation, and needn't be unique.
   /// </summary>
   private static IReadOnlyList<(ColumnDef Column, object? Value)> Key(TableEntity table, SqlDialect dialect, IReadOnlyDictionary<string, object?>? given,
                                                                       Action<string, ColumnDef?> issue)
   {
      if (DmlRules.KeyProblem(table) is { } problem)
      {
         issue(problem, null);
         return [];
      }
      // There is no problem only with a key of its own.
      KeyDef own = table.Key!;
      List<(ColumnDef Column, object? Value)> key = Values(table, dialect, given ?? None, original: true, issue);
      foreach ((ColumnDef column, object? value) in key)
      {
         if (!column.IsKey) { issue($"'{column.Name}' isn't part of the key of {table.DisplayName}", column); }
         else if (value == null) { issue($"The key value of '{column.Name}' is null: no row has it", column); }
      }
      foreach (ColumnDef column in own.Columns)
      {
         if (!key.Any(k => k.Column == column)) { issue($"The key needs a value for '{column.Name}'", column); }
      }
      return key.OrderBy(k => own.Columns.ToList().IndexOf(k.Column)).ToList();
   }

   /// <summary>The values by column, converted to the columns' types, in column order; what doesn't resolve or convert is an issue.</summary>
   private static List<(ColumnDef Column, object? Value)> Values(TableEntity table, SqlDialect dialect, IReadOnlyDictionary<string, object?> given, bool original,
                                                                 Action<string, ColumnDef?> issue)
   {
      List<(ColumnDef Column, object? Value)> values = [];
      foreach ((string name, object? value) in given)
      {
         NameMatch<ColumnDef> match = table.FindColumn(name);
         if (!match.IsFound)
         {
            issue(match.Status == MatchStatus.Ambiguous
               ? $"'{name}' could be {string.Join(" or ", match.Candidates.Select(c => "'" + c.Name + "'"))} of {table.DisplayName}: give the name as it is spelled"
               : $"{table.DisplayName} has no column '{name}'", null);
            continue;
         }
         ColumnDef column = match.Item!;
         if (values.Any(v => v.Column == column))
         {
            issue($"'{column.Name}' is given twice", column);
            continue;
         }
         if (original && column.Type.Kind == ScalarKind.Unknown)
         {
            // A value of no type the language has can only be compared with null.
            values.Add((column, value == null ? null : Unknown));
            continue;
         }
         if (!original && value == null && !column.Type.Nullable)
         {
            issue($"'{column.Name}' can't be null", column);
            continue;
         }
         if (!DmlValues.TryConvert(column, value, dialect, out object? converted, out string? problem))
         {
            issue(problem!, column);
            continue;
         }
         values.Add((column, converted));
      }
      values.Sort((a, b) => a.Column.Ordinal.CompareTo(b.Column.Ordinal));
      return values;
   }

   #endregion

   #region Order

   /// <summary>
   /// Inserts with the tables they refer to first, then updates, then deletes with the tables that refer to theirs
   /// first; otherwise in the order given. Tables that refer to each other keep the order given.
   /// </summary>
   private static IEnumerable<PlannedChange> Order(List<PlannedChange> changes)
   {
      List<TableEntity> tables = changes.Select(c => c.Table).Distinct().ToList();
      Dictionary<TableEntity, int> rank = [];
      List<TableEntity> left = [.. tables];
      while (left.Count > 0)
      {
         TableEntity next = left.FirstOrDefault(t => !Parents(t).Any(left.Contains)) ?? left[0];
         rank[next] = rank.Count;
         left.Remove(next);
      }
      IEnumerable<PlannedChange> Of<T>() where T : RowChange => changes.Where(c => c.Change is T);
      return Of<InsertRow>().OrderBy(c => rank[c.Table])
         .Concat(Of<UpdateRow>())
         .Concat(Of<DeleteRow>().OrderByDescending(c => rank[c.Table]));
   }

   /// <summary>The other tables of the same source a table's foreign keys refer to.</summary>
   private static IEnumerable<TableEntity> Parents(TableEntity table) =>
      table.Navigations.Where(n => !n.IsInverse && n.Relation.To is TableEntity parent && parent != table && parent.Source == table.Source)
         .Select(n => (TableEntity)n.Relation.To);

   #endregion

   #region Statements

   private static DmlStatement Statement(PlannedChange change)
   {
      TableEntity table = change.Table;
      SqlDialect dialect = change.Dialect;
      SqlTable target = Target(table);
      SqlDml dml;
      SqlSelect? then = null;
      DmlRowCount counting = DmlRowCount.Affected;
      IReadOnlyList<ColumnDef> returned = [];
      DmlStatementKind kind;
      switch (change.Change)
      {
         case InsertRow:
         {
            kind = DmlStatementKind.Insert;
            List<SqlExpr> values = change.Values.Select(v => Value(v.Column, v.Value)).ToList();
            List<SqlExpr> row = table.Columns.Select(Read).ToList();
            returned = table.Columns;
            counting = DmlRowCount.Rows;
            if (dialect.Returning == ReturningStyle.Output && table.HasTriggers)
            {
               // SQL Server takes no OUTPUT (without INTO) on a table with triggers: the row is read back by its key.
               dml = new SqlInsert(target, change.Values.Select(v => Column(v.Column)).ToList(), values);
               then = ReadBack(table, dialect, change.Values, values, row);
               if (then == null)
               {
                  then = CountOf(dialect);
                  counting = DmlRowCount.Selected;
                  returned = [];
               }
            }
            else
            {
               dml = new SqlInsert(target, change.Values.Select(v => Column(v.Column)).ToList(), values) { Returning = row };
            }
            break;
         }
         case UpdateRow:
            kind = DmlStatementKind.Update;
            dml = new SqlUpdate(target, change.Values.Select(v => new SqlAssignment(Column(v.Column), Value(v.Column, v.Value))).ToList(), Where(change));
            break;
         default:
            kind = DmlStatementKind.Delete;
            dml = new SqlDelete(target, Where(change));
            break;
      }
      if (kind != DmlStatementKind.Insert && dialect.RowCountOfChange != null)
      {
         then = CountOf(dialect);
         counting = DmlRowCount.Selected;
      }
      string sql = SqlWriter.Write(dml, then, dialect, returning: true, inline: false, out IReadOnlyList<SqlParameterSlot> parameters);
      string display = SqlWriter.Write(dml, null, dialect, returning: false, inline: true, out _);
      string parameterized = SqlWriter.Write(dml, null, dialect, returning: false, inline: false, out IReadOnlyList<SqlParameterSlot> shown);
      return new DmlStatement(kind, sql, parameters, display, DmlStatement.WithParameters(parameterized, shown))
      {
         Description = Describe(change),
         Change = change.Change,
         ChangeIndex = change.Index,
         ExpectedRows = 1,
         ReturnedColumns = returned,
         Counting = counting,
      };
   }

   /// <summary><c>the update of shop.orders (id = 1001)</c>.</summary>
   private static string Describe(PlannedChange change)
   {
      string key = change.Key.Count == 0 ? string.Empty
         : " (" + string.Join(", ", change.Key.Select(k => $"{k.Column.Name} = {BoundTreePrinter.Literal(k.Value)}")) + ")";
      return change.Change switch
      {
         InsertRow => $"the insert into {change.Table.DisplayName}",
         UpdateRow => $"the update of {change.Table.DisplayName}{key}",
         _ => $"the delete from {change.Table.DisplayName}{key}",
      };
   }

   /// <summary>The table, named as queries name it: without the source's default schema.</summary>
   private static SqlTable Target(TableEntity table)
   {
      string? schema = table.Source.Catalog == null && (string.IsNullOrEmpty(table.Schema) || string.Equals(table.Schema, table.Source.DefaultSchema, StringComparison.Ordinal))
         ? null
         : table.Schema;
      return new SqlTable(schema, table.Table, table.Table) { Catalog = table.Source.Catalog };
   }

   private static SqlColumn Column(ColumnDef column) => new(null, column.Name) { NativeType = column.NativeType };

   /// <summary>A column as the source's queries read it (a PostgreSQL enum as text).</summary>
   private static SqlExpr Read(ColumnDef column) => column.ReadAs is { } type ? new SqlCast(Column(column), type) : Column(column);

   private static SqlExpr Value(ColumnDef column, object? value) => value == null
      ? new SqlLiteral(null, column.Type)
      : new SqlParameterRef(new SqlParameterSlot(string.Empty, column.Type.AsNonNullable(), null, value, null, null));

   /// <summary>The row's key, then the original values that still have to hold: its row version alone when that is given.</summary>
   private static SqlExpr Where(PlannedChange change)
   {
      SqlDialect dialect = change.Dialect;
      List<SqlExpr> conditions = change.Key.Select(k => dialect.KeyEquals(Column(k.Column), Value(k.Column, k.Value), k.Column.Type)).ToList();
      IEnumerable<(ColumnDef Column, object? Value)> original = change.Original.Where(o => !o.Column.IsKey);
      if (original.FirstOrDefault(o => o.Column.IsRowVersion && o.Value != null) is { Column: not null } version) { original = [version]; }
      foreach ((ColumnDef column, object? value) in original)
      {
         if (value == null) { conditions.Add(new SqlIsNull(Column(column), negated: false)); }
         else if (value != Unknown && (column.IsRowVersion || dialect.ComparesOriginal(column.Type, column.NativeType)))
         {
            conditions.Add(new SqlBinary(SqlBinaryOp.Equal, Column(column), Value(column, value)));
         }
      }
      return conditions.Aggregate((a, b) => new SqlBinary(SqlBinaryOp.And, a, b));
   }

   /// <summary>
   /// The inserted row, selected by its key if the insert changed a row: by the identity the insert gave, and the key
   /// values it was given. Null when part of the key is neither (a default makes it).
   /// </summary>
   private static SqlSelect? ReadBack(TableEntity table, SqlDialect dialect, IReadOnlyList<(ColumnDef Column, object? Value)> given,
                                      IReadOnlyList<SqlExpr> values, IReadOnlyList<SqlExpr> row)
   {
      if (table.Key is not { IsDeclared: false }) { return null; }
      List<SqlExpr> conditions = [new SqlBinary(SqlBinaryOp.Equal, dialect.RowCountOfChange!, new SqlLiteral(1L, ScalarType.Int64))];
      foreach (ColumnDef column in table.Key.Columns)
      {
         int at = given.ToList().FindIndex(v => v.Column == column);
         if (at >= 0 && given[at].Value != null) { conditions.Add(new SqlBinary(SqlBinaryOp.Equal, Column(column), values[at])); }
         else if (column.IsIdentity && dialect.InsertedIdentity is { } identity) { conditions.Add(new SqlBinary(SqlBinaryOp.Equal, Column(column), identity)); }
         else { return null; }
      }
      SqlSelect select = new() { From = Target(table), Where = conditions.Aggregate((a, b) => new SqlBinary(SqlBinaryOp.And, a, b)) };
      select.Items.AddRange(row.Select(r => new SqlSelectItem(r, null)));
      return select;
   }

   /// <summary>The query that reads how many rows the last statement changed, where the database's own count isn't that; null where it is.</summary>
   internal static string? RowCountQuery(SqlDialect dialect) => dialect.RowCountOfChange == null ? null : SqlWriter.Write(CountOf(dialect), dialect);

   private static SqlSelect CountOf(SqlDialect dialect)
   {
      SqlSelect select = new();
      select.Items.Add(new SqlSelectItem(dialect.RowCountOfChange!, null));
      return select;
   }

   #endregion
}

/// <summary>Values of changes, converted to their column's type, or why they can't be.</summary>
internal static class DmlValues
{
   private const int Shown = 40;

   /// <summary>
   /// The value as the column's CLR type, converted as a database's value would be read, except where that loses
   /// something: a date with a time of day, a decimal with more places than the column's scale or too large for its
   /// precision, text longer than the column (as the database counts its length), a time of a day or more.
   /// Date-times with an offset keep theirs.
   /// </summary>
   public static bool TryConvert(ColumnDef column, object? value, SqlDialect dialect, out object? converted, out string? problem)
   {
      converted = null;
      problem = null;
      if (value is null or DBNull) { return true; }
      ScalarType type = column.Type;
      string takes = $"'{column.Name}' takes {TypeRules.Describe(type)}";
      try
      {
         switch (type.Kind)
         {
            case ScalarKind.DateTimeOffset:
               converted = value switch
               {
                  DateTimeOffset offset => offset,
                  string text => DateTimeOffset.Parse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                  _ => ValueConverter.ToLogical(value, type),
               };
               return true;
            case ScalarKind.Date when value is DateTime { TimeOfDay.Ticks: not 0 } or DateTimeOffset { TimeOfDay.Ticks: not 0 }:
               problem = $"{takes}; {Show(value)} has a time of day";
               return false;
            case ScalarKind.Time when value is TimeSpan span && (span < TimeSpan.Zero || span >= TimeSpan.FromDays(1)):
               problem = $"{takes}; {Show(value)} isn't a time of day";
               return false;
            case ScalarKind.Decimal:
               decimal exact = (decimal)ValueConverter.ToLogical(value, ScalarType.Decimal())!;
               if (type.Precision > 0 && Fits(exact, type) is { } why)
               {
                  problem = $"'{column.Name}' takes a number of {type.Precision} digits, {type.Scale} of them decimal places; {Show(value)} {why}";
                  return false;
               }
               converted = ValueConverter.ToLogical(exact, type);
               return true;
            default:
               converted = ValueConverter.ToLogical(value, type);
               break;
         }
      }
      catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException or ArgumentException)
      {
         problem = e is OverflowException ? $"{takes}; {Show(value)} is out of its range" : $"{takes}; {Show(value)} isn't one";
         return false;
      }
      if (converted is string written && type.Kind == ScalarKind.String && type.Length > 0 && dialect.TextLength(written) > type.Length)
      {
         problem = $"'{column.Name}' takes text of at most {type.Length} characters; {Show(value)} has {dialect.TextLength(written)}";
         return false;
      }
      return true;
   }

   /// <summary>Why a decimal doesn't fit a precision and scale, or null.</summary>
   private static string? Fits(decimal value, ScalarType type)
   {
      // Dividing by 1.000... drops trailing zeros, so the scale is the places the value needs.
      decimal normalized = value / 1.0000000000000000000000000000m;
      if (normalized.Scale > type.Scale) { return "has more"; }
      decimal whole = decimal.Truncate(Math.Abs(normalized));
      int digits = whole == 0 ? 0 : whole.ToString(CultureInfo.InvariantCulture).Length;
      return digits > type.Precision - type.Scale ? "is too large" : null;
   }

   private static string Show(object value)
   {
      string text = value is string s && s.Length > Shown ? BoundTreePrinter.Literal(s[..Shown] + "…") : BoundTreePrinter.Literal(value);
      return text;
   }
}
