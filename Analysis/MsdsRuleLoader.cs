using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QCS.Analysis
{
    public sealed class MsdsRuleSet
    {
        public List<MsdsFieldRule> Fields { get; set; } = new();
    }
    public sealed class MsdsFieldRule
    {
        public string Key { get; set; } = "";
        public int Section { get; set; } = 0; // 0 = all pages
        public List<string> Patterns { get; set; } = new(); // group 1 is extracted value
        public string Kind { get; set; } = "text"; // text, date, hazard_codes
        public List<string> StopLabels { get; set; } = new();
        public bool AllowNextLine { get; set; }
    }
    public static class MsdsRuleLoader
    {
        public static string RulePath => Path.Combine(AppContext.BaseDirectory, "Analysis", "FieldRules.json");
        public static MsdsRuleSet Load()
        {
            if (!File.Exists(RulePath))
                throw new FileNotFoundException("파싱 규칙 파일이 없습니다. Analysis\\FieldRules.json을 복사하세요.", RulePath);
            var json = File.ReadAllText(RulePath);
            var rules = JsonSerializer.Deserialize<MsdsRuleSet>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new InvalidDataException("JSON 규칙이 비어 있습니다.");
            if (rules.Fields == null || rules.Fields.Count == 0)
                throw new InvalidDataException("JSON fields가 비어 있습니다.");
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in rules.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Key) || !keys.Add(field.Key))
                    throw new InvalidDataException("필드 key가 없거나 중복됩니다: " + field.Key);
                if (field.Section < 0 || field.Section > 16 || field.Patterns == null || field.Patterns.Count == 0)
                    throw new InvalidDataException("필드 section/patterns 오류: " + field.Key);
                if (field.Kind != "text" && field.Kind != "date" && field.Kind != "hazard_codes")
                    throw new InvalidDataException("지원하지 않는 kind: " + field.Kind);
                foreach (var p in field.Patterns)
                {
                    var regex = new Regex(p, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                    if (field.Kind != "hazard_codes" && regex.GetGroupNumbers().Length < 2)
                        throw new InvalidDataException(field.Key + " 패턴에는 캡처 그룹 (...)이 필요합니다.");
                }
            }
            return rules;
        }
    }
}
