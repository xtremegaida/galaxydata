using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Excel;

/// <summary>A column of a sheet read as a table: its name, its column in the sheet (from 0), and the type of its values.</summary>
internal sealed record SheetColumn(string Name, int Index, ScalarType Type);

/// <summary>
/// A sheet read as a table. The first row with a value names the columns (unless the sheet has no header row): a
/// blank heading (or an error) is named by its column's letter, as are columns with values past the headings, and
/// a heading that repeats one before it (in any case) gets <c>_2</c>, <c>_3</c>, whichever no heading has. Each
/// column's type is the one all its values share: whole numbers are int64, other numbers doubles, numbers shown as
/// dates dates (or date-times, when some have a time), numbers shown as times of day times; any mix, or text, is
/// text. Rows without a value (errors are none) are left out.
/// </summary>
internal sealed class SheetLayout
{
   private SheetLayout(IReadOnlyList<SheetColumn> columns, long rows, int header)
   {
      Columns = columns;
      Rows = rows;
      Header = header;
   }

   public IReadOnlyList<SheetColumn> Columns { get; }

   /// <summary>The rows with a value after the header.</summary>
   public long Rows { get; }

   /// <summary>The number of the header row; 0 when there is none.</summary>
   public int Header { get; }

   public static SheetLayout Read(IEnumerable<SheetRow> rows, bool headerRow, bool allText)
   {
      Dictionary<int, string> headings = [];
      SortedDictionary<int, Kinds> seen = [];
      int header = 0;
      long count = 0;
      foreach (SheetRow row in rows)
      {
         if (!HasValue(row)) { continue; }
         if (headerRow && header == 0)
         {
            header = row.Number;
            foreach (Cell cell in row.Cells)
            {
               string heading = Clean(CellText.Of(cell.Value) ?? string.Empty);
               if (heading.Length > 0) { headings[cell.Column] = heading; }
               seen.TryAdd(cell.Column, Kinds.None);
            }
            continue;
         }
         count++;
         foreach (Cell cell in row.Cells) { seen[cell.Column] = seen.GetValueOrDefault(cell.Column) | KindOf(cell.Value); }
      }

      // Headings keep their names, the first time they're used; repeats and letters, for columns without one, give way to them.
      HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
      Dictionary<int, string> names = [];
      foreach (int column in seen.Keys)
      {
         if (headings.TryGetValue(column, out string? heading) && used.Add(heading)) { names[column] = heading; }
      }
      foreach (int column in seen.Keys)
      {
         if (!names.ContainsKey(column) && headings.TryGetValue(column, out string? heading)) { names[column] = Unique(heading, used); }
      }
      foreach (int column in seen.Keys)
      {
         if (!names.ContainsKey(column)) { names[column] = Unique(Workbook.Letters(column), used); }
      }
      List<SheetColumn> columns = seen.Select(c => new SheetColumn(names[c.Key], c.Key, allText ? ScalarType.Text() : TypeOf(c.Value))).ToList();
      return new SheetLayout(columns, count, header);
   }

   /// <summary>Whether a row has a value: a cell that isn't an error.</summary>
   public static bool HasValue(SheetRow row)
   {
      foreach (Cell cell in row.Cells)
      {
         if (cell.Value.Kind != CellKind.Error) { return true; }
      }
      return false;
   }

   /// <summary>A heading on one line, without the spaces around it.</summary>
   private static string Clean(string heading) =>
      heading.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();

   private static string Unique(string name, HashSet<string> used)
   {
      if (used.Add(name)) { return name; }
      for (int n = 2; ; n++)
      {
         string candidate = name + "_" + n.ToString(CultureInfo.InvariantCulture);
         if (used.Add(candidate)) { return candidate; }
      }
   }

   [Flags]
   private enum Kinds : byte
   {
      None = 0,
      Boolean = 1,
      Whole = 2,
      Fraction = 4,
      Date = 8,
      DateTime = 16,
      Time = 32,
      Text = 64,
   }

   /// <summary>Doubles hold whole numbers exactly up to 2^53.</summary>
   private const double LargestWhole = 9_007_199_254_740_992d;

   private static Kinds KindOf(CellValue value) => value.Kind switch
   {
      CellKind.Boolean => Kinds.Boolean,
      CellKind.Number => Math.Floor(value.Number) == value.Number && Math.Abs(value.Number) <= LargestWhole ? Kinds.Whole : Kinds.Fraction,
      CellKind.Date => Kinds.Date,
      CellKind.DateTime => Kinds.DateTime,
      CellKind.Time => Kinds.Time,
      CellKind.Text => Kinds.Text,
      // Errors are no value.
      _ => Kinds.None,
   };

   private static ScalarType TypeOf(Kinds kinds) => kinds switch
   {
      Kinds.Boolean => ScalarType.Boolean,
      Kinds.Whole => ScalarType.Int64,
      Kinds.Fraction or Kinds.Whole | Kinds.Fraction => ScalarType.Double,
      Kinds.Date => ScalarType.Date,
      Kinds.DateTime or Kinds.Date | Kinds.DateTime => ScalarType.DateTime,
      Kinds.Time => ScalarType.Time,
      _ => ScalarType.Text(),
   };
}

/// <summary>
/// Cell values as the values of a column's type. Text reads as numbers, dates, times and the like when it is their
/// invariant (ISO) form; numbers read as serial dates, of the workbook's date system, when the column is of dates;
/// decimals are rounded to the column's scale; errors are no value.
/// </summary>
internal static class CellConversion
{
   /// <summary>The longest interval, in days, a TimeSpan holds.</summary>
   private const double LongestInterval = 10_675_199d;

   public static bool TryConvert(CellValue value, ScalarType type, bool date1904, out object? result, out string? error)
   {
      error = null;
      result = null;
      if (value.Kind is CellKind.Empty or CellKind.Error) { return true; }
      double number = value.Number;
      string? text = value.Kind == CellKind.Text ? value.Text!.Trim() : null;
      switch (type.Kind)
      {
         case ScalarKind.String or ScalarKind.Json or ScalarKind.Unknown:
            result = CellText.Of(value);
            return true;
         case ScalarKind.Boolean:
            if ((value.Kind is CellKind.Boolean or CellKind.Number) && number is 0 or 1) { result = number != 0; }
            else if (text != null && bool.TryParse(text, out bool flag)) { result = flag; }
            break;
         case ScalarKind.Int16 or ScalarKind.Int32 or ScalarKind.Int64:
            long? whole = value.Kind == CellKind.Number && Math.Floor(number) == number && number is >= -9.2233720368547758E+18 and < 9.2233720368547758E+18 ? (long)number
               : text != null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedWhole) ? parsedWhole
               : null;
            if (whole != null)
            {
               result = Whole(whole.Value, type.Kind);
               if (result == null)
               {
                  error = $"{whole.Value.ToString(CultureInfo.InvariantCulture)} doesn't fit in a {(type.Kind == ScalarKind.Int16 ? "16" : "32")}-bit whole number";
                  return false;
               }
            }
            break;
         case ScalarKind.Decimal:
            decimal? exact = value.Kind == CellKind.Number && number is > -7.9E28 and < 7.9E28 ? (decimal)number
               : text != null && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsedDecimal) ? parsedDecimal
               : null;
            if (exact != null) { result = type.Precision > 0 ? decimal.Round(exact.Value, type.Scale, MidpointRounding.AwayFromZero) : exact.Value; }
            break;
         case ScalarKind.Single or ScalarKind.Double:
            double? real = value.Kind == CellKind.Number ? number
               : text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed
               : null;
            if (real != null) { result = type.Kind == ScalarKind.Single ? (object)(float)real.Value : real.Value; }
            break;
         case ScalarKind.Date:
            if (MomentOf(value, text, date1904) is { } day)
            {
               DateTime utc = day.UtcDateTime;
               if (utc.TimeOfDay != TimeSpan.Zero)
               {
                  error = $"{utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} has a time of day, and the column is of dates";
                  return false;
               }
               result = DateOnly.FromDateTime(utc);
            }
            break;
         case ScalarKind.DateTime:
            if (MomentOf(value, text, date1904) is { } moment) { result = DateTime.SpecifyKind(moment.UtcDateTime, DateTimeKind.Unspecified); }
            break;
         case ScalarKind.DateTimeOffset:
            if (MomentOf(value, text, date1904) is { } instant) { result = instant; }
            break;
         case ScalarKind.Time:
            if (value.Kind == CellKind.Time || (value.Kind == CellKind.Number && number is >= 0 and < 1)) { result = CellValue.TimeOfDay(number); }
            else if (text != null && IsoText.TryTime(text, out TimeOnly time)) { result = time; }
            break;
         case ScalarKind.Interval:
            if (value.Kind is CellKind.Number or CellKind.Time && Math.Abs(number) < LongestInterval) { result = TimeSpan.FromDays(number); }
            else if (text != null && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan span)) { result = span; }
            break;
         case ScalarKind.Guid:
            if (text != null && Guid.TryParse(text, out Guid guid)) { result = guid; }
            break;
      }
      if (result != null) { return true; }
      error ??= $"{Describe(value)} isn't {TypeRules.Describe(type)}";
      return false;
   }

   /// <summary>A whole number as the column's integer type; null when it doesn't fit.</summary>
   private static object? Whole(long value, ScalarKind kind) => kind switch
   {
      ScalarKind.Int16 when value is >= short.MinValue and <= short.MaxValue => (short)value,
      ScalarKind.Int32 when value is >= int.MinValue and <= int.MaxValue => (int)value,
      ScalarKind.Int64 => value,
      _ => null,
   };

   /// <summary>A moment from a date, a date-time, a number as a serial date of the workbook's date system, or ISO text (UTC unless it has an offset).</summary>
   private static DateTimeOffset? MomentOf(CellValue value, string? text, bool date1904)
   {
      double? automation = value.Kind switch
      {
         CellKind.Date or CellKind.DateTime => value.Number,
         CellKind.Number => CellValue.Automation(value.Number, date1904),
         _ => null,
      };
      if (automation != null) { return CellValue.TryDateTime(automation.Value, out DateTime date) ? new DateTimeOffset(date, TimeSpan.Zero) : null; }
      if (text != null && IsoText.TryDateTime(text, out DateTimeOffset parsed, out _)) { return parsed; }
      return null;
   }

   private static string Describe(CellValue value) => value.Kind switch
   {
      CellKind.Text => $"'{value.Text}'",
      CellKind.Boolean => CellText.Of(value)!,
      CellKind.Number => CellText.Of(value)!,
      CellKind.Date => "the date " + CellText.Of(value),
      CellKind.DateTime => "the date-time " + CellText.Of(value),
      CellKind.Time => "the time " + CellText.Of(value),
      _ => "the value",
   };
}
