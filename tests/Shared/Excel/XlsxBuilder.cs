using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace GalaxyData.Testing;

/// <summary>
/// Writes .xlsx workbooks for tests with the Open XML SDK, as Excel does: text as shared strings (or inline),
/// dates, date-times and times as numbers with number formats, hidden and chart sheets. A row is an array of
/// values, a null value no cell, and a null row no row; <see cref="Error"/>, <see cref="Formula"/>,
/// <see cref="Rich"/>, <see cref="Format"/> and <see cref="IsoDate"/> make the other kinds of cell.
/// </summary>
internal sealed class XlsxBuilder
{
   private readonly List<(string Name, SheetKind Kind, object?[]?[] Rows)> sheets = [];

   /// <summary>Serial dates count from 1904.</summary>
   public bool Date1904 { get; init; }

   /// <summary>Text is written in its cells rather than as shared strings.</summary>
   public bool InlineStrings { get; init; }

   /// <summary>Rows and cells are written without their references, each following the one before.</summary>
   public bool OmitReferences { get; init; }

   public static object Error(string code) => new ErrorCell(code);

   public static object Formula(string formula, object cached) => new FormulaCell(formula, cached);

   /// <summary>Text in runs, as rich text is, with a phonetic hint that isn't part of it.</summary>
   public static object Rich(params string[] runs) => new RichText(runs);

   /// <summary>A number (or date, date-time or time) with a number format of its own.</summary>
   public static object Format(object value, string code) => new Formatted(value, code);

   /// <summary>A cell of type <c>d</c>: a date as ISO text.</summary>
   public static object IsoDate(string text) => new IsoDateCell(text);

   public static object?[] Row(params object?[] values) => values;

   public XlsxBuilder Sheet(string name, params object?[]?[] rows)
   {
      sheets.Add((name, SheetKind.Visible, rows));
      return this;
   }

   public XlsxBuilder HiddenSheet(string name, params object?[]?[] rows)
   {
      sheets.Add((name, SheetKind.Hidden, rows));
      return this;
   }

   public XlsxBuilder ChartSheet(string name)
   {
      sheets.Add((name, SheetKind.Chart, []));
      return this;
   }

   public void Save(string path)
   {
      using SpreadsheetDocument document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
      WorkbookPart workbookPart = document.AddWorkbookPart();
      Writer writer = new(this, workbookPart);
      Workbook workbook = new();
      if (Date1904) { workbook.AppendChild(new WorkbookProperties { Date1904 = true }); }
      Sheets list = workbook.AppendChild(new Sheets());
      uint id = 1;
      foreach ((string name, SheetKind kind, object?[]?[] rows) in sheets)
      {
         if (kind == SheetKind.Chart)
         {
            ChartsheetPart chart = workbookPart.AddNewPart<ChartsheetPart>();
            chart.Chartsheet = new Chartsheet(new ChartSheetViews(new ChartSheetView { WorkbookViewId = 0U }));
            list.Append(new Sheet { Name = name, SheetId = id++, Id = workbookPart.GetIdOfPart(chart) });
            continue;
         }
         WorksheetPart part = workbookPart.AddNewPart<WorksheetPart>();
         part.Worksheet = new Worksheet(writer.Data(rows));
         Sheet sheet = new() { Name = name, SheetId = id++, Id = workbookPart.GetIdOfPart(part) };
         if (kind == SheetKind.Hidden) { sheet.State = SheetStateValues.Hidden; }
         list.Append(sheet);
      }
      workbookPart.Workbook = workbook;
      writer.Finish();
   }

   private enum SheetKind
   {
      Visible,
      Hidden,
      Chart,
   }

   private sealed record ErrorCell(string Code);

   private sealed record FormulaCell(string Formula, object Cached);

   private sealed record RichText(string[] Runs);

   private sealed record Formatted(object Value, string Code);

   private sealed record IsoDateCell(string Text);

   /// <summary>Writes the cells of one workbook, collecting its shared strings and number formats.</summary>
   private sealed class Writer(XlsxBuilder builder, WorkbookPart workbook)
   {
      private const string DateTimeFormat = "yyyy-mm-dd hh:mm:ss";
      private readonly SharedStringTable strings = new();
      private readonly Dictionary<string, int> stringIndexes = new(StringComparer.Ordinal);
      private int stringCount;
      private readonly List<(uint Id, string? Code)> styles = [(0, null), (14, null), (164, DateTimeFormat), (21, null)];

      public SheetData Data(object?[]?[] rows)
      {
         SheetData data = new();
         for (int r = 0; r < rows.Length; r++)
         {
            if (rows[r] is not { } values) { continue; }
            Row row = new();
            if (!builder.OmitReferences) { row.RowIndex = (uint)(r + 1); }
            for (int c = 0; c < values.Length; c++)
            {
               if (values[c] is not { } value) { continue; }
               Cell cell = Make(value);
               if (!builder.OmitReferences) { cell.CellReference = Letters(c) + (r + 1).ToString(CultureInfo.InvariantCulture); }
               row.Append(cell);
            }
            data.Append(row);
         }
         return data;
      }

      private Cell Make(object value) => value switch
      {
         string text when builder.InlineStrings => new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(Text(text)) },
         string text => new Cell { DataType = CellValues.SharedString, CellValue = new CellValue(Shared(text).ToString(CultureInfo.InvariantCulture)) },
         RichText rich => new Cell { DataType = CellValues.SharedString, CellValue = new CellValue(SharedRich(rich.Runs).ToString(CultureInfo.InvariantCulture)) },
         bool flag => new Cell { DataType = CellValues.Boolean, CellValue = new CellValue(flag ? "1" : "0") },
         ErrorCell error => new Cell { DataType = CellValues.Error, CellValue = new CellValue(error.Code) },
         IsoDateCell iso => new Cell { DataType = CellValues.Date, CellValue = new CellValue(iso.Text) },
         FormulaCell formula => Formula(formula),
         Formatted formatted => Number(formatted.Value, Style(formatted.Code)),
         DateOnly or DateTime or TimeOnly => Number(value, value switch { DateOnly => 1U, DateTime => 2U, _ => 3U }),
         _ => Number(value, 0),
      };

      private Cell Formula(FormulaCell formula)
      {
         Cell cell = formula.Cached is string text
            ? new Cell { DataType = CellValues.String, CellValue = new CellValue(text) }
            : Make(formula.Cached);
         cell.CellFormula = new CellFormula(formula.Formula);
         return cell;
      }

      private Cell Number(object value, uint style)
      {
         double number = value switch
         {
            DateOnly date => Serial(date.ToDateTime(TimeOnly.MinValue)),
            DateTime dateTime => Serial(dateTime),
            TimeOnly time => time.ToTimeSpan().TotalDays,
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
         };
         Cell cell = new() { CellValue = new CellValue(number.ToString("R", CultureInfo.InvariantCulture)) };
         if (style != 0) { cell.StyleIndex = style; }
         return cell;
      }

      private double Serial(DateTime value) => value.ToOADate() - (builder.Date1904 ? 1462 : 0);

      private uint Style(string code)
      {
         int existing = styles.FindIndex(s => s.Code == code);
         if (existing >= 0) { return (uint)existing; }
         styles.Add((164U + (uint)styles.Count(s => s.Code != null), code));
         return (uint)(styles.Count - 1);
      }

      private static Text Text(string text) => new(text) { Space = SpaceProcessingModeValues.Preserve };

      private int Shared(string text)
      {
         if (stringIndexes.TryGetValue(text, out int index)) { return index; }
         strings.Append(new SharedStringItem(Text(text)));
         return stringIndexes[text] = stringCount++;
      }

      private int SharedRich(string[] runs)
      {
         SharedStringItem item = new(runs.Select((run, i) => (OpenXmlElement)(i % 2 == 1
            ? new Run(new RunProperties(new Bold()), Text(run))
            : new Run(Text(run)))));
         item.Append(new PhoneticRun(new Text("ignored")) { BaseTextStartIndex = 0U, EndingBaseIndex = 1U });
         strings.Append(item);
         return stringCount++;
      }

      public void Finish()
      {
         SharedStringTablePart shared = workbook.AddNewPart<SharedStringTablePart>();
         shared.SharedStringTable = strings;
         WorkbookStylesPart stylesPart = workbook.AddNewPart<WorkbookStylesPart>();
         NumberingFormats formats = new(styles.Where(s => s.Code != null).Select(s => new NumberingFormat { NumberFormatId = s.Id, FormatCode = s.Code }));
         stylesPart.Stylesheet = new Stylesheet(
            formats,
            new Fonts(new Font()),
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
            new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(styles.Select(s => new CellFormat { NumberFormatId = s.Id, ApplyNumberFormat = s.Id != 0 })));
      }
   }

   public static string Letters(int column)
   {
      string letters = string.Empty;
      for (int n = column + 1; n > 0; n = (n - 1) / 26) { letters = (char)('A' + (n - 1) % 26) + letters; }
      return letters;
   }
}
