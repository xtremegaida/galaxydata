using System;
using System.Globalization;

namespace GalaxyData.Query.Excel;

internal enum CellKind : byte
{
   Empty,
   Number,
   Text,
   Boolean,
   Error,
   Date,
   DateTime,
   Time,
}

/// <summary>
/// The value of a cell, as the workbook holds it. Numbers are doubles, as Excel keeps them; a number shown as a
/// date or a time (by its number format) is one, as an OLE automation date (days since 1899-12-30) or, for a time
/// of day alone, the fraction of a day. An empty string, or one of spaces alone (what clearing a cell often
/// leaves), is no value.
/// </summary>
internal readonly struct CellValue
{
   private CellValue(CellKind kind, double number, string? text)
   {
      Kind = kind;
      Number = number;
      Text = text;
   }

   public static CellValue Empty => default;

   public CellKind Kind { get; }

   /// <summary>A number; a date or date-time as an OLE automation date; a time as a fraction of a day; 1 or 0 for a boolean.</summary>
   public double Number { get; }

   /// <summary>Text; an error's code (<c>#N/A</c>).</summary>
   public string? Text { get; }

   public bool IsEmpty => Kind == CellKind.Empty;

   public static CellValue OfNumber(double number) => new(CellKind.Number, number, null);

   public static CellValue OfText(string? text) => string.IsNullOrWhiteSpace(text) ? default : new CellValue(CellKind.Text, 0, text);

   public static CellValue OfBoolean(bool value) => new(CellKind.Boolean, value ? 1 : 0, null);

   public static CellValue OfError(string code) => new(CellKind.Error, 0, code);

   /// <summary>A number with a date or time format: a time of day alone when it has no days, else a date, with its time when it has one.</summary>
   public static CellValue OfDate(DateFormat format, double serial, bool date1904)
   {
      if (format == DateFormat.Time && serial is >= 0 and < 1) { return new CellValue(CellKind.Time, serial, null); }
      double automation = Automation(serial, date1904);
      bool whole = Math.Floor(automation) == automation;
      return new CellValue(format == DateFormat.Date && whole ? CellKind.Date : CellKind.DateTime, automation, null);
   }

   /// <summary>
   /// A date written as ISO 8601 text (a cell of type <c>d</c>): a date, a date-time when it has a time (in UTC, when
   /// it has an offset), or a time of day when it has no date.
   /// </summary>
   public static CellValue OfIsoDate(string text)
   {
      if (IsoText.TryTime(text, out TimeOnly time)) { return new CellValue(CellKind.Time, time.ToTimeSpan().TotalDays, null); }
      if (!IsoText.TryDateTime(text, out DateTimeOffset value, out bool timed)) { return OfText(text); }
      return new CellValue(timed ? CellKind.DateTime : CellKind.Date, value.UtcDateTime.ToOADate(), null);
   }

   /// <summary>
   /// A serial date as an OLE automation date. Excel's 1900 date system counts 1900-02-29, which never was, so its
   /// serials before March 1900 are a day behind; the 1904 system counts from 1904-01-01.
   /// </summary>
   public static double Automation(double serial, bool date1904) => date1904 ? serial + 1462 : serial is >= 1 and < 61 ? serial + 1 : serial;

   /// <summary>The date-time of an OLE automation date, when it is one DateTime holds.</summary>
   public static bool TryDateTime(double automation, out DateTime value)
   {
      if (automation is > -657435.0 and < 2958466.0)
      {
         value = DateTime.FromOADate(automation);
         return true;
      }
      value = default;
      return false;
   }

   /// <summary>The time of day of a fraction of a day, to the millisecond.</summary>
   public static TimeOnly TimeOfDay(double fraction)
   {
      long milliseconds = Math.Clamp((long)Math.Round(fraction * 86_400_000d), 0, 86_399_999);
      return new TimeOnly(milliseconds * TimeSpan.TicksPerMillisecond);
   }

   public override string ToString() => CellText.Of(this) ?? Text ?? string.Empty;
}

/// <summary>
/// Dates and times as text, in ISO 8601 form only (<c>2026-01-05</c>, <c>2026-01-05 10:30</c>,
/// <c>2026-01-05T10:30:00.5+02:00</c>, <c>10:30</c>), so text reads the same on every machine; <c>01/02/2026</c> isn't one.
/// </summary>
internal static class IsoText
{
   private static readonly string[] DateTimes =
   [
      "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
      "yyyy-MM-dd HH:mmzzz", "yyyy-MM-ddTHH:mmzzz", "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz",
      "yyyy-MM-dd HH:mmZ", "yyyy-MM-ddTHH:mmZ", "yyyy-MM-dd HH:mm:ss.FFFFFFFZ", "yyyy-MM-ddTHH:mm:ss.FFFFFFFZ",
   ];

   private static readonly string[] Times = ["HH:mm", "HH:mm:ss.FFFFFFF"];

   /// <summary>A date or date-time, as UTC when it has no offset; <paramref name="timed"/> when it has a time of day.</summary>
   public static bool TryDateTime(string text, out DateTimeOffset value, out bool timed)
   {
      string trimmed = text.Trim();
      timed = trimmed.Length > 10;
      return DateTimeOffset.TryParseExact(trimmed, DateTimes, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
   }

   public static bool TryTime(string text, out TimeOnly value) =>
      TimeOnly.TryParseExact(text.Trim(), Times, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}

/// <summary>How a number format shows numbers: as themselves, or as dates, date-times or times of day.</summary>
internal enum DateFormat : byte
{
   None,
   Date,
   DateTime,
   Time,
}

/// <summary>Which number formats show dates and times: Excel's built-in ones by id, custom ones by their codes.</summary>
internal static class NumberFormats
{
   public static DateFormat Classify(int id, string? code) => code != null ? ClassifyCode(code) : id switch
   {
      14 or 15 or 16 or 17 or (>= 27 and <= 31) or (>= 34 and <= 36) or (>= 50 and <= 58) => DateFormat.Date,
      18 or 19 or 20 or 21 or 32 or 33 or 45 or 47 => DateFormat.Time,
      22 => DateFormat.DateTime,
      // 46 is [h]:mm:ss, a duration: a number of days.
      _ => DateFormat.None,
   };

   /// <summary>
   /// A custom format's kind, from the first of its sections: <c>y</c> and <c>d</c> are parts of dates, <c>h</c> and
   /// <c>s</c> of times, and <c>m</c> is months unless the format has times, when it is minutes. Quoted text, escaped
   /// and padding characters and bracketed colours, conditions and locales mean nothing here; bracketed hours,
   /// minutes or seconds (<c>[h]:mm</c>) make it a duration, which is a number.
   /// </summary>
   public static DateFormat ClassifyCode(string code)
   {
      bool date = false;
      bool time = false;
      bool months = false;
      for (int i = 0; i < code.Length; i++)
      {
         char c = code[i];
         switch (c)
         {
            case '"':
               int close = code.IndexOf('"', i + 1);
               i = close < 0 ? code.Length : close;
               break;
            case '\\' or '_' or '*':
               i++;
               break;
            case '[':
               int end = code.IndexOf(']', i + 1);
               string inside = code[(i + 1)..(end < 0 ? code.Length : end)];
               if (inside.Length > 0 && inside.AsSpan().IndexOfAnyExcept("hHmMsS") < 0) { return DateFormat.None; }
               i = end < 0 ? code.Length : end;
               break;
            case ';':
               i = code.Length;
               break;
            default:
               if (string.Compare(code, i, "AM/PM", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
               {
                  time = true;
                  i += 4;
               }
               else if (string.Compare(code, i, "A/P", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
               {
                  time = true;
                  i += 2;
               }
               else
               {
                  switch (char.ToLowerInvariant(c))
                  {
                     case 'y' or 'd':
                        date = true;
                        break;
                     case 'h' or 's':
                        time = true;
                        break;
                     case 'm':
                        months = true;
                        break;
                  }
               }
               break;
         }
      }
      if (months && !time) { date = true; }
      return (date, time) switch
      {
         (true, true) => DateFormat.DateTime,
         (true, false) => DateFormat.Date,
         (false, true) => DateFormat.Time,
         _ => DateFormat.None,
      };
   }
}

/// <summary>Values as text, for columns of text: numbers as they read (<c>12</c>, <c>0.1</c>), dates and times in ISO form, booleans as Excel shows them.</summary>
internal static class CellText
{
   public static string? Of(CellValue value) => value.Kind switch
   {
      CellKind.Text => value.Text,
      CellKind.Boolean => value.Number != 0 ? "TRUE" : "FALSE",
      CellKind.Number => Number(value.Number),
      CellKind.Date => CellValue.TryDateTime(value.Number, out DateTime date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Number(value.Number),
      CellKind.DateTime => CellValue.TryDateTime(value.Number, out DateTime dateTime) ? DateTimeText(dateTime) : Number(value.Number),
      CellKind.Time => TimeText(CellValue.TimeOfDay(value.Number)),
      _ => null,
   };

   public static string Number(double number) =>
      Math.Floor(number) == number && Math.Abs(number) < 1e15
         ? ((long)number).ToString(CultureInfo.InvariantCulture)
         : number.ToString("R", CultureInfo.InvariantCulture);

   private static string DateTimeText(DateTime value) =>
      value.ToString(value.Millisecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

   private static string TimeText(TimeOnly value) =>
      value.ToString(value.Millisecond == 0 ? "HH:mm:ss" : "HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
