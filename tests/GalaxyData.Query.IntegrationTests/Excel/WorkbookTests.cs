using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using GalaxyData.Query.Excel;
using GalaxyData.Query.Types;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Excel;

/// <summary>A folder of its own under the temp directory, deleted with everything in it when disposed.</summary>
internal sealed class TempFolder : IDisposable
{
   public TempFolder()
   {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gdq-excel-tests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(Path);
   }

   public string Path { get; }

   public string File(string name) => System.IO.Path.Combine(Path, name);

   public void Dispose()
   {
      try
      {
         Directory.Delete(Path, recursive: true);
      }
      catch (IOException)
      {
         // A file still open; the temp directory's cleanup gets it.
      }
   }
}

/// <summary>Workbooks read cell by cell, and sheets read as tables: headings, names and the types of columns.</summary>
public sealed class WorkbookTests
{
   /// <summary>Each row with a value: its number, then each cell as its column letter, kind and value.</summary>
   private static string Cells(string path, string sheet)
   {
      using Workbook workbook = Workbook.Open(path);
      SheetInfo info = workbook.Sheets.Single(s => s.Name == sheet);
      return string.Join(Environment.NewLine, workbook.ReadRows(info).Select(row =>
         row.Number + ": " + string.Join(" | ", row.Cells.Select(c => $"{Workbook.Letters(c.Column)} {c.Value.Kind} {c.Value}"))));
   }

   private static string Layout(string path, string sheet, bool headerRow = true, bool allText = false)
   {
      using Workbook workbook = Workbook.Open(path);
      SheetLayout layout = SheetLayout.Read(workbook.ReadRows(workbook.Sheets.Single(s => s.Name == sheet)), headerRow, allText);
      return $"header {layout.Header}, {layout.Rows} rows: " + string.Join(", ", layout.Columns.Select(c => $"{c.Name} ({Workbook.Letters(c.Index)}) {c.Type}"));
   }

   private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);

   [Fact]
   public void ReadsTheWorksheetsInOrderAndKnowsWhichAreHidden()
   {
      using TempFolder folder = new();
      new XlsxBuilder()
         .Sheet("Sheet 1", ["a"])
         .ChartSheet("Chart")
         .HiddenSheet("Secret", ["b"])
         .Sheet("Q2", ["c"])
         .Save(folder.File("book.xlsx"));
      using Workbook workbook = Workbook.Open(folder.File("book.xlsx"));
      workbook.Sheets.Select(s => $"{s.Name} {(s.Hidden ? "hidden" : "visible")}").ShouldBe(["Sheet 1 visible", "Secret hidden", "Q2 visible"]);
      workbook.Date1904.ShouldBeFalse();
   }

   [Fact]
   public void ReadsEachKindOfCell()
   {
      using TempFolder folder = new();
      new XlsxBuilder()
         .Sheet("cells",
            ["text", 42, 2.5, true, false],
            [XlsxBuilder.Error("#N/A"), XlsxBuilder.Formula("1+1", 2), XlsxBuilder.Formula("\"a\"&\"b\"", "ab"), XlsxBuilder.Rich("bold ", "and", " plain")],
            [new DateOnly(2026, 1, 5), new DateTime(2026, 1, 5, 10, 30, 15), new TimeOnly(13, 45, 30), XlsxBuilder.IsoDate("2026-02-03")],
            [XlsxBuilder.Format(new DateTime(2026, 3, 4), "d mmm yyyy"), XlsxBuilder.Format(0.75, "h:mm AM/PM"), XlsxBuilder.Format(1.5, "[h]:mm"), XlsxBuilder.Format(0.25, "0%")],
            null,
            ["  spaced  ", "line_x000D_break", "", "under_x005F_x0041_score"])
         .Save(folder.File("book.xlsx"));
      Cells(folder.File("book.xlsx"), "cells").ShouldBe(Lines(
         "1: A Text text | B Number 42 | C Number 2.5 | D Boolean TRUE | E Boolean FALSE",
         "2: A Error #N/A | B Number 2 | C Text ab | D Text bold and plain",
         "3: A Date 2026-01-05 | B DateTime 2026-01-05 10:30:15 | C Time 13:45:30 | D Date 2026-02-03",
         "4: A Date 2026-03-04 | B Time 18:00:00 | C Number 1.5 | D Number 0.25",
         "6: A Text   spaced   | B Text line\rbreak | D Text under_x0041_score"));
   }

   [Fact]
   public void ReadsInlineStringsAndCellsWithoutReferences()
   {
      using TempFolder folder = new();
      new XlsxBuilder { InlineStrings = true, OmitReferences = true }
         .Sheet("s", ["a", "b", "c"], [1, 2, 3])
         .Save(folder.File("book.xlsx"));
      Cells(folder.File("book.xlsx"), "s").ShouldBe(Lines("1: A Text a | B Text b | C Text c", "2: A Number 1 | B Number 2 | C Number 3"));
   }

   [Fact]
   public void ReadsDatesOfThe1904System()
   {
      using TempFolder folder = new();
      new XlsxBuilder { Date1904 = true }.Sheet("s", [new DateOnly(2026, 1, 5), new DateTime(1999, 12, 31, 23, 59, 59)]).Save(folder.File("book.xlsx"));
      Cells(folder.File("book.xlsx"), "s").ShouldBe("1: A Date 2026-01-05 | B DateTime 1999-12-31 23:59:59");
   }

   [Theory]
   [InlineData("yyyy-mm-dd", "Date")]
   [InlineData("d/m/yy", "Date")]
   [InlineData("mmm-yy", "Date")]
   [InlineData("mmmm", "Date")]
   [InlineData("dddd", "Date")]
   [InlineData("yyyy-mm-dd hh:mm", "DateTime")]
   [InlineData("m/d/yyyy h:mm AM/PM", "DateTime")]
   [InlineData("h:mm", "Time")]
   [InlineData("mm:ss", "Time")]
   [InlineData("h:mm a/p", "Time")]
   [InlineData("[h]:mm:ss", "None")]
   [InlineData("[mm]:ss", "None")]
   [InlineData("General", "None")]
   [InlineData("0.00", "None")]
   [InlineData("#,##0.00 \"days\"", "None")]
   [InlineData("0.00E+00", "None")]
   [InlineData("[Red]#,##0;[Blue]-#,##0", "None")]
   [InlineData("[$-409]d-mmm-yy", "Date")]
   [InlineData("\\d\\a\\y 0", "None")]
   [InlineData("0;\"dd\"", "None")]
   [InlineData("@", "None")]
   public void KnowsWhichFormatsShowDates(string code, string kind) => NumberFormats.ClassifyCode(code).ToString().ShouldBe(kind);

   [Theory]
   [InlineData(0, "A")]
   [InlineData(25, "Z")]
   [InlineData(26, "AA")]
   [InlineData(701, "ZZ")]
   [InlineData(702, "AAA")]
   [InlineData(16383, "XFD")]
   public void NamesColumnsByTheirLetters(int column, string letters)
   {
      Workbook.Letters(column).ShouldBe(letters);
      Workbook.ColumnOf(letters + "12").ShouldBe(column);
   }

   [Fact]
   public void ReadsASheetAsATable()
   {
      using TempFolder folder = new();
      new XlsxBuilder()
         .Sheet("s",
            null,
            ["id", null, "Amount", "amount", "when", "at", "time", "flag", "mixed", "B", "empty"],
            [1, "x", 2.5, 3, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new TimeOnly(8, 0), true, 1, "b"],
            null,
            [2, "y", 4, 5, new DateOnly(2026, 1, 2), new DateTime(2026, 1, 2, 12, 0, 0), new TimeOnly(9, 30), false, "two", null, null, "past"],
            [3, XlsxBuilder.Error("#DIV/0!"), 6, 7])
         .Save(folder.File("book.xlsx"));
      Layout(folder.File("book.xlsx"), "s").ShouldBe(
         "header 2, 3 rows: id (A) int64?, B_2 (B) string?, Amount (C) double?, amount_2 (D) int64?, when (E) date?, at (F) datetime?, time (G) time?, " +
         "flag (H) boolean?, mixed (I) string?, B (J) string?, empty (K) string?, L (L) string?");
      Layout(folder.File("book.xlsx"), "s", allText: true).ShouldStartWith("header 2, 3 rows: id (A) string?, B_2 (B) string?, Amount (C) string?");
   }

   [Fact]
   public void ASheetWithoutAHeaderRowIsNamedByColumnLetters()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", [1, "a"], [2, "b"]).Save(folder.File("book.xlsx"));
      Layout(folder.File("book.xlsx"), "s", headerRow: false).ShouldBe("header 0, 2 rows: A (A) int64?, B (B) string?");
   }

   [Theory]
   [InlineData("int64", 12.0, "12")]
   [InlineData("int32", 12.5, "error: 12.5 isn't a whole number")]
   [InlineData("int16", 70000.0, "error: 70000 doesn't fit in a 16-bit whole number")]
   [InlineData("int64", " 007 ", "7")]
   [InlineData("decimal(10,2)", 12.25, "12.25")]
   [InlineData("decimal(10,2)", "1.5", "1.5")]
   [InlineData("double", "1e3", "1000")]
   [InlineData("string", 0.1, "0.1")]
   [InlineData("string", 1e20, "1E+20")]
   [InlineData("string", true, "TRUE")]
   [InlineData("boolean", "false", "False")]
   [InlineData("boolean", 1.0, "True")]
   [InlineData("boolean", 2.0, "error: 2 isn't a true/false value")]
   [InlineData("date", "2026-01-05", "2026-01-05")]
   [InlineData("date", 46027.0, "2026-01-05")]
   [InlineData("date", 46027.5, "error: 2026-01-05 12:00:00 has a time of day, and the column is of dates")]
   [InlineData("datetime", "2026-01-05 10:30", "2026-01-05 10:30:00")]
   [InlineData("time", 0.5, "12:00:00")]
   [InlineData("time", "13:45", "13:45:00")]
   [InlineData("guid", "8f4e0e7a-5a53-4c5e-8d7b-2f0a8c1d9e3f", "8f4e0e7a-5a53-4c5e-8d7b-2f0a8c1d9e3f")]
   [InlineData("guid", "nope", "error: 'nope' isn't a guid")]
   public void ConvertsCellsToTheTypesOfTheirColumns(string type, object cell, string expected)
   {
      CellValue value = cell switch
      {
         string text => CellValue.OfText(text),
         bool flag => CellValue.OfBoolean(flag),
         _ => CellValue.OfNumber(Convert.ToDouble(cell, System.Globalization.CultureInfo.InvariantCulture)),
      };
      bool converted = CellConversion.TryConvert(value, ScalarType.Parse(type), false, out object? result, out string? error);
      string actual = converted ? result switch
      {
         DateOnly date => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
         DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
         TimeOnly time => time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
         _ => Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture)!,
      } : "error: " + error;
      actual.ShouldBe(expected);
   }

   [Fact]
   public void ErrorsAndEmptyCellsAreNoValue()
   {
      CellConversion.TryConvert(CellValue.OfError("#N/A"), ScalarType.Int64, false, out object? error, out _).ShouldBeTrue();
      error.ShouldBeNull();
      CellConversion.TryConvert(CellValue.Empty, ScalarType.Text(), false, out object? empty, out _).ShouldBeTrue();
      empty.ShouldBeNull();
   }

   [Fact]
   public void AFileThatIsntAWorkbookSaysSo()
   {
      using TempFolder folder = new();
      File.WriteAllText(folder.File("text.xlsx"), "not a workbook");
      Should.Throw<WorkbookFormatException>(() => Workbook.Open(folder.File("text.xlsx"))).Message.ShouldBe("it isn't an .xlsx workbook, or it is encrypted");
      using (ZipArchive zip = ZipFile.Open(folder.File("empty.xlsx"), ZipArchiveMode.Create))
      {
         zip.CreateEntry("readme.txt");
      }
      Should.Throw<WorkbookFormatException>(() => Workbook.Open(folder.File("empty.xlsx"))).Message.ShouldBe("it has no workbook in it");
   }
}
