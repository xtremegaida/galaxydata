using GalaxyData.Testing;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Cli;
using GalaxyData.Query.Excel;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.IntegrationTests.Execution;
using GalaxyData.Query.Language;
using GalaxyData.Query.Types;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Excel;

/// <summary>
/// Edges of Excel folder sources: unusual packages and cells, headings and types, conversions, loading and
/// reloading, queries across sources, names and security, and the command line. Hand-made packages cover what
/// <see cref="XlsxBuilder"/> can't write.
/// </summary>
public sealed class ExcelEdgeTests
{
   private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
   private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
   private const string PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
   private const string Budget = "xl[\"Budget 2024\"][\"Sheet 1\"]";

   private static readonly string Sqlite = "shop=sqlite:" + Path.Combine(AppContext.BaseDirectory, "fixtures", "shop.sqlite.sql");

   private static readonly string NL = Environment.NewLine;

   private static CancellationToken Token => TestContext.Current.CancellationToken;

   // ---------------------------------------------------------------------------------------------------------------
   // Helpers
   // ---------------------------------------------------------------------------------------------------------------

   private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

   private static async Task<string> RowsAsync(QueryEngine engine, string query, QueryParameters? parameters = null)
   {
      await using QueryResult result = await engine.Prepare(query, parameters).ExecuteAsync(Token);
      return await TestSources.RowsAsync(result);
   }

   private static async Task<IReadOnlyList<object?[]>> ValuesAsync(QueryEngine engine, string query)
   {
      await using QueryResult result = await engine.Prepare(query).ExecuteAsync(Token);
      return await result.ToListAsync(Token);
   }

   /// <summary>Values with their CLR types, a row a line: <c>Int64:42 | DateOnly:2024-01-15</c>.</summary>
   private static string Typed(IEnumerable<object?[]> rows) => string.Join(NL, rows.Select(row => string.Join(" | ", row.Select(value => value switch
   {
      null => "null",
      DateTime dateTime => "DateTime:" + dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
      DateTimeOffset offset => "DateTimeOffset:" + offset.ToString("o", CultureInfo.InvariantCulture),
      DateOnly date => "DateOnly:" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
      TimeOnly time => "TimeOnly:" + time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
      double number => "Double:" + number.ToString("R", CultureInfo.InvariantCulture),
      _ => value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture),
   }))));

   private static CatalogOverlay Override(string entity, params (string Column, ScalarType Type)[] columns) =>
      new() { Entities = [new OverlayEntitySettings(entity) { Columns = [.. columns.Select(c => new OverlayColumn(c.Column) { Type = c.Type })] }] };

   /// <summary>Each row with a value: its number, then each cell as its column letter, kind and value.</summary>
   private static string Cells(string path, string sheet = "s")
   {
      using Workbook workbook = Workbook.Open(path);
      SheetInfo info = workbook.Sheets.Single(s => s.Name == sheet);
      return string.Join(NL, workbook.ReadRows(info).Select(row =>
         row.Number + ": " + string.Join(" | ", row.Cells.Select(c => $"{Workbook.Letters(c.Column)} {c.Value.Kind} {c.Value}"))));
   }

   private static string Layout(string path, string sheet = "s", bool headerRow = true, bool allText = false)
   {
      using Workbook workbook = Workbook.Open(path);
      SheetLayout layout = SheetLayout.Read(workbook.ReadRows(workbook.Sheets.Single(s => s.Name == sheet)), headerRow, allText);
      return $"header {layout.Header}, {layout.Rows} rows: " + string.Join(", ", layout.Columns.Select(c => $"{c.Name} ({Workbook.Letters(c.Index)}) {c.Type}"));
   }

   /// <summary>Writes a zip package of these parts, named and written exactly as given.</summary>
   private static void Package(string path, params (string Name, string Content)[] parts)
   {
      using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
      foreach ((string name, string content) in parts)
      {
         using StreamWriter writer = new(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
         writer.Write(content);
      }
   }

   private static string Rels(params (string Id, string Type, string Target)[] rels) =>
      $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{PackageRel}\">" +
      string.Concat(rels.Select(r => $"<Relationship Id=\"{r.Id}\" Type=\"{r.Type}\" Target=\"{r.Target}\"/>")) + "</Relationships>";

   private static string Escape(string text) =>
      text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);

   private static string WorkbookXml(params string[] sheets) =>
      $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets>" +
      string.Concat(sheets.Select((s, i) => $"<sheet name=\"{Escape(s)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets></workbook>";

   private static string Sheet(string rows) => $"<worksheet xmlns=\"{Main}\"><sheetData>{rows}</sheetData></worksheet>";

   private static string InlineRow(int row, string text) => $"<row r=\"{row}\"><c r=\"A{row}\" t=\"inlineStr\"><is><t>{text}</t></is></c></row>";

   /// <summary>A hand-made workbook: its sheets (a name and worksheet XML each), in order, with shared strings and styles parts when given.</summary>
   private static void Book(string path, (string Name, string Xml)[] sheets, string? sharedStrings = null, string? styles = null, string? workbook = null)
   {
      List<(string, string)> parts = [("_rels/.rels", Rels(("rId1", Rel + "/officeDocument", "xl/workbook.xml")))];
      List<(string, string, string)> rels = [];
      for (int i = 0; i < sheets.Length; i++)
      {
         parts.Add(($"xl/worksheets/sheet{i + 1}.xml", sheets[i].Xml));
         rels.Add(($"rId{i + 1}", Rel + "/worksheet", $"worksheets/sheet{i + 1}.xml"));
      }
      parts.Add(("xl/workbook.xml", workbook ?? WorkbookXml([.. sheets.Select(s => s.Name)])));
      if (sharedStrings != null)
      {
         parts.Add(("xl/sharedStrings.xml", sharedStrings));
         rels.Add(("rIdS", Rel + "/sharedStrings", "sharedStrings.xml"));
      }
      if (styles != null)
      {
         parts.Add(("xl/styles.xml", styles));
         rels.Add(("rIdT", Rel + "/styles", "styles.xml"));
      }
      parts.Add(("xl/_rels/workbook.xml.rels", Rels([.. rels])));
      Package(path, [.. parts]);
   }

   /// <summary>A hand-made workbook of one sheet, s, of these rows (XML), with the items of its shared strings and the content of its styles when given.</summary>
   private static void Book(string path, string rows, string? sharedStrings = null, string? styles = null) =>
      Book(path, [("s", Sheet(rows))],
           sharedStrings == null ? null : $"<sst xmlns=\"{Main}\">{sharedStrings}</sst>",
           styles == null ? null : $"<styleSheet xmlns=\"{Main}\">{styles}</styleSheet>");

   /// <summary>A workbook with one sheet, s: a heading n, then whole numbers from <paramref name="first"/>.</summary>
   private static void BigBook(string path, int rows, long first)
   {
      StringBuilder xml = new();
      xml.Append(InlineRow(1, "n"));
      for (int r = 0; r < rows; r++)
      {
         int number = r + 2;
         xml.Append("<row r=\"").Append(number).Append("\"><c r=\"A").Append(number).Append("\"><v>").Append(first + r).Append("</v></c></row>");
      }
      Book(path, [("s", Sheet(xml.ToString()))]);
   }

   private static async Task<TestSources> FolderAsync(string path, bool shop = false, ExcelFolderOptions? options = null)
   {
      TestSources sources = shop ? await TestSources.SqliteShopAsync() : new TestSources();
      await sources.AddExcelAsync("xl", path, options);
      return sources;
   }

   private static List<string> Strings(TestSources sources, string sql)
   {
      using DuckDBConnection connection = sources.Merge.Connect();
      using DuckDBCommand command = connection.CreateCommand();
      command.CommandText = sql;
      using DbDataReader reader = command.ExecuteReader();
      List<string> values = [];
      while (reader.Read()) { values.Add(reader.GetString(0)); }
      return values;
   }

   /// <summary>The tables of the merge engine's database, as catalog.schema.table.</summary>
   private static List<string> MergeTables(TestSources sources) =>
      Strings(sources, "SELECT database_name || '.' || schema_name || '.' || table_name FROM duckdb_tables() ORDER BY 1");

   private static async Task<(int Exit, string Output, string Error)> GdqAsync(params string[] args)
   {
      using StringWriter output = new();
      using StringWriter error = new();
      int exit = await GdqApp.RunAsync(args, new StringReader(string.Empty), output, error, Token);
      return (exit, output.ToString(), error.ToString());
   }

   // ---------------------------------------------------------------------------------------------------------------
   // 1. Reading cells
   // ---------------------------------------------------------------------------------------------------------------

   /// <summary>1900-system serials before March 1900 count the fictitious 1900-02-29: 1 is 1900-01-01, 59 is 1900-02-28, 61 is 1900-03-01.</summary>
   [Fact]
   public void EarlySerialsOfThe1900SystemAreTheDatesExcelShows()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", [XlsxBuilder.Format(1.0, "yyyy-mm-dd"), XlsxBuilder.Format(59.0, "yyyy-mm-dd"), XlsxBuilder.Format(61.0, "yyyy-mm-dd"),
                                    XlsxBuilder.Format(59.5, "yyyy-mm-dd hh:mm")]).Save(folder.File("b.xlsx"));
      Cells(folder.File("b.xlsx")).ShouldBe("1: A Date 1900-01-01 | B Date 1900-02-28 | C Date 1900-03-01 | D DateTime 1900-02-28 12:00:00");
   }

   /// <summary>Excel's built-in number formats: 14-17 dates, 18-21 times, 22 a date-time, 45 and 47 times, 46 a duration (a number); the others numbers.</summary>
   [Theory]
   [InlineData(0, "None")]
   [InlineData(1, "None")]
   [InlineData(2, "None")]
   [InlineData(9, "None")]
   [InlineData(10, "None")]
   [InlineData(11, "None")]
   [InlineData(12, "None")]
   [InlineData(13, "None")]
   [InlineData(14, "Date")]
   [InlineData(15, "Date")]
   [InlineData(16, "Date")]
   [InlineData(17, "Date")]
   [InlineData(18, "Time")]
   [InlineData(19, "Time")]
   [InlineData(20, "Time")]
   [InlineData(21, "Time")]
   [InlineData(22, "DateTime")]
   [InlineData(37, "None")]
   [InlineData(38, "None")]
   [InlineData(39, "None")]
   [InlineData(40, "None")]
   [InlineData(44, "None")]
   [InlineData(45, "Time")]
   [InlineData(46, "None")]
   [InlineData(47, "Time")]
   [InlineData(48, "None")]
   [InlineData(49, "None")]
   public void ClassifiesBuiltInFormatsById(int id, string kind) => NumberFormats.Classify(id, null).ToString().ShouldBe(kind);

   /// <summary>Custom format codes as real workbooks have them: locales, escapes, padding, accounting, conditions, CJK dates, durations.</summary>
   [Theory]
   [InlineData("[$-409]h:mm:ss AM/PM", "Time")]
   [InlineData("[$-F800]dddd, mmmm dd, yyyy", "Date")]
   [InlineData("[$-F400]h:mm:ss AM/PM", "Time")]
   [InlineData("[$-x-sysdate]dddd, mmmm dd, yyyy", "Date")]
   [InlineData("[$-x-systime]h:mm:ss AM/PM", "Time")]
   [InlineData("yyyy\\-mm\\-dd", "Date")]
   [InlineData("dd/mm/yyyy;@", "Date")]
   [InlineData("m/d/yy h:mm", "DateTime")]
   [InlineData("yyyy-mm-dd\\Thh:mm:ss", "DateTime")]
   [InlineData("mm:ss.0", "Time")]
   [InlineData("h \"hours\"", "Time")]
   [InlineData("mmm", "Date")]
   [InlineData("[Blue]h:mm", "Time")]
   [InlineData("[DBNum1][$-804]yyyy\"年\"m\"月\"d\"日\"", "Date")]
   [InlineData("[$-411]ggge\"年\"m\"月\"d\"日\"", "Date")]
   [InlineData("[ss]", "None")]
   [InlineData("[h]:mm:ss;@", "None")]
   [InlineData("#,##0.00_);[Red](#,##0.00)", "None")]
   [InlineData("_(\"$\"* #,##0.00_);_(\"$\"* \\(#,##0.00\\);_(\"$\"* \"-\"??_);_(@_)", "None")]
   [InlineData("_-* #,##0 _€_-;\\-* #,##0 _€_-;_-* \"-\" _€_-;_-@_-", "None")]
   [InlineData("0.00\" hrs\"", "None")]
   [InlineData("[$€-2] #,##0.00", "None")]
   [InlineData("[$USD] #,##0", "None")]
   [InlineData("# ?/?", "None")]
   [InlineData("[<=9999999]###-####;(###) ###-####", "None")]
   [InlineData("[Red]General", "None")]
   [InlineData("##0.0E+0", "None")]
   public void ClassifiesCustomFormatCodes(string code, string kind) => NumberFormats.ClassifyCode(code).ToString().ShouldBe(kind);

   /// <summary>A cell's style indexes cellXfs, never cellStyleXfs; numFmts of differential formats (dxfs) don't count.</summary>
   [Fact]
   public void StylesIndexTheCellFormatsOnly()
   {
      using TempFolder folder = new();
      const string styles = """
         <numFmts count="1"><numFmt numFmtId="164" formatCode="0.000"/></numFmts>
         <cellStyleXfs count="2"><xf numFmtId="14"/><xf numFmtId="22"/></cellStyleXfs>
         <cellXfs count="3"><xf numFmtId="0" xfId="0"/><xf numFmtId="164" xfId="1"/><xf numFmtId="14" xfId="0"><alignment horizontal="left"/></xf></cellXfs>
         <dxfs count="1"><dxf><numFmt numFmtId="164" formatCode="yyyy-mm-dd"/></dxf></dxfs>
         """;
      Book(folder.File("b.xlsx"), """<row r="1"><c r="A1"><v>45000</v></c><c r="B1" s="1"><v>45000</v></c><c r="C1" s="2"><v>45000</v></c></row>""", styles: styles);
      Cells(folder.File("b.xlsx")).ShouldBe("1: A Number 45000 | B Number 45000 | C Date 2023-03-15");
   }

   /// <summary>Shared strings: runs joined, phonetic hints (rPh, phoneticPr) left out, _xHHHH_ unescaped; an empty item, or one of spaces alone, no value.</summary>
   [Fact]
   public void SharedStringsJoinRunsAndLeaveOutPhoneticHints()
   {
      using TempFolder folder = new();
      const string strings = """
         <si><t>漢字</t><rPh sb="0" eb="2"><t>かんじ</t></rPh><phoneticPr fontId="1" type="noConversion"/></si>
         <si><r><t>a</t></r><r><rPr><b/><sz val="11"/></rPr><t xml:space="preserve"> b</t></r><rPh sb="0" eb="1"><t>x</t></rPh></si>
         <si><t xml:space="preserve">   </t></si>
         <si><t>tab_x0009_end</t></si>
         <si><t/></si>
         <si><r><t>only run</t></r></si>
         """;
      Book(folder.File("b.xlsx"), """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c><c r="D1" t="s"><v>3</v></c><c r="E1" t="s"><v>4</v></c><c r="F1" t="s"><v>5</v></c></row>""",
           sharedStrings: strings);
      Cells(folder.File("b.xlsx")).ShouldBe("1: A Text 漢字 | B Text a b | D Text tab\tend | F Text only run");
   }

   /// <summary>Inline strings with rich-text runs read as their joined text.</summary>
   [Fact]
   public void InlineStringsJoinTheirRuns()
   {
      using TempFolder folder = new();
      Book(folder.File("b.xlsx"), """<row r="1"><c r="A1" t="inlineStr"><is><r><t>x</t></r><r><rPr><i/></rPr><t>y</t></r></is></c><c r="B1" t="inlineStr"><is><t>plain</t></is></c></row>""");
      Cells(folder.File("b.xlsx")).ShouldBe("1: A Text xy | B Text plain");
   }

   /// <summary>A formula's text result is an ST_Xstring like shared strings, escaped the same way (_x000D_), so it reads unescaped too.</summary>
   [Fact]
   public void FormulaTextResultsAreUnescaped()
   {
      using TempFolder folder = new();
      Book(folder.File("b.xlsx"), """<row r="1"><c r="A1" t="str"><f>"a"&amp;CHAR(13)&amp;"b"</f><v>a_x000D_b</v></c></row>""");
      Cells(folder.File("b.xlsx")).ShouldBe("1: A Text a\rb");
   }

   /// <summary>Errors, booleans, formulas without a cached value (no value), an empty formula string (no value), and exponents.</summary>
   [Fact]
   public void ErrorsBooleansAndFormulasWithoutCachedValues()
   {
      using TempFolder folder = new();
      Book(folder.File("b.xlsx"), """<row r="1"><c r="A1" t="e"><f>1/0</f><v>#DIV/0!</v></c><c r="B1"><f>A1</f></c><c r="C1" t="b"><v>1</v></c><c r="D1" t="b"><v>0</v></c><c r="E1" t="str"><f>""</f><v></v></c><c r="F1" t="n"><v>-1.5E-3</v></c></row>""");
      Cells(folder.File("b.xlsx")).ShouldBe("1: A Error #DIV/0! | C Boolean TRUE | D Boolean FALSE | F Number -0.0015");
   }

   /// <summary>Cells and rows without references follow the ones before them, after ones with references too.</summary>
   [Fact]
   public void CellsAndRowsWithoutReferencesFollowTheOnesBefore()
   {
      using TempFolder folder = new();
      Book(folder.File("b.xlsx"), """<row r="3"><c r="C3"><v>1</v></c><c><v>2</v></c></row><row><c><v>3</v></c><c r="E4"><v>4</v></c><c><v>5</v></c></row>""");
      Cells(folder.File("b.xlsx")).ShouldBe("3: C Number 1 | D Number 2" + NL + "4: A Number 3 | E Number 4 | F Number 5");
   }

   /// <summary>Columns as far out as AA and XFD (the last) read, and load.</summary>
   [Fact]
   public async Task VeryWideColumnsReadAndLoad()
   {
      using TempFolder folder = new();
      Book(folder.File("wide.xlsx"), """<row r="1"><c r="AA1" t="inlineStr"><is><t>aa</t></is></c><c r="XFD1" t="inlineStr"><is><t>last</t></is></c></row><row r="2"><c r="AA2"><v>1</v></c><c r="XFD2"><v>2</v></c></row>""");
      Layout(folder.File("wide.xlsx")).ShouldBe("header 1, 1 rows: aa (AA) int64?, last (XFD) int64?");
      await using TestSources sources = await FolderAsync(folder.Path);
      (await RowsAsync(sources.Engine(), "xl.wide.s.select(last, aa)")).ShouldBe(Lines("2 | 1"));
   }

   /// <summary>Strict Open XML (purl.oclc.org namespaces and relationship types) reads: sheets, shared strings, styles, the 1904 system, ISO dates.</summary>
   [Fact]
   public void StrictWorkbooksRead()
   {
      const string strictMain = "http://purl.oclc.org/ooxml/spreadsheetml/main";
      const string strictRel = "http://purl.oclc.org/ooxml/officeDocument/relationships";
      using TempFolder folder = new();
      Package(folder.File("strict.xlsx"),
         ("_rels/.rels", Rels(("rId1", strictRel + "/officeDocument", "xl/workbook.xml"))),
         ("xl/workbook.xml", $"<workbook xmlns=\"{strictMain}\" xmlns:r=\"{strictRel}\"><workbookPr date1904=\"true\"/><sheets><sheet name=\"data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
         ("xl/_rels/workbook.xml.rels", Rels(("rId1", strictRel + "/worksheet", "worksheets/sheet1.xml"), ("rId2", strictRel + "/sharedStrings", "sharedStrings.xml"), ("rId3", strictRel + "/styles", "styles.xml"))),
         ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{strictMain}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"d\"><v>2024-01-15</v></c><c r=\"C1\" s=\"1\"><v>0</v></c></row></sheetData></worksheet>"),
         ("xl/sharedStrings.xml", $"<sst xmlns=\"{strictMain}\"><si><t>name</t></si></sst>"),
         ("xl/styles.xml", $"<styleSheet xmlns=\"{strictMain}\"><cellXfs count=\"2\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>"));
      using (Workbook workbook = Workbook.Open(folder.File("strict.xlsx")))
      {
         workbook.Date1904.ShouldBeTrue();
      }
      Cells(folder.File("strict.xlsx"), "data").ShouldBe("1: A Text name | B Date 2024-01-15 | C Date 1904-01-01");
   }

   /// <summary>Relationship targets that are absolute (/xl/...) or relative with .. and . resolve to their parts.</summary>
   [Fact]
   public void AbsoluteAndParentRelativeTargetsResolve()
   {
      using TempFolder folder = new();
      Package(folder.File("b.xlsx"),
         ("_rels/.rels", Rels(("rId1", Rel + "/officeDocument", "/xl/workbook.xml"))),
         ("xl/workbook.xml", WorkbookXml("one", "two")),
         ("xl/_rels/workbook.xml.rels", Rels(("rId1", Rel + "/worksheet", "/xl/worksheets/sheet1.xml"), ("rId2", Rel + "/worksheet", "../xl/worksheets/./sheet2.xml"))),
         ("xl/worksheets/sheet1.xml", Sheet(InlineRow(1, "first"))),
         ("xl/worksheets/sheet2.xml", Sheet(InlineRow(1, "second"))));
      Cells(folder.File("b.xlsx"), "one").ShouldBe("1: A Text first");
      Cells(folder.File("b.xlsx"), "two").ShouldBe("1: A Text second");
   }

   /// <summary>A %-encoded target names a ZIP item written unescaped (UTF-8 names), as the reader expects.</summary>
   [Fact]
   public void PercentEncodedTargetsFindUnescapedItems()
   {
      using TempFolder folder = new();
      Package(folder.File("b.xlsx"),
         ("_rels/.rels", Rels(("rId1", Rel + "/officeDocument", "xl/workbook.xml"))),
         ("xl/workbook.xml", WorkbookXml("plain")),
         ("xl/_rels/workbook.xml.rels", Rels(("rId1", Rel + "/worksheet", "worksheets/bl%C3%A4tt%201.xml"))),
         ("xl/worksheets/blätt 1.xml", Sheet(InlineRow(1, "p"))));
      Cells(folder.File("b.xlsx"), "plain").ShouldBe("1: A Text p");
   }

   /// <summary>
   /// Part names are URIs (OPC): a part named /xl/worksheets/sheet%201.xml is the ZIP item xl/worksheets/sheet%201.xml,
   /// as System.IO.Packaging (and tools built on it) write it; its sheet must read, not come out empty.
   /// </summary>
   [Fact]
   public void PercentEncodedTargetsFindItemsNamedTheSameWay()
   {
      using TempFolder folder = new();
      Package(folder.File("b.xlsx"),
         ("_rels/.rels", Rels(("rId1", Rel + "/officeDocument", "xl/workbook.xml"))),
         ("xl/workbook.xml", WorkbookXml("encoded")),
         ("xl/_rels/workbook.xml.rels", Rels(("rId1", Rel + "/worksheet", "worksheets/sheet%201.xml"))),
         ("xl/worksheets/sheet%201.xml", Sheet(InlineRow(1, "e"))));
      Cells(folder.File("b.xlsx"), "encoded").ShouldBe("1: A Text e");
   }

   /// <summary>Elements written with a namespace prefix (x:row, x:c, as some generators write them) read as unprefixed ones do.</summary>
   [Fact]
   public void PrefixedElementsRead()
   {
      using TempFolder folder = new();
      Book(folder.File("b.xlsx"),
           [("s", $"<x:worksheet xmlns:x=\"{Main}\"><x:sheetData><x:row r=\"1\"><x:c r=\"A1\" t=\"s\"><x:v>0</x:v></x:c><x:c r=\"B1\" t=\"inlineStr\"><x:is><x:t>i</x:t></x:is></x:c><x:c r=\"C1\" s=\"1\"><x:v>45000</x:v></x:c></x:row></x:sheetData></x:worksheet>")],
           sharedStrings: $"<x:sst xmlns:x=\"{Main}\"><x:si><x:r><x:t>sh</x:t></x:r><x:r><x:t>ared</x:t></x:r></x:si></x:sst>",
           styles: $"<x:styleSheet xmlns:x=\"{Main}\"><x:cellXfs count=\"2\"><x:xf numFmtId=\"0\"/><x:xf numFmtId=\"14\"/></x:cellXfs></x:styleSheet>");
      Cells(folder.File("b.xlsx")).ShouldBe("1: A Text shared | B Text i | C Date 2023-03-15");
   }

   /// <summary>ZIP item names match their relationship targets in any case.</summary>
   [Fact]
   public void PartNamesMatchInAnyCase()
   {
      using TempFolder folder = new();
      Package(folder.File("b.xlsx"),
         ("_RELS/.RELS", Rels(("rId1", Rel + "/officeDocument", "xl/workbook.xml"))),
         ("XL/WORKBOOK.XML", WorkbookXml("s")),
         ("XL/_RELS/WORKBOOK.XML.RELS", Rels(("rId1", Rel + "/worksheet", "worksheets/sheet1.xml"))),
         ("XL/WORKSHEETS/SHEET1.XML", Sheet(InlineRow(1, "upper"))));
      Cells(folder.File("b.xlsx")).ShouldBe("1: A Text upper");
   }

   /// <summary>A t="d" cell holding an ISO time alone is a time of day, not a date-time on whatever day it happens to be read.</summary>
   [Fact]
   public void IsoTimeCellsAreTimesOfDay()
   {
      using TempFolder folder = new();
      Book(folder.File("b.xlsx"), """<row r="1"><c r="A1" t="d"><v>10:30:00</v></c><c r="B1" t="d"><v>2024-01-15T10:30:00</v></c></row>""");
      Cells(folder.File("b.xlsx")).ShouldBe("1: A Time 10:30:00 | B DateTime 2024-01-15 10:30:00");
   }

   private const string Laughs =
      """<!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"><!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;"><!ENTITY lol4 "&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;"><!ENTITY lol5 "&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;"><!ENTITY lol6 "&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;"><!ENTITY lol7 "&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;"><!ENTITY lol8 "&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;"><!ENTITY lol9 "&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;"><!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>""";

   /// <summary>DTDs (entity expansion, external entities) in a sheet, the shared strings or the workbook part are refused: a warning, nothing expanded, the rest read.</summary>
   [Fact]
   public async Task DocumentTypeDefinitionsAreRefusedWithAWarning()
   {
      using TempFolder folder = new();
      Book(folder.File("sheetdtd.xlsx"), [("bad", "<?xml version=\"1.0\"?>" + Laughs + Sheet(InlineRow(1, "&lol9;&xxe;"))), ("ok", Sheet(InlineRow(1, "fine")))]);
      Book(folder.File("stringsdtd.xlsx"), [("s", Sheet("""<row r="1"><c r="A1" t="s"><v>0</v></c></row>"""))],
           sharedStrings: "<?xml version=\"1.0\"?>" + Laughs + $"<sst xmlns=\"{Main}\"><si><t>&lol9;</t></si></sst>");
      Book(folder.File("bookdtd.xlsx"), [("s", Sheet(InlineRow(1, "x")))], workbook: "<?xml version=\"1.0\"?>" + Laughs + WorkbookXml("s"));
      new XlsxBuilder().Sheet("s", ["a"], [1]).Save(folder.File("good.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path);
      SourceSchema schema = sources.Schemas["xl"];
      schema.Tables.Select(t => $"{t.Schema}.{t.Name}").ShouldBe(["good.s", "sheetdtd.ok"]);
      string warnings = string.Join(NL, schema.Warnings ?? []);
      warnings.ShouldContain("bookdtd.xlsx");
      warnings.ShouldContain("'bad' of 'sheetdtd.xlsx'");
      warnings.ShouldContain("stringsdtd.xlsx");
      warnings.ShouldNotContain("lollollol");
   }

   // ---------------------------------------------------------------------------------------------------------------
   // 2. Headings and types
   // ---------------------------------------------------------------------------------------------------------------

   /// <summary>Headings that are numbers, dates or booleans are named as they read; headings on several lines are one line.</summary>
   [Fact]
   public void HeadingsThatAreNumbersDatesOrBooleansReadAsTheyShow()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", [2024, new DateOnly(2024, 1, 15), true, 1.5, "  spaced\r\nheading "], [1, 2, 3, 4, 5]).Save(folder.File("b.xlsx"));
      Layout(folder.File("b.xlsx")).ShouldBe("header 1, 1 rows: 2024 (A) int64?, 2024-01-15 (B) int64?, TRUE (C) int64?, 1.5 (D) int64?, spaced heading (E) int64?");
   }

   /// <summary>An error is no value (plan 4.9), so a heading that is one is blank, and its column is named by its letter.</summary>
   [Fact]
   public void AHeadingThatIsAnErrorIsBlank()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["a", XlsxBuilder.Error("#REF!")], [1, 2]).Save(folder.File("b.xlsx"));
      Layout(folder.File("b.xlsx")).ShouldBe("header 1, 1 rows: a (A) int64?, B (B) int64?");
   }

   /// <summary>Headings keep their names: a heading a_2 keeps it, and a repeated 'a' takes the next free suffix.</summary>
   [Fact]
   public void AHeadingThatLooksLikeASuffixKeepsItsName()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["a", "A", "a_2"], [1, 2, 3]).Save(folder.File("b.xlsx"));
      Layout(folder.File("b.xlsx")).ShouldBe("header 1, 1 rows: a (A) int64?, A_3 (B) int64?, a_2 (C) int64?");
   }

   /// <summary>Whole numbers past 2^53 make a column of doubles; negative and large whole numbers within it stay int64, and load exactly.</summary>
   [Fact]
   public async Task WholeNumbersPast2To53AreDoublesAndOthersStayWhole()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["big", "neg", "frac", "edge"], [9007199254740994.0, -5, -0.5, 9007199254740992.0], [1, -3000000000L, 2, -9007199254740992.0]).Save(folder.File("n.xlsx"));
      Layout(folder.File("n.xlsx")).ShouldBe("header 1, 2 rows: big (A) double?, neg (B) int64?, frac (C) double?, edge (D) int64?");
      await using TestSources sources = await FolderAsync(folder.Path);
      Typed(await ValuesAsync(sources.Engine(), "xl.n.s.select(neg, edge).orderBy(neg)"))
         .ShouldBe("Int64:-3000000000 | Int64:-9007199254740992" + NL + "Int64:-5 | Int64:9007199254740992");
   }

   /// <summary>A cell of spaces alone (a common leftover of clearing a cell) is no value, as an empty string is, and doesn't make a column of numbers text.</summary>
   [Fact]
   public void ACellOfSpacesAloneDoesntMakeNumbersText()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["n"], [1], [" "], [2]).Save(folder.File("b.xlsx"));
      Layout(folder.File("b.xlsx")).ShouldBe("header 1, 2 rows: n (A) int64?");
   }

   /// <summary>Rows without a value are left out (plan 4.9), and errors are no value: a row of errors alone isn't a row of the table.</summary>
   [Fact]
   public async Task RowsOfErrorsAloneAreLeftOut()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["n", "m"], [1, 2], [XlsxBuilder.Error("#N/A"), XlsxBuilder.Error("#REF!")], [3, 4]).Save(folder.File("e.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path);
      (await RowsAsync(sources.Engine(), "xl.e.s.select(n, m)")).ShouldBe(Lines("1 | 2", "3 | 4"));
      Layout(folder.File("e.xlsx")).ShouldBe("header 1, 2 rows: n (A) int64?, m (B) int64?");
   }

   /// <summary>Without a header row every row is data, and columns are named by their letters.</summary>
   [Fact]
   public async Task WithoutAHeaderRowEveryRowIsData()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["x", 1], ["y", 2]).Save(folder.File("h.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path, options: new ExcelFolderOptions { Path = folder.Path, HeaderRow = false });
      (await RowsAsync(sources.Engine(), "xl.h.s.select(A, B).orderBy(A)")).ShouldBe(Lines("'x' | 1", "'y' | 2"));
   }

   /// <summary>AllText reads numbers as they read, dates and times in ISO form, booleans as TRUE/FALSE, errors as no value.</summary>
   [Fact]
   public async Task AllTextReadsValuesAsText()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["n", "d", "b", "t", "e"],
            [12, new DateOnly(2024, 1, 15), true, new TimeOnly(8, 30), XlsxBuilder.Error("#N/A")],
            [0.1, new DateTime(2024, 1, 15, 10, 0, 0), false, null, "x"])
         .Save(folder.File("a.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path, options: new ExcelFolderOptions { Path = folder.Path, AllText = true });
      (await RowsAsync(sources.Engine(), "xl.a.s.select(n, d, b, t, e).orderBy(n)"))
         .ShouldBe(Lines("'0.1' | '2024-01-15 10:00:00' | 'FALSE' | null | 'x'", "'12' | '2024-01-15' | 'TRUE' | '08:30:00' | null"));
   }

   // ---------------------------------------------------------------------------------------------------------------
   // 3. Conversion to the catalog's types
   // ---------------------------------------------------------------------------------------------------------------

   /// <summary>A number too big for an interval is a conversion error naming the cell, not an OverflowException out of the load.</summary>
   [Fact]
   public void AnIntervalTooLongIsAnErrorNotAnException()
   {
      bool converted = true;
      Should.NotThrow(() => { converted = CellConversion.TryConvert(CellValue.OfNumber(1e20), ScalarType.Interval, false, out object? _, out string? _); });
      converted.ShouldBeFalse();
   }

   /// <summary>Text reads as a number only in its invariant form (the class's contract): "1,5" (a decimal comma) isn't fifteen.</summary>
   [Theory]
   [InlineData("double")]
   [InlineData("decimal(10,2)")]
   public void TextWithACommaIsntReadAsThousands(string type)
   {
      bool converted = CellConversion.TryConvert(CellValue.OfText("1,5"), ScalarType.Parse(type), false, out object? result, out _);
      (converted ? Convert.ToString(result, CultureInfo.InvariantCulture) : "error").ShouldBe("error");
   }

   /// <summary>A number read as a date is a serial date, as a date-formatted cell's is: 1 is 1900-01-01 and 59 is 1900-02-28.</summary>
   [Fact]
   public void NumbersReadAsDatesAreSerialDates()
   {
      CellConversion.TryConvert(CellValue.OfNumber(1), ScalarType.Date, false, out object? first, out _).ShouldBeTrue();
      first.ShouldBe(new DateOnly(1900, 1, 1));
      CellConversion.TryConvert(CellValue.OfNumber(59), ScalarType.Date, false, out object? early, out _).ShouldBeTrue();
      early.ShouldBe(new DateOnly(1900, 2, 28));
   }

   /// <summary>ISO text with an offset is an instant, the same on every machine (as t="d" cells are read), never shifted by the machine's time zone.</summary>
   [Fact]
   public void IsoTextWithAnOffsetDoesntDependOnTheMachinesTimeZone()
   {
      CellConversion.TryConvert(CellValue.OfText("2024-01-15T10:00:00+05:00"), ScalarType.DateTimeOffset, false, out object? instant, out _).ShouldBeTrue();
      ((DateTimeOffset)instant!).UtcDateTime.ShouldBe(new DateTime(2024, 1, 15, 5, 0, 0));
      CellConversion.TryConvert(CellValue.OfText("2024-01-15T10:00:00Z"), ScalarType.DateTime, false, out object? moment, out _).ShouldBeTrue();
      moment.ShouldBe(new DateTime(2024, 1, 15, 10, 0, 0));
   }

   /// <summary>Text reads as a date in its ISO form (the class's contract): 01/02/2024 is ambiguous (1 February in most of the world) and isn't read as 2 January.</summary>
   [Fact]
   public void SlashedDateTextIsntReadAsAnAmericanDate()
   {
      bool converted = CellConversion.TryConvert(CellValue.OfText("01/02/2024"), ScalarType.Date, false, out object? result, out _);
      (converted ? Convert.ToString(result, CultureInfo.InvariantCulture) : "error").ShouldBe("error");
   }

   /// <summary>Every kind a column can have after an override converts a matching cell, or fails with a message (never an exception).</summary>
   [Theory]
   [InlineData("int16", 12.0, "Int16:12")]
   [InlineData("int32", -12.0, "Int32:-12")]
   [InlineData("single", 1.5, "Single:1.5")]
   [InlineData("interval", 1.5, "TimeSpan:1.12:00:00")]
   [InlineData("datetimeoffset", 45000.5, "DateTimeOffset:2023-03-15T12:00:00.0000000+00:00")]
   [InlineData("json", 1.0, "String:1")]
   [InlineData("binary", 1.0, "error")]
   [InlineData("guid", 1.0, "error")]
   [InlineData("boolean", "TRUE", "Boolean:True")]
   [InlineData("time", "1:30 PM", "error")]
   [InlineData("interval", "1.02:03:04", "TimeSpan:1.02:03:04")]
   public void ConvertsToEveryKind(string type, object cell, string expected)
   {
      CellValue value = cell is string text ? CellValue.OfText(text) : CellValue.OfNumber(Convert.ToDouble(cell, CultureInfo.InvariantCulture));
      string actual = "error";
      Should.NotThrow(() => { actual = CellConversion.TryConvert(value, ScalarType.Parse(type), false, out object? result, out _) ? Typed([[result]]) : "error"; });
      actual.ShouldBe(expected);
   }

   // ---------------------------------------------------------------------------------------------------------------
   // 4. Loading and loading again
   // ---------------------------------------------------------------------------------------------------------------

   /// <summary>A sheet loaded leniently (bad values null) mustn't serve a strict query: that one must fail naming the cell.</summary>
   [Fact]
   public async Task ALenientLoadDoesntServeAStrictQuery()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      shop.WriteCustomers(["id", "name", "tier"], [1, "Acme Ltd", "gold"], ["n/a", "Beta Corp", "silver"]);
      const string query = "xl.customers.list.select(id, name).orderBy(name)";
      (await RowsAsync(shop.Sources.Engine(options: new QueryEngineOptions { LenientConversion = true }), query)).ShouldBe(Lines("1 | 'Acme Ltd'", "null | 'Beta Corp'"));
      (await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(shop.Sources.Engine(), query))).Message.ShouldContain("(cell A3)");
   }

   /// <summary>A load that fails on a value leaves no staging table behind, and a later good load works.</summary>
   [Fact]
   public async Task AFailedLoadLeavesNoStagingTable()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      shop.WriteCustomers(["id", "name", "tier"], [1, "Acme Ltd", "gold"], ["n/a", "Beta Corp", "silver"]);
      await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(shop.Sources.Engine(), "xl.customers.list.select(id)"));
      MergeTables(shop.Sources).ShouldNotContain(t => t.Contains('[', StringComparison.Ordinal));
      shop.WriteCustomers(ExcelShop.CustomerRows);
      (await RowsAsync(shop.Sources.Engine(), "xl.customers.list.select(id).orderBy(id)")).ShouldBe(Lines("1", "2", "4"));
   }

   /// <summary>Introspection loads nothing, and neither does preparing or explaining a query.</summary>
   [Fact]
   public async Task IntrospectionAndExplainLoadNothing()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      MergeTables(shop.Sources).ShouldNotContain(t => t.StartsWith("excel_", StringComparison.Ordinal));
      shop.Sources.Engine().Prepare(Budget + ".join(shop.customers, outer.customer_id == inner.id, b: outer, c: inner).select(c.name)").Explain(verbose: true).Fragments.Count.ShouldBe(2);
      MergeTables(shop.Sources).ShouldNotContain(t => t.StartsWith("excel_", StringComparison.Ordinal));
   }

   /// <summary>A workbook another program holds open without sharing is left out of the schema with a warning, and fails a query with a message.</summary>
   [Fact]
   public async Task AWorkbookHeldOpenIsAWarningAndAQueryFailure()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      DateTime before = File.GetLastWriteTimeUtc(shop.Customers);
      using (FileStream held = new(shop.Customers, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      {
         (await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(shop.Sources.Engine(), "xl.customers.list.select(id)"))).Message.ShouldStartWith("xl: 'customers.xlsx' can't be read: ");
         SourceSchema schema = await shop.Sources.Excel.IntrospectAsync("xl", cancellationToken: Token);
         (schema.Warnings ?? []).ShouldContain(w => w.StartsWith("'customers.xlsx' can't be read: ", StringComparison.Ordinal));
      }
      File.SetLastWriteTimeUtc(shop.Customers, before);
   }

   /// <summary>A virtual entity of the overlay over a sheet loads the sheet it reads.</summary>
   [Fact]
   public async Task AVirtualEntityOverASheetLoadsIt()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      CatalogOverlay overlay = new() { VirtualEntities = [new OverlayVirtualEntity("reports.big", "xl.customers.list.where(id > 1).select(id, name)")] };
      shop.Sources.Catalog(overlay).Diagnostics.ShouldBeEmpty();
      QueryEngine engine = shop.Sources.Engine(overlay);
      (await RowsAsync(engine, "reports.big.select(name).orderBy(name)")).ShouldBe(Lines("'Beta Corp'", "'Delta'"));
      (await RowsAsync(engine, "reports.big.join(shop.customers, outer.id == inner.id, r: outer, c: inner).select(c.name)")).ShouldBe(Lines("'Beta Corp'"));
   }

   /// <summary>A column overridden to decimal without a precision (a double in DuckDB) loads its values.</summary>
   [Fact]
   public async Task ADecimalColumnWithoutAPrecisionLoads()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine(Override("xl[\"Budget 2024\"][\"Sheet 1\"]", ("amount", ScalarType.Decimal())));
      IReadOnlyList<object?[]> rows = await ValuesAsync(engine, Budget + ".select(amount).orderBy(amount)");
      rows.Select(r => Convert.ToDecimal(r[0], CultureInfo.InvariantCulture)).ShouldBe([300m, 450.25m, 800m, 1200.5m, 2000m]);
      MergeTables(shop.Sources).ShouldNotContain(t => t.Contains('[', StringComparison.Ordinal));
   }

   /// <summary>A number stored into decimal(10,2) rounds as SQL (and Excel's ROUND) does: 0.125 is 0.13, 0.375 is 0.38, never truncated.</summary>
   [Fact]
   public async Task DecimalsRoundToTheirScale()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["id", "d"], [1, 0.125], [2, 0.375], [3, -0.125]).Save(folder.File("d.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path);
      QueryEngine engine = sources.Engine(Override("xl.d.s", ("d", ScalarType.Decimal(10, 2))));
      IReadOnlyList<object?[]> rows = await ValuesAsync(engine, "xl.d.s.orderBy(id).select(d)");
      rows.Select(r => (decimal)r[0]!).ShouldBe([0.13m, 0.38m, -0.13m]);
   }

   /// <summary>For comparison, outside M8: a SQLite decimal(10,2) holding 0.125 (SQLite keeps what it is given) fetched into the merge engine rounds too.</summary>
   [Fact]
   public async Task SqliteDecimalsRoundToTheirScaleInTheMergeEngine()
   {
      await using TestSources sources = await new TestSources().AddSqliteAsync("db", "CREATE TABLE t (id INTEGER PRIMARY KEY, d DECIMAL(10,2)); INSERT INTO t VALUES (1, 0.125), (2, 0.375);");
      await sources.AddDuckDbAsync("dk", "CREATE TABLE k (id INTEGER); INSERT INTO k VALUES (1), (2);");
      IReadOnlyList<object?[]> rows = await ValuesAsync(sources.Engine(), "db.t.join(dk.k, outer.id == inner.id, t: outer, k: inner).select(id: t.id, d: t.d).orderBy(id)");
      rows.Select(r => (decimal)r[1]!).ShouldBe([0.13m, 0.38m]);
   }

   /// <summary>In a 1904 workbook, a number read as a date (a column overridden to date) is a 1904 serial, as a date-formatted cell is.</summary>
   [Fact]
   public async Task NumbersReadAsDatesUseTheWorkbooksDateSystem()
   {
      using TempFolder folder = new();
      new XlsxBuilder { Date1904 = true }.Sheet("s", ["n", "shown"], [45000, new DateOnly(2027, 3, 16)]).Save(folder.File("mac.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path);
      QueryEngine engine = sources.Engine(Override("xl.mac.s", ("n", ScalarType.Date)));
      IReadOnlyList<object?[]> rows = await ValuesAsync(engine, "xl.mac.s.select(n, shown)");
      rows.Single()[1].ShouldBe(new DateOnly(2027, 3, 16));
      rows.Single()[0].ShouldBe(DateOnly.FromDateTime(DateTime.FromOADate(45000 + 1462)));
   }

   /// <summary>A count, as the first query of a sheet, loads it.</summary>
   [Fact]
   public async Task ACountLoadsTheSheet()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      await using QueryResult count = await shop.Sources.Engine().Prepare("xl.customers.list.where(id > 1)").ForCount().ExecuteAsync(Token);
      (await TestSources.RowsAsync(count)).ShouldBe(Lines("2"));
   }

   /// <summary>A page of a sheet (no key to stabilize the order with) is the rows of the page.</summary>
   [Fact]
   public async Task APageOfASheet()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      await using QueryResult page = await shop.Sources.Engine().ExecuteAsync(new QueryRequest("xl.customers.list.orderBy(tier).select(name)") { Paging = new PageRequest(1, 2) }, Token);
      (await TestSources.RowsAsync(page)).ShouldBe(Lines("'Acme Ltd'", "'Beta Corp'"));
   }

   /// <summary>A statement reading two workbooks, and two sheets of one workbook (one in a subquery), loads them all.</summary>
   [Fact]
   public async Task AStatementReadingSeveralSheetsLoadsThemAll()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      (await RowsAsync(engine, "xl.customers.list.join(" + Budget + ", outer.id == inner.customer_id, c: outer, b: inner).groupBy(c.name).select(name: c.name, total: b.sum(amount)).orderBy(name)"))
         .ShouldBe(Lines("'Acme Ltd' | 1500.5", "'Beta Corp' | 800"));
      (await RowsAsync(engine, Budget + ".where(amount > xl[\"Budget 2024\"].Q2.max(target) / 10).select(dept, amount).orderBy(amount)"))
         .ShouldBe(Lines("'Sales' | 800", "'Sales' | 1200.5", "'R&D' | 2000"));
   }

   /// <summary>Two folders in one query: each fragment is copied inside the merge engine, from its own catalog.</summary>
   [Fact]
   public async Task TwoFoldersInOneQuery()
   {
      using TempFolder a = new();
      using TempFolder b = new();
      new XlsxBuilder().Sheet("s", ["k", "v"], [1, "a1"], [2, "a2"]).Save(a.File("book.xlsx"));
      new XlsxBuilder().Sheet("s", ["k", "w"], [2, "b2"], [3, "b3"]).Save(b.File("book.xlsx"));
      await using TestSources sources = await TestSources.SqliteShopAsync();
      await sources.AddExcelAsync("xa", a.Path);
      await sources.AddExcelAsync("xb", b.Path);
      QueryEngine engine = sources.Engine();
      const string query = "xa.book.s.join(xb.book.s, outer.k == inner.k, x: outer, y: inner).select(x.v, y.w)";
      engine.Prepare(query).Fragments.Select(f => $"{f.Source.Alias} {f.InMergeEngine}").Order().ShouldBe(["xa True", "xb True"]);
      (await RowsAsync(engine, query)).ShouldBe(Lines("'a2' | 'b2'"));
   }

   /// <summary>One folder under two aliases: two catalogs, each working, and removing one leaves the other.</summary>
   [Fact]
   public async Task OneFolderUnderTwoAliases()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      await shop.Sources.AddExcelAsync("xl2", shop.Folder.Path);
      QueryEngine engine = shop.Sources.Engine();
      (await RowsAsync(engine, "xl.customers.list.join(xl2.customers.list, outer.id == inner.id, a: outer, b: inner).select(a.name, b.tier).orderBy(name)"))
         .ShouldBe(Lines("'Acme Ltd' | 'gold'", "'Beta Corp' | 'silver'", "'Delta' | 'bronze'"));
      shop.Sources.Excel.RemoveFolder("xl2").ShouldBeTrue();
      (await RowsAsync(engine, "xl.customers.list.select(id).orderBy(id)")).ShouldBe(Lines("1", "2", "4"));
   }

   /// <summary>Folders whose aliases differ only in case are two folders: DuckDB's catalog names are case-insensitive, so they mustn't share one.</summary>
   [Fact]
   public async Task AliasesThatDifferInCaseDontShareACatalog()
   {
      using TempFolder a = new();
      using TempFolder b = new();
      new XlsxBuilder().Sheet("s", ["v"], ["from a"]).Save(a.File("book.xlsx"));
      new XlsxBuilder().Sheet("s", ["v"], ["from b"]).Save(b.File("book.xlsx"));
      await using TestSources sources = await FolderAsync(a.Path);
      ExcelSourceProvider excel = sources.Excel;
      excel.AddFolder("XL", new ExcelFolderOptions { Path = b.Path });
      SourceSchema other = await excel.IntrospectAsync("XL", cancellationToken: Token);
      QueryEngine upper = new(new CatalogBuilder().AddSource(excel.Source("XL"), other).Build(), sources, [excel], sources.Merge);
      QueryEngine lower = sources.Engine();
      (await RowsAsync(lower, "xl.book.s.select(v)")).ShouldBe(Lines("'from a'"));
      (await RowsAsync(upper, "XL.book.s.select(v)")).ShouldBe(Lines("'from b'"));
      (await RowsAsync(lower, "xl.book.s.select(v)")).ShouldBe(Lines("'from a'"));
      excel.RemoveFolder("XL");
      (await RowsAsync(lower, "xl.book.s.select(v)")).ShouldBe(Lines("'from a'"));
   }

   /// <summary>A folder removed and added again works again, loading its sheets afresh.</summary>
   [Fact]
   public async Task AFolderRemovedAndAddedAgainWorks()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      (await RowsAsync(engine, "xl.customers.list.select(id).orderBy(id)")).ShouldBe(Lines("1", "2", "4"));
      shop.Sources.Excel.RemoveFolder("xl").ShouldBeTrue();
      shop.Sources.Excel.AddFolder("xl", new ExcelFolderOptions { Path = shop.Folder.Path });
      (await RowsAsync(engine, "xl.customers.list.select(id).orderBy(id)")).ShouldBe(Lines("1", "2", "4"));
   }

   /// <summary>A query of a folder that was removed fails as queries do (a QueryExecutionException), not with an InvalidOperationException.</summary>
   [Fact]
   public async Task AQueryOfARemovedFolderFailsAsAQuery()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      shop.Sources.Excel.RemoveFolder("xl");
      await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(shop.Sources.Engine(), "xl.customers.list.select(id)"));
   }

   /// <summary>Disposing the provider detaches its catalogs from the merge engine's database.</summary>
   [Fact]
   public async Task DisposingTheProviderDetachesItsCatalogs()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      await RowsAsync(shop.Sources.Engine(), "xl.customers.list.select(id)");
      Strings(shop.Sources, "SELECT database_name FROM duckdb_databases()").ShouldContain("excel_xl");
      shop.Sources.Excel.Dispose();
      Strings(shop.Sources, "SELECT database_name FROM duckdb_databases()").ShouldNotContain("excel_xl");
   }

   /// <summary>A load cancelled part way leaves no staging table, and the sheet loads later.</summary>
   [Fact]
   public async Task ACancelledLoadLeavesNoStagingTable()
   {
      using TempFolder folder = new();
      BigBook(folder.File("big.xlsx"), 150_000, 0);
      await using TestSources sources = await FolderAsync(folder.Path);
      QueryEngine engine = sources.Engine();
      using (CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(150)))
      {
         try
         {
            await using QueryResult result = await engine.Prepare("xl.big.s.count()").ExecuteAsync(cancel.Token);
         }
         catch (OperationCanceledException)
         {
            // Cancelled, as asked.
         }
      }
      MergeTables(sources).ShouldNotContain(t => t.Contains('[', StringComparison.Ordinal));
      (await RowsAsync(engine, "xl.big.s.count()")).ShouldBe(Lines("150000"));
   }

   /// <summary>A query reading a sheet goes on seeing its rows while the sheet is loaded again for another (plan 4.9).</summary>
   [Fact]
   public async Task AQueryGoesOnSeeingItsRowsWhileTheSheetIsLoadedAgain()
   {
      using TempFolder folder = new();
      string path = folder.File("big.xlsx");
      BigBook(path, 20_000, 0);
      await using TestSources sources = await FolderAsync(folder.Path);
      QueryEngine engine = sources.Engine();
      await using QueryResult first = await engine.Prepare("xl.big.s.select(n)").ExecuteAsync(Token);
      (await first.ReadAsync(Token)).ShouldBeTrue();
      DateTime before = File.GetLastWriteTimeUtc(path);
      File.Delete(path);
      BigBook(path, 20_000, 1_000_000);
      File.SetLastWriteTimeUtc(path, before.AddMinutes(1));
      (await RowsAsync(engine, "xl.big.s.where(n == 1000000).select(n)")).ShouldBe(Lines("1000000"));
      long rows = 1;
      while (await first.ReadAsync(Token))
      {
         Convert.ToInt64(first.Current[0], CultureInfo.InvariantCulture).ShouldBeLessThan(1_000_000);
         rows++;
      }
      rows.ShouldBe(20_000);
   }

   /// <summary>Sheets of several workbooks load at once, from queries running together.</summary>
   [Fact]
   public async Task SheetsOfSeveralWorkbooksLoadAtOnce()
   {
      using TempFolder folder = new();
      for (int b = 0; b < 4; b++)
      {
         XlsxBuilder builder = new();
         for (int s = 0; s < 4; s++) { builder.Sheet("s" + s.ToString(CultureInfo.InvariantCulture), ["n"], [b * 10 + s]); }
         builder.Save(folder.File($"b{b}.xlsx"));
      }
      await using TestSources sources = await FolderAsync(folder.Path);
      QueryEngine engine = sources.Engine();
      string[] results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => RowsAsync(engine, $"xl.b{i / 4}.s{i % 4}.select(n)"), Token)));
      results.ShouldBe([.. Enumerable.Range(0, 16).Select(i => Lines((i / 4 * 10 + i % 4).ToString(CultureInfo.InvariantCulture)))]);
   }

   /// <summary>Queries while the workbook is replaced again and again (as Excel saves: a new file renamed over the old) see one version or the other, never an error.</summary>
   [Fact]
   public async Task QueriesWhileTheWorkbookIsReplacedSeeOneVersionOrTheOther()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      const string query = "xl.customers.list.select(id).orderBy(id)";
      string[] valid = [Lines("1", "2", "4"), Lines("1", "2", "4", "5")];
      DateTime start = File.GetLastWriteTimeUtc(shop.Customers);
      Task writer = Task.Run(async () =>
      {
         for (int version = 1; version <= 8; version++)
         {
            string temp = shop.Folder.File($"next{version}.tmp");
            new XlsxBuilder().Sheet("list", version % 2 == 1 ? [.. ExcelShop.CustomerRows, [5, "Epsilon", "gold"]] : ExcelShop.CustomerRows).Save(temp);
            File.SetLastWriteTimeUtc(temp, start.AddMinutes(version));
            // As Excel saves: the old file renamed away (a reader may have it open, sharing delete), the new one renamed in.
            string old = shop.Folder.File($"old{version}.tmp");
            File.Move(shop.Customers, old);
            File.Move(temp, shop.Customers);
            File.Delete(old);
            await Task.Delay(40, Token);
         }
      }, Token);
      Task<string[]>[] readers = [.. Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
      {
         List<string> seen = [];
         for (int i = 0; i < 15; i++)
         {
            try
            {
               seen.Add(await RowsAsync(engine, query));
            }
            catch (QueryExecutionException e) when (e.Message.Contains("is no longer in", StringComparison.Ordinal))
            {
               // The instant between the renames, when there is no file of that name.
            }
         }
         return seen.ToArray();
      }, Token))];
      await writer;
      foreach (string rows in (await Task.WhenAll(readers)).SelectMany(r => r)) { valid.ShouldContain(rows); }
   }

   /// <summary>
   /// Engines whose catalogs type a column differently (an overlay, a refreshed schema) take turns loading it; each
   /// query must read the column as its own catalog types it. A statement holds the sheet as loaded from when it is
   /// readied until it has started, its own or a fragment's in the merge engine.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task EnginesTypingAColumnDifferentlyEachReadTheirOwnType(bool pushDown)
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine text = shop.Sources.Engine(Override("xl.customers.list", ("id", ScalarType.Text())), new QueryEngineOptions { PushDown = pushDown });
      QueryEngine numbers = shop.Sources.Engine();
      async Task<List<string>> Run(QueryEngine engine, string query)
      {
         List<string> seen = [];
         for (int i = 0; i < 25; i++) { seen.Add(await RowsAsync(engine, query)); }
         return seen;
      }
      Task<List<string>>[] texts = [.. Enumerable.Range(0, 3).Select(_ => Task.Run(() => Run(text, "xl.customers.list.select(n: length(id)).orderBy(n)"), Token))];
      Task<List<string>>[] wholes = [.. Enumerable.Range(0, 3).Select(_ => Task.Run(() => Run(numbers, "xl.customers.list.select(n: id * 2).orderBy(n)"), Token))];
      foreach (string rows in (await Task.WhenAll(texts)).SelectMany(r => r)) { rows.ShouldBe(Lines("1", "1", "1")); }
      foreach (string rows in (await Task.WhenAll(wholes)).SelectMany(r => r)) { rows.ShouldBe(Lines("2", "4", "8")); }
   }

   /// <summary>A sheet that was renamed says to refresh the schema.</summary>
   [Fact]
   public async Task ASheetThatWasRenamedSaysToRefreshTheSchema()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      DateTime before = File.GetLastWriteTimeUtc(shop.Customers);
      new XlsxBuilder().Sheet("people", ExcelShop.CustomerRows).Save(shop.Customers);
      File.SetLastWriteTimeUtc(shop.Customers, before.AddMinutes(1));
      (await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(shop.Sources.Engine(), "xl.customers.list.select(id)")))
         .Message.ShouldBe("xl: 'customers.xlsx' has no sheet 'list' now; refresh the source's schema");
   }

   /// <summary>A workbook damaged after its schema was read fails the query with a message, leaving nothing behind.</summary>
   [Fact]
   public async Task AWorkbookDamagedAfterItsSchemaWasReadFailsTheQuery()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      DateTime before = File.GetLastWriteTimeUtc(shop.Customers);
      await File.WriteAllTextAsync(shop.Customers, "garbage", Token);
      File.SetLastWriteTimeUtc(shop.Customers, before.AddMinutes(1));
      (await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(shop.Sources.Engine(), "xl.customers.list.select(id)")))
         .Message.ShouldBe("xl: 'customers.xlsx' can't be read: it isn't an .xlsx workbook, or it is encrypted");
      MergeTables(shop.Sources).ShouldNotContain(t => t.Contains('[', StringComparison.Ordinal));
   }

   /// <summary>Reading the schema again drops the tables of sheets and workbooks that are gone.</summary>
   [Fact]
   public async Task ReadingTheSchemaAgainDropsSheetsThatAreGone()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      await RowsAsync(engine, "xl[\"Budget 2024\"].Q2.select(target)");
      await RowsAsync(engine, "xl.customers.list.select(id)");
      MergeTables(shop.Sources).ShouldContain("excel_xl.Budget 2024.Q2");
      File.Delete(shop.Folder.File("Budget 2024.xlsx"));
      await shop.Sources.Excel.IntrospectAsync("xl", cancellationToken: Token);
      MergeTables(shop.Sources).Where(t => t.StartsWith("excel_xl.", StringComparison.Ordinal)).ShouldBe(["excel_xl.customers.list"]);
   }

   /// <summary>Workbooks named like DuckDB's own schemas main and temp are schemas of the folder like any other.</summary>
   [Theory]
   [InlineData("main")]
   [InlineData("temp")]
   public async Task WorkbooksNamedLikeDuckDbSchemasLoad(string name)
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["v"], [1]).Save(folder.File(name + ".xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path, shop: true);
      string table = QueryText.FormatPath(["xl", name, "s"]);
      (await RowsAsync(sources.Engine(), table + ".select(v)")).ShouldBe(Lines("1"));
      (await RowsAsync(sources.Engine(), table + ".join(shop.customers, outer.v == inner.id, s: outer, c: inner).select(c.name)")).ShouldBe(Lines("'Acme Ltd'"));
   }

   /// <summary>DuckDB has information_schema and pg_catalog in every database, and no tables can go in them: workbooks named so are left out, with a warning.</summary>
   [Theory]
   [InlineData("information_schema")]
   [InlineData("pg_catalog")]
   public async Task WorkbooksNamedLikeDuckDbsSystemSchemasAreLeftOut(string name)
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["v"], [1]).Save(folder.File(name + ".xlsx"));
      new XlsxBuilder().Sheet("s", ["v"], [2]).Save(folder.File("other.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path);
      sources.Schemas["xl"].Tables.Select(t => t.Schema).ShouldBe(["other"]);
      sources.Schemas["xl"].Warnings.ShouldBe([$"'{name}.xlsx' is left out: DuckDB, which keeps the sheets, has a schema of that name in every database"]);
   }

   // ---------------------------------------------------------------------------------------------------------------
   // 5. Queries
   // ---------------------------------------------------------------------------------------------------------------

   /// <summary>MaxFetchedRows counts the rows copied inside the merge engine.</summary>
   [Fact]
   public async Task MaxFetchedRowsCountsCopiedRows()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine(options: new QueryEngineOptions { MaxFetchedRows = 4 });
      string query = Budget + ".join(shop.customers, outer.customer_id == inner.id, b: outer, c: inner).select(b.dept, c.name)";
      (await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(engine, query))).Message.ShouldContain("more than 4 rows");
   }

   /// <summary>Parameters reach a fragment copied with INSERT ... SELECT.</summary>
   [Fact]
   public async Task ParametersReachCopiedFragments()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      string query = Budget + ".where(amount > $min and dept != $skip).join(shop.customers, outer.customer_id == inner.id, b: outer, c: inner).select(b.dept, b.amount, c.name).orderBy(amount)";
      QueryParameters parameters = new QueryParameters().Add("min", 500).Add("skip", "Ops");
      PreparedQuery prepared = engine.Prepare(query, parameters);
      prepared.Fragments.Single(f => f.Source.Alias == "xl").InMergeEngine.ShouldBeTrue();
      (await RowsAsync(engine, query, parameters)).ShouldBe(Lines("'Sales' | 800 | 'Beta Corp'", "'Sales' | 1200.5 | 'Acme Ltd'"));
   }

   /// <summary>A value worked out first in SQLite reaches a fragment copied inside the merge engine.</summary>
   [Fact]
   public async Task ARuntimeValueReachesACopiedFragment()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      string query = Budget + ".where(amount > shop.orders.max(total)).select(dept, amount).orderBy(amount)";
      engine.Prepare(query).Fragments.ShouldContain(f => f.Source.Alias == "shop" && f.Value != null);
      (await RowsAsync(engine, query)).ShouldBe(Lines("'Ops' | 300", "'Ops' | 450.25", "'Sales' | 800", "'Sales' | 1200.5", "'R&D' | 2000"));
   }

   private static void TypesBook(string path) => new XlsxBuilder().Sheet("t",
         ["id", "i", "d", "day", "at", "tm", "flag", "txt", "dec", "off", "small"],
         [1, 42, 2.5, new DateOnly(2024, 1, 15), new DateTime(2024, 1, 15, 10, 30, 0), new TimeOnly(8, 15, 30), true, "hello", 12.34, new DateTime(2024, 1, 15, 23, 30, 0), 7],
         [2, -7, -0.125, new DateOnly(1999, 12, 31), new DateTime(1999, 12, 31, 23, 59, 59), new TimeOnly(23, 59, 59), false, "wörld", 0.5, new DateTime(2024, 6, 30, 0, 0, 0), -3])
      .Save(path);

   private static CatalogOverlay TypesOverlay =>
      Override("xl.types.t", ("dec", ScalarType.Decimal(10, 2)), ("off", ScalarType.DateTimeOffset), ("small", ScalarType.Int16));

   private const string TypesExpected =
      "Int64:42 | Double:2.5 | DateOnly:2024-01-15 | DateTime:2024-01-15 10:30:00.0000000 | TimeOnly:08:15:30.0000000 | Boolean:True | String:hello | Decimal:12.34 | DateTimeOffset:2024-01-15T23:30:00.0000000+00:00 | Int16:7\n" +
      "Int64:-7 | Double:-0.125 | DateOnly:1999-12-31 | DateTime:1999-12-31 23:59:59.0000000 | TimeOnly:23:59:59.0000000 | Boolean:False | String:wörld | Decimal:0.50 | DateTimeOffset:2024-06-30T00:00:00.0000000+00:00 | Int16:-3";

   /// <summary>Every column type comes back the same from a query of the folder alone, copied inside the merge engine, without push-down, and through a merge engine of its own.</summary>
   [Fact]
   public async Task ColumnTypesSurviveEveryRoute()
   {
      using TempFolder folder = new();
      TypesBook(folder.File("types.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path, shop: true);
      string expected = TypesExpected.Replace("\n", NL, StringComparison.Ordinal);
      const string columns = "i, d, day, at, tm, flag, txt, dec, off, small";
      Typed(await ValuesAsync(sources.Engine(TypesOverlay), $"xl.types.t.orderBy(id).select({columns})")).ShouldBe(expected);
      // Ordered by i descending: the rows of id 1 and 2, as above.
      string across = "xl.types.t.join(shop.customers, outer.id == inner.id, t: outer, c: inner).select(" +
                      string.Join(", ", columns.Split(", ").Select(c => "t." + c)) + ").orderBy(desc(i))";
      Typed(await ValuesAsync(sources.Engine(TypesOverlay), across)).ShouldBe(expected);
      Typed(await ValuesAsync(sources.Engine(TypesOverlay, new QueryEngineOptions { PushDown = false }), across)).ShouldBe(expected);
      using DuckDb.DuckDbMergeEngine other = new();
      Typed(await ValuesAsync(sources.Engine(TypesOverlay, merge: other), across)).ShouldBe(expected);
   }

   /// <summary>A fragment copied inside the merge engine works in UTC, as every other connection of the engine does: hour() and date() of an instant don't depend on the machine's zone.</summary>
   [Fact]
   public async Task CopiedFragmentsWorkInUtc()
   {
      using TempFolder folder = new();
      TypesBook(folder.File("types.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path, shop: true);
      QueryEngine engine = sources.Engine(TypesOverlay);
      (await RowsAsync(engine, "xl.types.t.where(hour(off) == 23).select(id)")).ShouldBe(Lines("1"));
      const string across = "xl.types.t.where(hour(off) == 23 and date(off) == toDate('2024-01-15')).join(shop.customers, outer.id == inner.id, t: outer, c: inner).select(t.id, c.name)";
      engine.Prepare(across).Fragments.Single(f => f.Source.Alias == "xl").Sql.ShouldContain("hour(");
      // Every connection the engine prepares has SET TimeZone = 'UTC'; a connection it doesn't prepare has the machine's zone.
      string zone = Strings(sources, "SELECT current_setting('TimeZone')").Single();
      (await RowsAsync(engine, across)).ShouldBe(Lines("1 | 'Acme Ltd'"), $"a merge connection that isn't prepared works in {zone}");
   }

   /// <summary>A date-time of a sheet compared with a parameter with an offset gives the same answer alone and in a copied fragment (both in UTC).</summary>
   [Fact]
   public async Task AnOffsetParameterComparesTheSameInACopiedFragment()
   {
      using TempFolder folder = new();
      TypesBook(folder.File("types.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path, shop: true);
      QueryEngine engine = sources.Engine(TypesOverlay);
      QueryParameters since = new QueryParameters().Add("since", new DateTimeOffset(2024, 1, 15, 9, 0, 0, TimeSpan.Zero));
      (await RowsAsync(engine, "xl.types.t.where(at > $since).select(id)", since)).ShouldBe(Lines("1"));
      (await RowsAsync(engine, "xl.types.t.where(at > $since).join(shop.customers, outer.id == inner.id, t: outer, c: inner).select(t.id, c.name)", since))
         .ShouldBe(Lines("1 | 'Acme Ltd'"));
   }

   /// <summary>For comparison, outside M8: a DuckDB source's timestamp compared with a parameter with an offset, in the source and in the merge engine (no push-down).</summary>
   [Fact]
   public async Task ADuckDbTimestampComparesWithAnOffsetParameter()
   {
      await using TestSources sources = await new TestSources().AddDuckDbAsync("dk", "CREATE TABLE t (id INTEGER, moment TIMESTAMP); INSERT INTO t VALUES (1, TIMESTAMP '2024-01-15 10:30:00'), (2, TIMESTAMP '1999-12-31 23:59:59');");
      QueryParameters since = new QueryParameters().Add("since", new DateTimeOffset(2024, 1, 15, 9, 0, 0, TimeSpan.Zero));
      (await RowsAsync(sources.Engine(), "dk.t.where(moment > $since).select(id)", since)).ShouldBe(Lines("1"));
      (await RowsAsync(sources.Engine(options: new QueryEngineOptions { PushDown = false }), "dk.t.where(moment > $since).select(id)", since)).ShouldBe(Lines("1"));
   }

   /// <summary>Workbook, sheet and column names that need quoting (quotes, dots, brackets, keywords, unicode, SQL) work alone and across sources.</summary>
   [Fact]
   public async Task NamesThatNeedQuotesWork()
   {
      using TempFolder folder = new();
      const string schema = "O'Brien; DROP TABLE x.v1";
      const string sheet = "order \"by\" 'x' é";
      string[] columns = ["a\"b", "x.y", "from", "ünï code", "[bracket]", "it", "$money", "select"];
      new XlsxBuilder().Sheet(sheet, columns, [1, 2, 3, 4, 5, 6, 7, 8], [2, 3, 4, 5, 6, 7, 8, 9]).Save(folder.File(schema + ".xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path, shop: true);
      sources.Schemas["xl"].Tables.Single().Columns.Select(c => c.Name).ShouldBe(columns);
      string table = QueryText.FormatPath(["xl", schema, sheet]);
      QueryEngine engine = sources.Engine();
      (await RowsAsync(engine, $"{table}.select({string.Join(", ", columns.Select(QueryText.QuoteName))}).orderBy({QueryText.QuoteName("from")})"))
         .ShouldBe(Lines("1 | 2 | 3 | 4 | 5 | 6 | 7 | 8", "2 | 3 | 4 | 5 | 6 | 7 | 8 | 9"));
      string key = QueryText.AppendMember(new StringBuilder("outer"), "a\"b").ToString();
      string xy = QueryText.AppendMember(new StringBuilder("t"), "x.y").ToString();
      string select = QueryText.AppendMember(new StringBuilder("t"), "select").ToString();
      (await RowsAsync(engine, $"{table}.join(shop.customers, {key} == inner.id, t: outer, c: inner).select(c.name, v: {xy}, w: {select}).orderBy(name)"))
         .ShouldBe(Lines("'Acme Ltd' | 2 | 8", "'Beta Corp' | 3 | 9"));
   }

   /// <summary>A schema name can't reach a workbook outside the folder: parent paths and full paths are refused before anything is read.</summary>
   [Theory]
   [InlineData("..\\outside")]
   [InlineData("../outside")]
   [InlineData("inner\\..\\..\\outside")]
   [InlineData("FULL")]
   public async Task ASchemaCantNameAWorkbookOutsideTheFolder(string schema)
   {
      using TempFolder root = new();
      string inner = Path.Combine(root.Path, "inner");
      Directory.CreateDirectory(inner);
      new XlsxBuilder().Sheet("s", ["v"], ["secret"]).Save(root.File("outside.xlsx"));
      if (schema == "FULL") { schema = root.File("outside"); }
      await using TestSources sources = new();
      ExcelSourceProvider excel = sources.Excel;
      excel.AddFolder("xl", new ExcelFolderOptions { Path = inner });
      TableSchema table = new(schema, "s", TableKind.Table, [new ColumnSchema("v", 0, "VARCHAR", ScalarType.Text())]);
      QueryCatalog catalog = new CatalogBuilder().AddSource(excel.Source("xl"), new SourceSchema(ExcelSourceProvider.Kind, null, string.Empty, [table])).Build();
      QueryEngine engine = new(catalog, sources, [excel], sources.Merge);
      string query = QueryText.FormatPath(["xl", schema, "s"]) + ".select(v)";
      engine.Prepare(query).Success.ShouldBeTrue();
      (await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(engine, query))).Message.ShouldContain("isn't the name of a workbook");
   }

   /// <summary>Semi and anti joins of a SQLite table with a sheet: any() and all().</summary>
   [Fact]
   public async Task AnyAndAllOfASheet()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      (await RowsAsync(engine, "shop.customers.where(c => xl.customers.list.any(l => l.id == c.id)).select(name).orderBy(name)")).ShouldBe(Lines("'Acme Ltd'", "'Beta Corp'"));
      (await RowsAsync(engine, "shop.customers.where(c => xl.customers.list.all(l => l.id != c.id)).select(name).orderBy(name)")).ShouldBe(Lines("'Gamma Inc'"));
      (await RowsAsync(engine, "xl.customers.list.where(l => not shop.orders.any(o => o.customer_id == l.id)).select(name).orderBy(name)")).ShouldBe(Lines("'Delta'"));
   }

   /// <summary>Set operations of a sheet with a SQLite table: concat and except.</summary>
   [Fact]
   public async Task SetOperationsWithASqliteTable()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      (await RowsAsync(engine, "xl.customers.list.select(id, name).concat(shop.customers.select(id, name)).orderBy(id, name)"))
         .ShouldBe(Lines("1 | 'Acme Ltd'", "1 | 'Acme Ltd'", "2 | 'Beta Corp'", "2 | 'Beta Corp'", "3 | 'Gamma Inc'", "4 | 'Delta'"));
      (await RowsAsync(engine, "shop.customers.select(id).except(xl.customers.list.select(id))")).ShouldBe(Lines("3"));
   }

   /// <summary>A left join of a sheet to a SQLite table, and paging (skip, take) across sources.</summary>
   [Fact]
   public async Task LeftJoinAndPagingAcrossSources()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      (await RowsAsync(engine, "xl.customers.list.leftJoin(shop.orders, outer.id == inner.customer_id, l: outer, o: inner).select(l.name, o.id).orderBy(name, id)"))
         .ShouldBe(Lines("'Acme Ltd' | 1001", "'Acme Ltd' | 1002", "'Beta Corp' | 1003", "'Delta' | null"));
      (await RowsAsync(engine, "xl.customers.list.join(shop.customers, outer.id == inner.id, l: outer, c: inner).select(l.id, c.name).orderBy(id).skip(1).take(1)"))
         .ShouldBe(Lines("2 | 'Beta Corp'"));
      await using QueryResult page = await engine.ExecuteAsync(new QueryRequest("xl.customers.list.join(shop.customers, outer.id == inner.id, l: outer, c: inner).select(l.id, c.name).orderBy(desc(id))")
         { Paging = new PageRequest(1, 5) }, Token);
      (await TestSources.RowsAsync(page)).ShouldBe(Lines("1 | 'Acme Ltd'"));
   }

   /// <summary>A sheet drives a bind join: SQLite is fetched by the keys of the sheet copied into f1.</summary>
   [Fact]
   public async Task ASheetDrivesABindJoin()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation(Budget, ["customer_id"], "shop.customers", ["id"]) { Name = "customer" }] };
      QueryEngine engine = shop.Sources.Engine(overlay, new QueryEngineOptions { BindJoins = BindJoinMode.Always });
      await using QueryResult result = await engine.Prepare(Budget + ".select(dept, amount, who: customer.name).orderBy(amount)").ExecuteAsync(Token);
      (await TestSources.RowsAsync(result)).ShouldBe(Lines("'Ops' | 300 | 'Acme Ltd'", "'Ops' | 450.25 | 'Gamma Inc'", "'Sales' | 800 | 'Beta Corp'", "'Sales' | 1200.5 | 'Acme Ltd'", "'R&D' | 2000 | null"));
      FragmentStats customers = result.Stats.Fragments.Single(f => f.Source == "shop");
      customers.Strategy.ShouldBe(FetchStrategy.Keys);
      customers.Keys.ShouldBe(4);
      customers.Rows.ShouldBe(3);
   }

   /// <summary>A sheet fetched by keys in several batches: each batch is copied inside the merge engine, adding to the table.</summary>
   [Fact]
   public async Task ASheetIsCopiedByKeysInSeveralBatches()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      CatalogOverlay overlay = new()
      {
         Entities = [new OverlayEntitySettings("xl.customers.list") { Key = ["id"] }],
         Relations = [new OverlayRelation("shop.orders", ["customer_id"], "xl.customers.list", ["id"]) { Name = "listed" }],
      };
      QueryEngine engine = shop.Sources.Engine(overlay, new QueryEngineOptions { BindJoins = BindJoinMode.Always, MaxBindBatch = 1 });
      engine.Prepare("shop.orders.select(id, tier: listed.tier).orderBy(id)").Explain().Fragments.Single(f => f.Source == "xl").Strategy
         .ShouldBe("into f2 inside the merge engine, by the keys of f1.customer_id when they are few enough, else in full");
      await using QueryResult result = await engine.Prepare("shop.orders.select(id, tier: listed.tier).orderBy(id)").ExecuteAsync(Token);
      (await TestSources.RowsAsync(result)).ShouldBe(Lines("1001 | 'gold'", "1002 | 'gold'", "1003 | 'silver'", "1004 | null"));
      FragmentStats sheet = result.Stats.Fragments.Single(f => f.Source == "xl");
      sheet.Batches.ShouldBe(3);
      sheet.Rows.ShouldBe(2);
   }

   /// <summary>A query of no table, with an Excel folder the only source, runs.</summary>
   [Fact]
   public async Task AQueryOfNoTableRunsWithOnlyAFolder()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["v"], [1]).Save(folder.File("b.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path);
      (await RowsAsync(sources.Engine(), "1 + 1")).ShouldBe(Lines("2"));
   }

   /// <summary>A query of one folder with no merge engine configured still runs, in the folder's catalog.</summary>
   [Fact]
   public async Task AQueryOfOneFolderNeedsNoMergeEngineConfigured()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      (await RowsAsync(shop.Sources.Engine(noMerge: true), "xl.customers.list.select(id).orderBy(id)")).ShouldBe(Lines("1", "2", "4"));
   }

   /// <summary>A value too big for its column's type (an interval) fails the query naming its cell, or is null when lenient; it doesn't escape as an OverflowException.</summary>
   [Fact]
   public async Task AValueTooBigForAnIntervalIsAConversionFailure()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["id", "span"], [1, 1.5], [2, 1e20]).Save(folder.File("i.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path);
      CatalogOverlay overlay = Override("xl.i.s", ("span", ScalarType.Interval));
      (await RowsAsync(sources.Engine(overlay, new QueryEngineOptions { LenientConversion = true }), "xl.i.s.orderBy(id).select(span)")).ShouldBe(Lines("1.12:00:00", "null"));
      (await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(sources.Engine(overlay), "xl.i.s.orderBy(id).select(span)"))).Message.ShouldContain("(cell B3)");
   }

   /// <summary>Removing a folder and adding it again while a query streams its rows lets the query finish, and later queries work.</summary>
   [Fact]
   public async Task RemovingAFolderWhileAQueryReadsItLetsTheQueryFinish()
   {
      using TempFolder folder = new();
      BigBook(folder.File("big.xlsx"), 20_000, 0);
      await using TestSources sources = await FolderAsync(folder.Path);
      QueryEngine engine = sources.Engine();
      await using (QueryResult result = await engine.Prepare("xl.big.s.select(n)").ExecuteAsync(Token))
      {
         (await result.ReadAsync(Token)).ShouldBeTrue();
         sources.Excel.RemoveFolder("xl");
         sources.Excel.AddFolder("xl", new ExcelFolderOptions { Path = folder.Path });
         long rows = 1;
         while (await result.ReadAsync(Token)) { rows++; }
         rows.ShouldBe(20_000);
      }
      (await RowsAsync(engine, "xl.big.s.count()")).ShouldBe(Lines("20000"));
   }

   /// <summary>A sheet of headings alone is a table of text columns without rows.</summary>
   [Fact]
   public async Task ASheetOfHeadingsAloneIsATableWithoutRows()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["a", "b"]).Save(folder.File("h.xlsx"));
      await using TestSources sources = await FolderAsync(folder.Path, shop: true);
      sources.Schemas["xl"].Tables.Single().Columns.Select(c => $"{c.Name} {c.Type}").ShouldBe(["a string?", "b string?"]);
      (await RowsAsync(sources.Engine(), "xl.h.s.count()")).ShouldBe(Lines("0"));
      (await RowsAsync(sources.Engine(), "shop.customers.where(c => xl.h.s.any(h => h.a == c.name)).count()")).ShouldBe(Lines("0"));
   }

   /// <summary>A relation the overlay declares between two sheets of a folder navigates within one statement.</summary>
   [Fact]
   public async Task ARelationBetweenSheetsOfAFolder()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      CatalogOverlay overlay = new()
      {
         Entities = [new OverlayEntitySettings("xl.customers.list") { Key = ["id"] }],
         Relations = [new OverlayRelation(Budget, ["customer_id"], "xl.customers.list", ["id"]) { Name = "listed" }],
      };
      QueryEngine engine = shop.Sources.Engine(overlay);
      string query = Budget + ".select(dept, amount, tier: listed.tier).orderBy(amount)";
      engine.Prepare(query).Fragments.Count.ShouldBe(1);
      (await RowsAsync(engine, query)).ShouldBe(Lines("'Ops' | 300 | 'gold'", "'Ops' | 450.25 | null", "'Sales' | 800 | 'silver'", "'Sales' | 1200.5 | 'gold'", "'R&D' | 2000 | null"));
   }

   /// <summary>Sorting, take, groupBy and aggregates pushed into a copied fragment (INSERT INTO f1 ... ORDER BY ... LIMIT), and a count across sources.</summary>
   [Fact]
   public async Task WorkPushedIntoACopiedFragment()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      (await RowsAsync(engine, Budget + ".orderBy(desc(amount)).take(2).join(shop.customers, outer.customer_id == inner.id, b: outer, c: inner).select(c.name, b.amount).orderBy(amount)"))
         .ShouldBe(Lines("'Acme Ltd' | 1200.5"));
      (await RowsAsync(engine, Budget + ".groupBy(customer_id).select(customer_id: key, total: sum(amount)).join(shop.customers, outer.customer_id == inner.id, g: outer, c: inner).select(c.name, g.total).orderBy(name)"))
         .ShouldBe(Lines("'Acme Ltd' | 1500.5", "'Beta Corp' | 800", "'Gamma Inc' | 450.25"));
      await using QueryResult count = await engine.Prepare(Budget + ".join(shop.customers, outer.customer_id == inner.id, b: outer, c: inner).select(b.dept)").ForCount().ExecuteAsync(Token);
      (await TestSources.RowsAsync(count)).ShouldBe(Lines("4"));
   }

   /// <summary>Hidden workbooks are left out of the folder's schema.</summary>
   [Fact]
   public async Task HiddenWorkbooksAreLeftOut()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["v"], [1]).Save(folder.File("seen.xlsx"));
      new XlsxBuilder().Sheet("s", ["v"], [1]).Save(folder.File("unseen.xlsx"));
      File.SetAttributes(folder.File("unseen.xlsx"), FileAttributes.Hidden);
      try
      {
         await using TestSources sources = await FolderAsync(folder.Path);
         sources.Schemas["xl"].Tables.Select(t => t.Schema).ShouldBe(["seen"]);
      }
      finally
      {
         File.SetAttributes(folder.File("unseen.xlsx"), FileAttributes.Normal);
      }
   }

   /// <summary>Reading the schema of some workbooks only (IncludeSchemas) leaves the others' sheets working (loaded again when next read).</summary>
   [Fact]
   public async Task ReadingThePartOfTheSchemaLeavesTheRestWorking()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      (await RowsAsync(engine, "xl.customers.list.select(id).orderBy(id)")).ShouldBe(Lines("1", "2", "4"));
      await shop.Sources.Excel.IntrospectAsync("xl", new IntrospectionOptions { IncludeSchemas = ["Budget 2024"] }, Token);
      (await RowsAsync(engine, "xl.customers.list.select(id).orderBy(id)")).ShouldBe(Lines("1", "2", "4"));
   }

   // ---------------------------------------------------------------------------------------------------------------
   // 6. The command line
   // ---------------------------------------------------------------------------------------------------------------

   /// <summary>gdq run over a folder alone, and explain and sql across a folder and SQLite.</summary>
   [Fact]
   public async Task GdqRunsExplainsAndWritesSqlForAFolder()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("lines", ["customer_id", "amount"], [1, 10.5], [2, 20]).Save(folder.File("budget.xlsx"));
      string excel = "xl=excel:" + folder.Path;
      (int exit, string output, string error) = await GdqAsync("run", "-s", excel, "--format", "csv", "xl.budget.lines.select(customer_id, amount).orderBy(amount)");
      (exit, error).ShouldBe((0, string.Empty));
      output.ShouldContain("10.5");
      output.ShouldContain("20");
      const string across = "xl.budget.lines.join(shop.customers, outer.customer_id == inner.id, b: outer, c: inner).select(c.name, b.amount)";
      (exit, output, error) = await GdqAsync("explain", "-s", Sqlite, "-s", excel, across);
      (exit, error).ShouldBe((0, string.Empty));
      output.ShouldContain("copied into f1 inside the merge engine");
      (exit, output, error) = await GdqAsync("sql", "-s", Sqlite, "-s", excel, across);
      (exit, error).ShouldBe((0, string.Empty));
      output.ShouldContain("excel_xl.budget.lines");
      output.ShouldContain("copied into f1 inside the merge engine");
   }

   /// <summary>gdq with one folder (its path with spaces and a quote) under two aliases, one path with a trailing separator and spaces around the parts.</summary>
   [Fact]
   public async Task GdqTakesOneFolderUnderTwoAliases()
   {
      using TempFolder root = new();
      string folder = Path.Combine(root.Path, "my 'books'");
      Directory.CreateDirectory(folder);
      new XlsxBuilder().Sheet("s", ["k"], [1], [2]).Save(Path.Combine(folder, "b.xlsx"));
      (int exit, string output, string error) = await GdqAsync("run", "-s", "a=excel:" + folder, "-s", " b = excel : " + folder + Path.DirectorySeparatorChar,
                                                               "--format", "csv", "a.b.s.join(b.b.s, outer.k == inner.k, x: outer, y: inner).count()");
      (exit, error).ShouldBe((0, string.Empty));
      output.ShouldContain("2");
   }

   /// <summary>gdq repl over a folder runs queries of it, one after another in one session.</summary>
   [Fact]
   public async Task GdqReadsAFolderInTheRepl()
   {
      using TempFolder folder = new();
      new XlsxBuilder().Sheet("s", ["k", "v"], [1, "one"], [2, "two"]).Save(folder.File("b.xlsx"));
      using StringWriter output = new();
      using StringWriter error = new();
      int exit = await GdqApp.RunAsync(["repl", "-s", "xl=excel:" + folder.Path],
                                       new StringReader("xl.b.s.select(v).orderBy(v)\nxl.b.s.count()\n:quit\n"), output, error, Token);
      (exit, error.ToString()).ShouldBe((0, string.Empty));
      output.ToString().ShouldContain("two");
   }
}
