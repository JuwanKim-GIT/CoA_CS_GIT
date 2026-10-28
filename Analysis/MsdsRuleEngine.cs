using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace QCS.Analysis
{
    public static class MsdsRuleEngine
    {
        private static readonly string[] Headings = { "", "화학제품|회사|identification", "유해성|위험성|hazard", "구성성분|composition", "응급조치|first.aid", "폭발|화재|fire", "누출|accidental", "취급|저장|handling", "노출방지|보호구|exposure", "물리|화학적|physical", "안정성|반응성|stability", "독성|toxicological", "환경|ecological", "폐기|disposal", "운송|transport", "법적|규제|regulatory", "참고|other.information" };
        private sealed class Row { public int Page; public int Section; public string Text = ""; }
        public static void Apply(MsdsAnalysisResult result, MsdsPdfTextResult source, MsdsRuleSet rules)
        {
            var rows = GetRows(source);
            foreach (var field in rules.Fields)
            {
                var subset = rows.Where(x => field.Section == 0 || x.Section == field.Section).ToList();
                // Some PDFs put section headings at the END of a page. When the
                // normal section scope contains no matching candidate, retry
                // on pages containing that section heading (not entire document).
                var fallback = field.Section == 0 ? new List<Row>() : GetHeadingPageRows(source, field.Section);
                // Some SDS documents use "1. 정보" instead of the standard section title.
                // For section 1 only, use page 1 as an additional fallback scope.
                if (field.Section == 1 && source.Pages.Count > 0)
                {
                    var firstPage = source.Pages[0];
                    var firstPageRows = Regex.Split(firstPage.Text ?? "", @"\r\n|\n|\r")
                        .Select(x => x.Trim()).Where(x => x.Length > 0)
                        .Select(x => new Row { Page = firstPage.PageNumber, Section = 1, Text = x });
                    fallback = fallback.Concat(firstPageRows)
                        .GroupBy(x => (x.Page, x.Text)).Select(g => g.First()).ToList();
                }
                if (field.Kind == "hazard_codes")
                {
                    var codes = subset.SelectMany(r => field.Patterns.SelectMany(p => Matches(r.Text, p)))
                        .Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
                    if (codes.Count == 0 && fallback.Count > 0)
                        codes = fallback.SelectMany(r => field.Patterns.SelectMany(p => Matches(r.Text, p)))
                            .Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
                    if (codes.Count > 0)
                    {
                        var origin = subset.Concat(fallback).First(r => field.Patterns.Any(p => Matches(r.Text, p).Count > 0));
                        Add(result, field.Key, string.Join(", ", codes), origin);
                    }
                    continue;
                }
                bool found = false;
                // Prefer genuine section rows; use heading-page fallback only
                // if the first pass did not yield a valid value.
                foreach (var candidates in new[] { subset, fallback })
                {
                if (found || candidates.Count == 0) continue;
                for (int i = 0; i < candidates.Count && !found; i++)
                {
                    foreach (var pattern in field.Patterns)
                    {
                        var match = SafeMatch(candidates[i].Text, pattern);
                        if (!match.Success) continue;
                        var value = match.Groups[1].Value.Trim().Trim(':', '：', ' ', '\t');
                        if (value.Length == 0 && field.AllowNextLine && i + 1 < candidates.Count && candidates[i + 1].Page == candidates[i].Page)
                            {
                            var next = candidates[i + 1].Text.Trim();
                            // A new field label is not a value of the preceding field.
                            if (!Regex.IsMatch(next, @"^(?:회사명|등록회사명|공급업체명|제조자명|주소|전화|Tel|Phone|긴급전화번호|제품번호|화학물질명|제품명|제품\s*식별명|수입업체|Manufacturer\s*/)", RegexOptions.IgnoreCase))
                                value = next;
                        }
                        value = Cleanup(value, field);
                        if (field.Key == "msds_product_name" &&
                            (Regex.IsMatch(value, @"^(?:㈜|\(주\)|주식회사|회사명|공급업체|제조자|Manufacturer)", RegexOptions.IgnoreCase) ||
                             Regex.IsMatch(value, @"^(?:제품\s*식별명|제품명)$", RegexOptions.IgnoreCase))) continue;
                        if (field.Key == "msds_supplier_address" && value.Length > 0)
                            value = ExtendAddress(value, candidates, i);
                        if (field.Kind == "date") value = NormalizeDate(value);
                        if (value.Length < 2 || value.Length > 250 || IsLabelOnly(value)) continue;
                        Add(result, field.Key, value, candidates[i]);
                        found = true;
                        break;
                    }
                }
                }
            }
        }
        private static List<Row> GetHeadingPageRows(MsdsPdfTextResult source, int section)
        {
            var pages = new HashSet<int>();
            foreach (var page in source.Pages)
                foreach (var line in Regex.Split(page.Text ?? "", @"\r\n|\n|\r"))
                {
                    var m = Regex.Match(line.Trim(), @"^(?:section\s*)?(1[0-6]|[1-9])\s*(?:[.:]|항\s*[:：]?)\s*(.*)$", RegexOptions.IgnoreCase);
                    if (m.Success && int.Parse(m.Groups[1].Value) == section &&
                        Regex.IsMatch(m.Groups[2].Value, Headings[section], RegexOptions.IgnoreCase))
                        pages.Add(page.PageNumber);
                }
            return source.Pages.Where(p => pages.Contains(p.PageNumber))
                .SelectMany(p => Regex.Split(p.Text ?? "", @"\r\n|\n|\r")
                    .Select(x => x.Trim()).Where(x => x.Length > 0)
                    .Select(x => new Row { Page = p.PageNumber, Section = section, Text = x })).ToList();
        }
        private static List<Row> GetRows(MsdsPdfTextResult source)
        {
            var rows = new List<Row>();
            int active = 0;
            foreach (var page in source.Pages)
                foreach (var line in Regex.Split(page.Text ?? "", @"\r\n|\n|\r").Select(x => x.Trim()).Where(x => x.Length > 0))
                {
                    var heading = Regex.Match(line, @"^\s*(?:section\s*)?(1[0-6]|[1-9])\s*(?:[.:]|항\s*[:：]?)\s*(.*)$", RegexOptions.IgnoreCase);
                    if (heading.Success)
                    {
                        int n = int.Parse(heading.Groups[1].Value);
                        if (Regex.IsMatch(heading.Groups[2].Value, Headings[n], RegexOptions.IgnoreCase)) active = n;
                    }
                    rows.Add(new Row { Page = page.PageNumber, Section = active, Text = line });
                }
            return rows;
        }
        private static Match SafeMatch(string input, string pattern) => Regex.Match(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        private static MatchCollection Matches(string input, string pattern) => Regex.Matches(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        private static string Cleanup(string value, MsdsFieldRule rule)
        {
            foreach (var label in rule.StopLabels ?? new List<string>())
            {
                var match = SafeMatch(value, @"\s*(?:" + label + @")");
                if (match.Success && match.Index > 0) value = value.Substring(0, match.Index).Trim();
            }
            // Some PDFs repeat labels during text extraction.
            value = Regex.Replace(value, @"^(?:제품명|주소|등록회사명|인화점)\s*[:：]?", "", RegexOptions.IgnoreCase).Trim();
            return value;
        }
        private static string ExtendAddress(string value, List<Row> rows, int index)
        {
            // Continue only for clearly wrapped address lines, never across a new label.
            for (int j = index + 1; j < rows.Count && j <= index + 3; j++)
            {
                if (rows[j].Page != rows[index].Page) break;
                var next = rows[j].Text.Trim();
                if (Regex.IsMatch(next, @"^(?:전화|Tel|Fax|Phone|E-mail|긴급|회사명|수입업체|제품명|[a-z]\)|[1-9]\s*항)", RegexOptions.IgnoreCase)) break;
                // Restrict continuation to English address fragments to prevent
                // attaching unrelated Korean prose or headings.
                if (!Regex.IsMatch(next, @"^[A-Za-z0-9][A-Za-z0-9 ,./()\-]*$") || next.Length > 90) break;
                value += " " + next;
            }
            return value;
        }
        private static bool IsLabelOnly(string value) => Regex.IsMatch(value, @"^(?:제품\s*식별명|제품명|공급자\s*정보|주소|전화번호|등록회사명|자료\s*없음|해당\s*없음)$", RegexOptions.IgnoreCase);
        private static string NormalizeDate(string input)
        {
            var m = Regex.Match(input, @"\b(\d{1,4})[-./](\d{1,2})[-./](\d{1,4})\b");
            if (!m.Success) return "";
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value), c = int.Parse(m.Groups[3].Value);
            int year, month, day;
            if (m.Groups[1].Length == 4) { year = a; month = b; day = c; }
            else if (m.Groups[3].Length == 4) { year = c; month = b; day = a; } // DD-MM-YYYY
            else return ""; // ambiguous; do not guess
            try { return new DateTime(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
            catch { return ""; }
        }
        private static void Add(MsdsAnalysisResult result, string key, string value, Row row)
        {
            result.Fields.Add(new MsdsExtractedField { FieldKey = key, Value = value, Status = MsdsFieldStatus.NeedsReview, SourcePage = row.Page, SourceText = row.Text });
        }
    }
}
