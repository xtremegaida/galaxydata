using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace GalaxyData.Query.Excel;

/// <summary>A worksheet of a workbook: its name, the package part with its cells, and whether Excel hides it.</summary>
internal sealed record SheetInfo(string Name, string Part, bool Hidden);

/// <summary>A row of a sheet with a value in it: its number (from 1) and its cells with values, by column.</summary>
internal sealed record SheetRow(int Number, Cell[] Cells);

/// <summary>A cell with a value: its column (from 0) and the value.</summary>
internal readonly record struct Cell(int Column, CellValue Value);

/// <summary>A file that can't be read as a workbook: not one, damaged, or encrypted.</summary>
internal sealed class WorkbookFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads an .xlsx workbook (an Office Open XML spreadsheet) without an Excel library: the package is a zip of XML
/// parts, read here with <see cref="XmlReader"/>, a row at a time. Only worksheets have cells to read; chart,
/// dialog and macro sheets are left out. Elements are matched by local name, so transitional and strict documents
/// both read. The file is opened once, and may be replaced while it is read (as Excel saves).
/// </summary>
internal sealed class Workbook : IDisposable
{
   private static readonly XmlReaderSettings Settings = new()
   {
      DtdProcessing = DtdProcessing.Prohibit,
      IgnoreComments = true,
      IgnoreProcessingInstructions = true,
      CloseInput = true,
   };

   private readonly ZipArchive zip;
   private readonly Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.OrdinalIgnoreCase);
   private readonly string? sharedStringsPart;
   private readonly string? stylesPart;
   private string[]? sharedStrings;
   private DateFormat[]? styles;

   private Workbook(ZipArchive zip)
   {
      this.zip = zip;
      foreach (ZipArchiveEntry entry in zip.Entries)
      {
         // Relationship targets are URIs: a part may be named as its target is written, or as that reads unescaped.
         string name = entry.FullName.Replace('\\', '/').TrimStart('/');
         entries.TryAdd(name, entry);
         entries.TryAdd(Uri.UnescapeDataString(name), entry);
      }
      string part = Relationships(string.Empty).FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal))?.Target ?? "xl/workbook.xml";
      if (!entries.ContainsKey(part)) { throw new WorkbookFormatException("it has no workbook in it"); }
      List<Relationship> relationships = Relationships(part);
      sharedStringsPart = relationships.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal))?.Target;
      stylesPart = relationships.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal))?.Target;
      Sheets = ReadSheets(part, relationships);
   }

   /// <summary>The worksheets, in the workbook's order.</summary>
   public IReadOnlyList<SheetInfo> Sheets { get; }

   /// <summary>Whether serial dates count from 1904 (old Mac workbooks) instead of 1900.</summary>
   public bool Date1904 { get; private set; }

   public static Workbook Open(string path)
   {
      FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      try
      {
         return new Workbook(new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false));
      }
      catch (InvalidDataException e)
      {
         stream.Dispose();
         throw new WorkbookFormatException("it isn't an .xlsx workbook, or it is encrypted", e);
      }
      catch (XmlException e)
      {
         stream.Dispose();
         throw new WorkbookFormatException("its XML is damaged: " + e.Message, e);
      }
      catch
      {
         stream.Dispose();
         throw;
      }
   }

   public void Dispose() => zip.Dispose();

   /// <summary>The rows of a sheet that have a value, in order, each with its cells that have one.</summary>
   public IEnumerable<SheetRow> ReadRows(SheetInfo sheet)
   {
      ArgumentNullException.ThrowIfNull(sheet);
      if (!entries.ContainsKey(sheet.Part)) { throw new WorkbookFormatException($"its part {sheet.Part} isn't in the workbook"); }
      using XmlReader reader = Read(sheet.Part);
      while (reader.Read() && !(reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheetData")) { }
      if (reader.EOF || reader.IsEmptyElement) { yield break; }
      int depth = reader.Depth;
      int number = 0;
      List<Cell> cells = [];
      reader.Read();
      while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
      {
         if (reader.NodeType != XmlNodeType.Element)
         {
            reader.Read();
            continue;
         }
         if (reader.LocalName != "row")
         {
            reader.Skip();
            continue;
         }
         number = ParseInt(reader.GetAttribute("r")) is { } given and > 0 ? given : number + 1;
         cells.Clear();
         ReadCells(reader, cells);
         if (cells.Count > 0) { yield return new SheetRow(number, [.. cells]); }
      }
   }

   /// <summary>The cells with values of the row the reader is on; the reader ends past the row.</summary>
   private void ReadCells(XmlReader reader, List<Cell> cells)
   {
      if (reader.IsEmptyElement)
      {
         reader.Read();
         return;
      }
      int depth = reader.Depth;
      int column = -1;
      reader.Read();
      while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
      {
         if (reader.NodeType != XmlNodeType.Element)
         {
            reader.Read();
            continue;
         }
         if (reader.LocalName != "c")
         {
            reader.Skip();
            continue;
         }
         column = ColumnOf(reader.GetAttribute("r")) ?? column + 1;
         string? type = reader.GetAttribute("t");
         int style = ParseInt(reader.GetAttribute("s")) ?? 0;
         CellValue value = ReadCell(reader, type, style);
         if (!value.IsEmpty) { cells.Add(new Cell(column, value)); }
      }
      reader.Read();
   }

   /// <summary>The value of the cell the reader is on (its cached value, for a formula); the reader ends past the cell.</summary>
   private CellValue ReadCell(XmlReader reader, string? type, int style)
   {
      if (reader.IsEmptyElement)
      {
         reader.Read();
         return CellValue.Empty;
      }
      int depth = reader.Depth;
      string? value = null;
      string? inline = null;
      reader.Read();
      while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
      {
         if (reader.NodeType != XmlNodeType.Element)
         {
            reader.Read();
            continue;
         }
         switch (reader.LocalName)
         {
            case "v":
               value = reader.ReadElementContentAsString();
               break;
            case "is":
               inline = ReadText(reader);
               break;
            default:
               reader.Skip();
               break;
         }
      }
      reader.Read();
      return Value(type, style, value, inline);
   }

   private CellValue Value(string? type, int style, string? value, string? inline)
   {
      switch (type)
      {
         case "s":
            string[] strings = SharedStrings();
            return ParseInt(value) is { } index && index >= 0 && index < strings.Length ? CellValue.OfText(strings[index]) : CellValue.Empty;
         case "inlineStr":
            return CellValue.OfText(inline ?? (value == null ? null : Unescape(value)));
         case "str":
            return CellValue.OfText(value == null ? null : Unescape(value));
         case "b":
            return value == null ? CellValue.Empty : CellValue.OfBoolean(value.Trim() is "1" or "true" or "TRUE");
         case "e":
            return value == null ? CellValue.Empty : CellValue.OfError(value);
         case "d":
            return value == null ? CellValue.Empty : CellValue.OfIsoDate(value);
         default:
            if (value == null) { return CellValue.Empty; }
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) { return CellValue.OfText(value); }
            DateFormat[] formats = Styles();
            DateFormat format = style >= 0 && style < formats.Length ? formats[style] : DateFormat.None;
            return format == DateFormat.None ? CellValue.OfNumber(number) : CellValue.OfDate(format, number, Date1904);
      }
   }

   private List<SheetInfo> ReadSheets(string part, List<Relationship> relationships)
   {
      List<SheetInfo> sheets = [];
      using XmlReader reader = Read(part);
      while (reader.Read())
      {
         if (reader.NodeType != XmlNodeType.Element) { continue; }
         if (reader.LocalName == "workbookPr")
         {
            Date1904 = reader.GetAttribute("date1904") is "1" or "true";
         }
         else if (reader.LocalName == "sheet" && reader.GetAttribute("name") is { Length: > 0 } name && RelationshipId(reader) is { } id)
         {
            // Chart sheets, dialog sheets and macro sheets have no cells.
            Relationship? target = relationships.FirstOrDefault(r => r.Id == id);
            if (target == null || !target.Type.EndsWith("/worksheet", StringComparison.Ordinal)) { continue; }
            sheets.Add(new SheetInfo(name, target.Target, reader.GetAttribute("state") is "hidden" or "veryHidden"));
         }
      }
      return sheets;
   }

   /// <summary>The <c>r:id</c> of the element the reader is on, in whichever relationships namespace the document uses.</summary>
   private static string? RelationshipId(XmlReader reader)
   {
      for (bool more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
      {
         if (reader.LocalName == "id" && reader.NamespaceURI.EndsWith("relationships", StringComparison.Ordinal))
         {
            string id = reader.Value;
            reader.MoveToElement();
            return id;
         }
      }
      reader.MoveToElement();
      return null;
   }

   private string[] SharedStrings()
   {
      if (sharedStrings != null) { return sharedStrings; }
      List<string> strings = [];
      if (sharedStringsPart != null && entries.ContainsKey(sharedStringsPart))
      {
         using XmlReader reader = Read(sharedStringsPart);
         reader.Read();
         while (!reader.EOF)
         {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si") { strings.Add(ReadText(reader)); }
            else { reader.Read(); }
         }
      }
      return sharedStrings = [.. strings];
   }

   /// <summary>Which number format each cell style (by index) has: dates and times are numbers shown as such.</summary>
   private DateFormat[] Styles()
   {
      if (styles != null) { return styles; }
      List<DateFormat> formats = [];
      if (stylesPart != null && entries.ContainsKey(stylesPart))
      {
         Dictionary<int, string> codes = [];
         using XmlReader reader = Read(stylesPart);
         int cellFormats = -1;
         while (reader.Read())
         {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == cellFormats)
            {
               cellFormats = -1;
               continue;
            }
            if (reader.NodeType != XmlNodeType.Element) { continue; }
            switch (reader.LocalName)
            {
               case "numFmt" when ParseInt(reader.GetAttribute("numFmtId")) is { } id:
                  codes[id] = reader.GetAttribute("formatCode") ?? string.Empty;
                  break;
               case "cellXfs" when !reader.IsEmptyElement:
                  cellFormats = reader.Depth;
                  break;
               case "xf" when cellFormats >= 0 && reader.Depth == cellFormats + 1:
                  int format = ParseInt(reader.GetAttribute("numFmtId")) ?? 0;
                  formats.Add(NumberFormats.Classify(format, codes.GetValueOrDefault(format)));
                  break;
            }
         }
      }
      return styles = [.. formats];
   }

   /// <summary>
   /// The text of a string item (<c>si</c>) or inline string (<c>is</c>) the reader is on: its text, or the text of
   /// its runs, without phonetic hints; the reader ends past it.
   /// </summary>
   private static string ReadText(XmlReader reader)
   {
      if (reader.IsEmptyElement)
      {
         reader.Read();
         return string.Empty;
      }
      int depth = reader.Depth;
      StringBuilder text = new();
      reader.Read();
      while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
      {
         if (reader.NodeType != XmlNodeType.Element)
         {
            reader.Read();
         }
         else if (reader.LocalName == "t")
         {
            text.Append(reader.ReadElementContentAsString());
         }
         else if (reader.LocalName == "r" && !reader.IsEmptyElement)
         {
            // A run of rich text: its formatting, then its text.
            int run = reader.Depth;
            reader.Read();
            while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == run))
            {
               if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") { text.Append(reader.ReadElementContentAsString()); }
               else if (reader.NodeType == XmlNodeType.Element) { reader.Skip(); }
               else { reader.Read(); }
            }
            reader.Read();
         }
         else
         {
            reader.Skip();
         }
      }
      reader.Read();
      return Unescape(text.ToString());
   }

   /// <summary>Characters XML can't hold are written <c>_xHHHH_</c> (and an underscore that would read as one, <c>_x005F_</c>).</summary>
   internal static string Unescape(string text)
   {
      if (!text.Contains("_x", StringComparison.Ordinal)) { return text; }
      StringBuilder result = new(text.Length);
      for (int i = 0; i < text.Length; i++)
      {
         if (text[i] == '_' && i + 6 < text.Length && text[i + 1] == 'x' && text[i + 6] == '_' &&
             int.TryParse(text.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
         {
            result.Append((char)code);
            i += 6;
         }
         else
         {
            result.Append(text[i]);
         }
      }
      return result.ToString();
   }

   private XmlReader Read(string part)
   {
      Stream stream = entries[part].Open();
      return XmlReader.Create(stream, Settings);
   }

   private sealed record Relationship(string Id, string Type, string Target);

   /// <summary>The relationships of a part (the package's own for an empty name), with their targets as part names.</summary>
   private List<Relationship> Relationships(string part)
   {
      int slash = part.LastIndexOf('/');
      string folder = slash < 0 ? string.Empty : part[..(slash + 1)];
      string name = part[(slash + 1)..];
      string rels = folder + "_rels/" + name + ".rels";
      List<Relationship> relationships = [];
      if (!entries.ContainsKey(rels)) { return relationships; }
      using XmlReader reader = Read(rels);
      while (reader.Read())
      {
         if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") { continue; }
         if (reader.GetAttribute("TargetMode") == "External") { continue; }
         if (reader.GetAttribute("Id") is { } id && reader.GetAttribute("Type") is { } type && reader.GetAttribute("Target") is { } target)
         {
            relationships.Add(new Relationship(id, type, Resolve(folder, target)));
         }
      }
      return relationships;
   }

   /// <summary>A relationship's target as a part name: relative to the folder of the part it's from, unless it starts with <c>/</c>.</summary>
   private static string Resolve(string folder, string target)
   {
      string path = Uri.UnescapeDataString(target.Replace('\\', '/'));
      path = path.StartsWith('/') ? path[1..] : folder + path;
      List<string> parts = [];
      foreach (string segment in path.Split('/'))
      {
         if (segment is "" or ".") { continue; }
         if (segment == "..")
         {
            if (parts.Count > 0) { parts.RemoveAt(parts.Count - 1); }
            continue;
         }
         parts.Add(segment);
      }
      return string.Join('/', parts);
   }

   /// <summary>The column (from 0) of a cell reference such as <c>AB12</c>; null when there is none.</summary>
   internal static int? ColumnOf(string? reference)
   {
      if (string.IsNullOrEmpty(reference)) { return null; }
      int column = 0;
      int letters = 0;
      foreach (char c in reference)
      {
         char upper = char.ToUpperInvariant(c);
         if (upper is < 'A' or > 'Z') { break; }
         column = column * 26 + (upper - 'A' + 1);
         if (++letters > 3) { return null; }
      }
      return letters == 0 ? null : column - 1;
   }

   /// <summary>The letters of a column (from 0): <c>A</c>, <c>Z</c>, <c>AA</c>.</summary>
   internal static string Letters(int column)
   {
      Span<char> letters = stackalloc char[4];
      int at = letters.Length;
      for (int n = column + 1; n > 0; n = (n - 1) / 26) { letters[--at] = (char)('A' + (n - 1) % 26); }
      return new string(letters[at..]);
   }

   private static int? ParseInt(string? text) =>
      int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}
