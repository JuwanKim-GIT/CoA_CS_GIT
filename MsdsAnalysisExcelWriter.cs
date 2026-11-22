using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace QCS
{
    // Open XML Spreadsheet writer without an Excel installation or additional NuGet packages.
    internal static class MsdsAnalysisExcelWriter
    {
        internal static void Save(string path, IReadOnlyList<string[]> results, IReadOnlyList<string[]> logs)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            Write(archive, "[Content_Types].xml", x => {
                x.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                x.WriteStartElement("Default"); x.WriteAttributeString("Extension", "rels"); x.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml"); x.WriteEndElement();
                x.WriteStartElement("Default"); x.WriteAttributeString("Extension", "xml"); x.WriteAttributeString("ContentType", "application/xml"); x.WriteEndElement();
                foreach (var n in new[] { 1, 2 }) { x.WriteStartElement("Override"); x.WriteAttributeString("PartName", $"/xl/worksheets/sheet{n}.xml"); x.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"); x.WriteEndElement(); }
                x.WriteStartElement("Override"); x.WriteAttributeString("PartName", "/xl/workbook.xml"); x.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"); x.WriteEndElement();
                x.WriteStartElement("Override"); x.WriteAttributeString("PartName", "/xl/styles.xml"); x.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"); x.WriteEndElement();
                x.WriteEndElement();
            });
            Write(archive, "_rels/.rels", x => {
                x.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                x.WriteStartElement("Relationship"); x.WriteAttributeString("Id", "rId1"); x.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"); x.WriteAttributeString("Target", "xl/workbook.xml"); x.WriteEndElement(); x.WriteEndElement();
            });
            Write(archive, "xl/workbook.xml", x => {
                x.WriteStartElement("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                x.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                x.WriteStartElement("sheets");
                for (int i = 1; i <= 2; i++) { x.WriteStartElement("sheet"); x.WriteAttributeString("name", i == 1 ? "MSDS 분석결과" : "분석 로그"); x.WriteAttributeString("sheetId", i.ToString()); x.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", $"rId{i}"); x.WriteEndElement(); }
                x.WriteEndElement(); x.WriteEndElement();
            });
            Write(archive, "xl/_rels/workbook.xml.rels", x => {
                x.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                for (int i = 1; i <= 2; i++) { x.WriteStartElement("Relationship"); x.WriteAttributeString("Id", $"rId{i}"); x.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"); x.WriteAttributeString("Target", $"worksheets/sheet{i}.xml"); x.WriteEndElement(); }
                x.WriteStartElement("Relationship"); x.WriteAttributeString("Id", "rId3"); x.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"); x.WriteAttributeString("Target", "styles.xml"); x.WriteEndElement(); x.WriteEndElement();
            });
            Write(archive, "xl/styles.xml", x => {
                x.WriteStartElement("styleSheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                x.WriteStartElement("fonts"); x.WriteAttributeString("count", "2");
                x.WriteStartElement("font"); x.WriteStartElement("sz"); x.WriteAttributeString("val", "11"); x.WriteEndElement(); x.WriteEndElement();
                x.WriteStartElement("font"); x.WriteStartElement("b"); x.WriteEndElement(); x.WriteStartElement("color"); x.WriteAttributeString("rgb", "FFFFFFFF"); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
                x.WriteStartElement("fills"); x.WriteAttributeString("count", "5");
                Fill(x, null); Fill(x, "gray125"); Fill(x, "FF1E3A5F"); Fill(x, "FFEAF1F9"); Fill(x, "FFF4F7FB");
                x.WriteEndElement();
                x.WriteStartElement("borders"); x.WriteAttributeString("count", "2");
                x.WriteStartElement("border"); x.WriteEndElement();
                x.WriteStartElement("border");
                foreach (var side in new[] { "left", "right", "top", "bottom" }) {
                    x.WriteStartElement(side); x.WriteAttributeString("style", "hair");
                    x.WriteStartElement("color"); x.WriteAttributeString("rgb", "FFD4DEE9"); x.WriteEndElement(); x.WriteEndElement();
                }
                x.WriteEndElement(); x.WriteEndElement();
                x.WriteStartElement("cellStyleXfs"); x.WriteAttributeString("count", "1");
                x.WriteStartElement("xf"); x.WriteAttributeString("numFmtId", "0"); x.WriteAttributeString("fontId", "0"); x.WriteAttributeString("fillId", "0"); x.WriteAttributeString("borderId", "0"); x.WriteEndElement(); x.WriteEndElement();
                x.WriteStartElement("cellXfs"); x.WriteAttributeString("count", "5");
                CellStyle(x, 0, 0, 1, false); // normal body
                CellStyle(x, 1, 2, 1, true);  // column headers
                CellStyle(x, 0, 3, 1, false); // document group, first row
                CellStyle(x, 0, 4, 1, false); // document group, alternating
                CellStyle(x, 0, 0, 0, false); // fallback
                x.WriteEndElement(); x.WriteEndElement();
            });
            Sheet(archive, 1, results);
            Sheet(archive, 2, logs);
        }
        private static void Fill(XmlWriter x, string? color)
        {
            x.WriteStartElement("fill"); x.WriteStartElement("patternFill");
            x.WriteAttributeString("patternType", color == null ? "none" : color == "gray125" ? "gray125" : "solid");
            if (color != null && color != "gray125") {
                x.WriteStartElement("fgColor"); x.WriteAttributeString("rgb", color); x.WriteEndElement();
                x.WriteStartElement("bgColor"); x.WriteAttributeString("indexed", "64"); x.WriteEndElement();
            }
            x.WriteEndElement(); x.WriteEndElement();
        }
        private static void CellStyle(XmlWriter x, int font, int fill, int border, bool bold)
        {
            x.WriteStartElement("xf"); x.WriteAttributeString("numFmtId", "0");
            x.WriteAttributeString("fontId", font.ToString()); x.WriteAttributeString("fillId", fill.ToString());
            x.WriteAttributeString("borderId", border.ToString()); x.WriteAttributeString("xfId", "0");
            x.WriteAttributeString("applyAlignment", "1");
            x.WriteStartElement("alignment"); x.WriteAttributeString("vertical", "center");
            x.WriteAttributeString("horizontal", "left"); x.WriteAttributeString("wrapText", "1");
            x.WriteEndElement(); x.WriteEndElement();
        }
        private static void Sheet(ZipArchive archive, int index, IReadOnlyList<string[]> rows) => Write(archive, $"xl/worksheets/sheet{index}.xml", x => {
            x.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            int cols = rows.Count == 0 ? 1 : rows.Max(r => r.Length);
            int ingredientStart = index == 1 && rows.Count > 0 ? Array.IndexOf(rows[0], "성분명") : -1;
            if (ingredientStart < 0) ingredientStart = 0;
            var groups = new List<(int start, int end)>();
            if (index == 1 && rows.Count > 1) {
                int groupStart = 1;
                for (int r = 2; r <= rows.Count; r++) {
                    if (r == rows.Count || (rows[r].Length > 0 && !string.IsNullOrWhiteSpace(rows[r][0]))) {
                        groups.Add((groupStart, r - 1)); groupStart = r;
                    }
                }
            }
            x.WriteStartElement("sheetViews"); x.WriteStartElement("sheetView"); x.WriteAttributeString("workbookViewId", "0");
            x.WriteAttributeString("showGridLines", "0");
            x.WriteStartElement("pane"); x.WriteAttributeString("ySplit", "1"); x.WriteAttributeString("topLeftCell", "A2");
            x.WriteAttributeString("activePane", "bottomLeft"); x.WriteAttributeString("state", "frozen"); x.WriteEndElement();
            x.WriteEndElement(); x.WriteEndElement();
            x.WriteStartElement("sheetFormatPr"); x.WriteAttributeString("defaultRowHeight", "24"); x.WriteEndElement();
            x.WriteStartElement("cols");
            for (int c = 0; c < cols; c++) {
                var h = rows.Count > 0 && c < rows[0].Length ? rows[0][c] : "";
                double width = index == 2 ? (c == 1 ? 36 : c == 4 ? 72 : c == 2 ? 23 : 19)
                    : c >= ingredientStart ? (h.Contains("성분명") ? 29 : h.Contains("CAS") ? 17 : h.Contains("LD50") || h.Contains("LC50") ? 27 : h == "함량(원문)" ? 20 : 16)
                    : h == "MSDS 파일명" ? 31 : h == "Graco Code" ? 14 : h == "원료/제품" ? 13
                    : h.Contains("주소") ? 33 : h.Contains("개인보호구") || h.Contains("반응성") ? 36
                    : h.Contains("유해") ? 28 : h.Contains("제품명") ? 32 : h.Contains("공급업체") ? 23
                    : h.Contains("번호") ? 19 : 20;
                x.WriteStartElement("col"); x.WriteAttributeString("min", (c + 1).ToString()); x.WriteAttributeString("max", (c + 1).ToString());
                x.WriteAttributeString("width", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
                x.WriteAttributeString("customWidth", "1"); x.WriteEndElement();
            }
            x.WriteEndElement(); x.WriteStartElement("sheetData");
            int groupIndex = -1; int groupEnd = -1;
            for (int r = 0; r < rows.Count; r++) {
                if (index == 1 && r > groupEnd && r > 0) { groupIndex++; groupEnd = groupIndex < groups.Count ? groups[groupIndex].end : r; }
                int groupStyle = groupIndex % 2 == 0 ? 2 : 3;
                double rowHeight = r == 0 ? 36 : 27;
                // Excel does not auto-fit merged cells. Distribute the required display height across the ingredient rows.
                if (index == 1 && r > 0 && groupIndex < groups.Count) {
                    var g = groups[groupIndex]; int n = Math.Max(1, g.end - g.start + 1);
                    double needed = 27;
                    for (int c = 0; c < ingredientStart && c < rows[g.start].Length; c++) {
                        var v = rows[g.start][c] ?? "";
                        var h = rows[0][c];
                        int chars = h.Contains("주소") ? 29 : h.Contains("보호구") || h.Contains("반응성") ? 32 : h == "MSDS 파일명" ? 27 : h.Contains("제품명") ? 27 : 18;
                        int lines = v.Replace("\r", "").Split('\n').Sum(line => Math.Max(1, (line.Length + chars - 1) / chars));
                        needed = Math.Max(needed, lines * 17.5 + 9);
                    }
                    rowHeight = Math.Max(rowHeight, Math.Min(100, Math.Ceiling(needed / n)));
                }
                if (index == 2 && r > 0 && rows[r].Length > 4) {
                    var details = rows[r][4] ?? "";
                    rowHeight = Math.Min(90, Math.Max(27, Math.Ceiling(details.Length / 68.0) * 17 + 9));
                }
                x.WriteStartElement("row"); x.WriteAttributeString("r", (r + 1).ToString());
                x.WriteAttributeString("ht", rowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)); x.WriteAttributeString("customHeight", "1");
                for (int c = 0; c < rows[r].Length; c++) {
                    if (index == 1 && r > 0 && c < ingredientStart && groupIndex < groups.Count && r > groups[groupIndex].start) continue;
                    x.WriteStartElement("c"); x.WriteAttributeString("r", Column(c) + (r + 1));
                    x.WriteAttributeString("t", "inlineStr"); x.WriteAttributeString("s", (r == 0 ? 1 : index == 1 ? groupStyle : 0).ToString());
                    x.WriteStartElement("is"); x.WriteStartElement("t"); x.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                    x.WriteString(Sanitize(rows[r][c])); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
                }
                x.WriteEndElement();
            }
            x.WriteEndElement();
            if (index == 1 && ingredientStart > 0) {
                var merges = groups.Where(g => g.end > g.start).SelectMany(g => Enumerable.Range(0, ingredientStart)
                    .Select(c => $"{Column(c)}{g.start + 1}:{Column(c)}{g.end + 1}")).ToList();
                if (merges.Count > 0) {
                    x.WriteStartElement("mergeCells"); x.WriteAttributeString("count", merges.Count.ToString());
                    foreach (var m in merges) { x.WriteStartElement("mergeCell"); x.WriteAttributeString("ref", m); x.WriteEndElement(); }
                    x.WriteEndElement();
                }
            }
            // Filtering a vertically merged report would split document groups; retain filters on the flat log only.
            if (index == 2 && rows.Count > 0) {
                x.WriteStartElement("autoFilter"); x.WriteAttributeString("ref", $"A1:{Column(cols - 1)}{rows.Count}"); x.WriteEndElement();
            }
            x.WriteStartElement("pageMargins");
            x.WriteAttributeString("left", "0.25"); x.WriteAttributeString("right", "0.25");
            x.WriteAttributeString("top", "0.5"); x.WriteAttributeString("bottom", "0.5");
            x.WriteAttributeString("header", "0.2"); x.WriteAttributeString("footer", "0.2"); x.WriteEndElement();
            x.WriteStartElement("pageSetup"); x.WriteAttributeString("orientation", "landscape"); x.WriteAttributeString("fitToWidth", "1"); x.WriteAttributeString("fitToHeight", "0"); x.WriteEndElement();
            x.WriteEndElement();
        });
        private static string Column(int n) { var s = ""; do { s = (char)('A' + n % 26) + s; n = n / 26 - 1; } while (n >= 0); return s; }
        private static string Sanitize(string? value) => new string((value ?? "").Where(c => c == '\t' || c == '\n' || c == '\r' || c >= ' ').Take(32767).ToArray());
        private static void Write(ZipArchive archive, string name, Action<XmlWriter> build) { var entry = archive.CreateEntry(name, CompressionLevel.Optimal); using var stream = entry.Open(); using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }); writer.WriteStartDocument(); build(writer); writer.WriteEndDocument(); }
    }
}
