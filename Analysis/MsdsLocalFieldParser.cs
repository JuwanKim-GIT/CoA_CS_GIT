using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace QCS.Analysis
{
    // MSDS 1차 규칙 추출: PDF 원본 전송 없음, OCR 없음, 추측값 자동 생성 없음.
    public sealed class MsdsLocalFieldParser
    {
        private static readonly Regex CasPattern = new(@"\b\d{2,7}-\d{2}-\d\b", RegexOptions.Compiled);
        private static readonly Regex ConcentrationPattern = new(@"(?<!\d)(?:[<>≤≥]\s*)?\d+(?:[.,]\d+)?\s*(?:[-~–—]\s*(?:[<>≤≥]\s*)?\d+(?:[.,]\d+)?)?\s*%?", RegexOptions.Compiled);
        private static readonly string[] ProductLabels = { "제품명", "제품 이름", "제품의 명칭", "제품 식별명", "Product name", "Product identifier", "Trade name", "물질명" };
        private static readonly string[] SupplierLabels = { "공급업체명", "등록회사명", "제조자명", "Supplier name", "Manufacturer name", "회사명" };

        public MsdsAnalysisResult Parse(MsdsPdfTextResult source, MsdsRuleSet? rules = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var result = new MsdsAnalysisResult { FilePath = source.FilePath, Engine = MsdsEngineMode.Local, RequiresManualEntry = !source.HasExtractableText };
            if (!source.HasExtractableText)
            {
                result.Warnings.Add("텍스트를 추출할 수 없어 수동 등록이 필요합니다.");
                return result;
            }
            var sec1 = SectionRows(source, 1);
            var sec2 = SectionRows(source, 2);
            var sec9 = SectionRows(source, 9);
            var sec14 = SectionRows(source, 14);
            // 매 분석마다 최신 외부 JSON 규칙을 사용한다.
            rules ??= MsdsRuleLoader.Load();
            MsdsRuleEngine.Apply(result, source, rules);
            // 성분 표의 열 관계를 검증하지 않은 CAS/함량은 임의로 연결하지 않는다.
            // 섹션 3에 있는 CAS 번호만 후보로 표시하며 함량은 보수적으로 공란 처리.
            var composition = SectionRows(source, 3);
            if (composition.Count == 0 || !composition.Any(r => CasPattern.IsMatch(r.line)))
            {
                // Heading order can be reversed by PDF text extraction.
                // Restrict fallback to pages explicitly containing section 3.
                var pages = AllRows(source).Where(r => Regex.IsMatch(r.line,
                    @"^3\s*[.:]\s*(?:구성성분|composition)", RegexOptions.IgnoreCase))
                    .Select(r => r.page).Distinct().ToHashSet();
                composition = AllRows(source).Where(r => pages.Contains(r.page)).ToList();
            }
            for (int i = 0; i < composition.Count; i++)
            {
                var line = composition[i].line;
                foreach (Match match in CasPattern.Matches(line))
                {
                    if (!IsValidCas(match.Value)) continue;
                    var name = line.Substring(0, match.Index).Trim(' ', ':', '|', '\t');
                    name = Regex.Replace(name, @"^(?:CAS\s*(?:번호|No\.?)?|식별자)\s*[:：]?", "", RegexOptions.IgnoreCase).Trim();
                    if (name.Length == 0)
                    {
                        for (int j = i - 1; j >= Math.Max(0, i - 4); j--)
                        {
                            var previous = composition[j].line;
                            if (CasPattern.IsMatch(previous)) break;
                            if (Regex.IsMatch(previous, @"^(?:물질명|성분명|화학명)\s*[:：]?\s*\S+"))
                            { name = Regex.Replace(previous, @"^(?:물질명|성분명|화학명)\s*[:：]?\s*", ""); break; }
                        }
                    }
                    var remainder = line.Substring(match.Index + match.Length).Trim();
                    var amount = ConcentrationPattern.Match(remainder);
                    string? concentration = amount.Success ? amount.Value.Trim() : null;
                    if (concentration == null && i + 1 < composition.Count && Regex.IsMatch(composition[i + 1].line, @"^함유량"))
                    {
                        var next = ConcentrationPattern.Match(composition[i + 1].line);
                        if (next.Success) concentration = next.Value.Trim();
                    }
                    if (concentration == null)
                    {
                        // A common layout is: name / CAS number / concentration.
                        for (int j = i + 1; j < Math.Min(composition.Count, i + 4); j++)
                        {
                            if (composition[j].page != composition[i].page || CasPattern.IsMatch(composition[j].line)) break;
                            var cm = Regex.Match(composition[j].line, @"^함유량\s*\(?%?\)?\s*[:：]?\s*([<>≤≥]?\s*\d+(?:[.,]\d+)?(?:\s*[-~–—]\s*\d+(?:[.,]\d+)?)?\s*%?)", RegexOptions.IgnoreCase);
                            if (cm.Success) { concentration = cm.Groups[1].Value.Trim(); break; }
                        }
                    }
                    result.Ingredients.Add(new MsdsIngredient
                    {
                        CasNumber = match.Value,
                        Name = name.Length > 0 && name.Length < 140 ? name : null,
                        ConcentrationRaw = concentration,
                        SourcePage = composition[i].page,
                        SourceText = line
                    });
                }
            }
            if (result.Ingredients.Count == 0) result.Warnings.Add("3항 구성성분의 CAS 번호를 확인하지 못했습니다.");
            result.Warnings.Add("규칙 추출 결과는 미검증 후보입니다. 원문과 대조 후 사용하세요.");
            return result;
        }

        private static List<(int page, string line)> AllRows(MsdsPdfTextResult source) =>
            source.Pages.SelectMany(p => SplitLines(p.Text).Select(line => (p.PageNumber, line))).ToList();

        private static List<(int page, string line)> SectionRows(MsdsPdfTextResult source, int section)
        {
            var result = new List<(int page, string line)>();
            int active = 0;
            var headings = new[] { "", "화학제품|회사|identification", "유해성|위험성|hazard", "구성성분|composition", "응급조치|first.aid", "폭발|화재|fire", "누출|accidental", "취급|저장|handling", "노출방지|보호구|exposure", "물리|화학적|physical", "안정성|반응성|stability", "독성|toxicological", "환경|ecological", "폐기|disposal", "운송|transport", "법적|규제|regulatory", "참고|other.information" };
            foreach (var row in AllRows(source))
            {
                var m = Regex.Match(row.line, @"^\s*(?:section\s*)?(1[0-6]|[1-9])\s*(?:[.:]|항\s*[:：]?)\s*(.*)$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    int n = int.Parse(m.Groups[1].Value);
                    if (Regex.IsMatch(m.Groups[2].Value, headings[n], RegexOptions.IgnoreCase)) active = n;
                }
                if (active == section) result.Add(row);
            }
            return result;
        }

        private static void Put(MsdsAnalysisResult result, string key, string value, (int page, string line) origin)
        {
            value = value.Trim().Trim(':', '：');
            if (value.Length < 2 || value.Length > 200 || Regex.IsMatch(value, @"^(정보|자료 없음|해당없음)$")) return;
            var f = result.Fields.FirstOrDefault(x => x.FieldKey == key);
            if (f == null) { f = new MsdsExtractedField { FieldKey = key }; result.Fields.Add(f); }
            f.Value = value; f.Status = MsdsFieldStatus.NeedsReview;
            f.SourcePage = origin.page; f.SourceText = origin.line;
        }

        private static void Extract(MsdsAnalysisResult result, List<(int page, string line)> rows, string key, string pattern)
        {
            foreach (var row in rows)
            {
                var m = Regex.Match(row.line, pattern, RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                Put(result, key, m.Groups[1].Value, row);
                if (result.Fields.Any(x => x.FieldKey == key && x.Value != null)) break;
            }
        }

        private static void AddField(MsdsAnalysisResult result, List<(int page, string line)> rows, string key, string[] labels)
        {
            for (int i = 0; i < rows.Count; i++)
                foreach (var label in labels)
                {
                    var m = Regex.Match(rows[i].line, @"(?i)(?:^|\s)" + Regex.Escape(label) + @"\s*[:：]?\s*(.*)$");
                    if (!m.Success) continue;
                    var candidate = m.Groups[1].Value.Trim();
                    if (candidate.Length == 0 && i + 1 < rows.Count) candidate = rows[i + 1].line;
                    if (labels.Any(l => string.Equals(candidate, l, StringComparison.OrdinalIgnoreCase))) continue;
                    Put(result, key, candidate, rows[i]);
                    if (result.Fields.Any(f => f.FieldKey == key && f.Value != null)) return;
                }
        }

        private static List<string> SplitLines(string text) =>
            (text ?? "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        private static bool IsValidCas(string cas)
        {
            var digits = cas.Replace("-", "");
            int sum = 0, multiplier = 1;
            for (int i = digits.Length - 2; i >= 0; i--) sum += (digits[i] - '0') * multiplier++;
            return sum % 10 == digits[^1] - '0';
        }
    }
}
