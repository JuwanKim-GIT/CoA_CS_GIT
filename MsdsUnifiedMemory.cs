using System;
using System.Collections.Generic;
using System.Linq;

namespace QCS
{
    // Single in-memory row table: one basic field or one ingredient property per row.
    internal sealed class MsdsUnifiedMemory
    {
        internal static readonly string[] IngredientCodes =
        { "content_raw", "content_min", "content_max", "content_calculated", "twa", "stel", "oral_ld50", "dermal_ld50", "inhalation_lc50" };
        internal static readonly string[] IngredientNames =
        { "함량원문", "최소(%)", "최대(%)", "산출값(%)", "TWA", "STEL", "경구 LD50", "경피 LD50", "흡입 LC50" };

        internal sealed class Row
        {
            // 16 logical columns from the BEA114 design.
            public string MsdsId { get; set; } = "";
            public string Category { get; set; } = "";
            public int? Order { get; set; }
            public string CasNumber { get; set; } = "";
            public string IngredientName { get; set; } = "";
            public string FieldCode { get; set; } = "";
            public string FieldName { get; set; } = "";
            public string ValueAi { get; set; } = "";
            public string? ValueCorrection { get; set; }
            public string? ValueMod { get; set; }
            public string FinalValue => ValueMod ?? (AiApplied && ValueCorrection != null ? ValueCorrection : ValueAi);
            public string LearningNo { get; set; } = "";
            public bool AiApplied { get; set; }
            public string Verification { get; set; } = "미검증";
            public int? SourcePage { get; set; }
            public string SourceEvidence { get; set; } = "";
            internal Row CopyForReview() => (Row)MemberwiseClone();
        }

        // An ingredient is a computed view over nine rows, never a second store.
        internal sealed class IngredientView
        {
            private readonly IReadOnlyList<Row> _rows;
            internal IngredientView(IReadOnlyList<Row> rows) { _rows = rows; }
            public int? Order => _rows[0].Order;
            public string CasNumber => _rows[0].CasNumber;
            public string Name => _rows[0].IngredientName;
            public string Ai(int index) => _rows.FirstOrDefault(r => r.FieldCode == IngredientCodes[index])?.ValueAi ?? "";
            public string OriginalForStorage(int index) => _rows.FirstOrDefault(r => r.FieldCode == IngredientCodes[index]) is { } row ? row.ValueAi : "";
            public string Final(int index) => _rows.FirstOrDefault(r => r.FieldCode == IngredientCodes[index])?.FinalValue ?? "";
        }

        internal sealed record NaturalStep(Row Target, string RuleId, int Version, string Instruction, string Kind, int Sequence,
            string Before, string After, int? Page, string Evidence, string Status, string? Error, DateTime At, string ScopeLabel = "");
        internal List<NaturalStep> NaturalHistory { get; } = new();
        internal string NaturalActor { get; set; } = "";
        public List<Row> Rows { get; } = new();
        public bool Confirmed { get; set; }
        public bool DisplayInitialized { get; set; }
        public IEnumerable<Row> Basics => Rows.Where(r => r.Category == "기본정보");
        public IEnumerable<IngredientView> Ingredients => Rows.Where(r => r.Category == "구성성분")
            .GroupBy(r => r.Order).OrderBy(g => g.Key)
            .Select(g => new IngredientView(g.ToList()));
        internal MsdsUnifiedMemory CopyForReview() {
            var copy=new MsdsUnifiedMemory(); var map=Rows.ToDictionary(r=>r,r=>r.CopyForReview());
            copy.Rows.AddRange(Rows.Select(r=>map[r]));
            foreach(var step in NaturalHistory) if(map.TryGetValue(step.Target,out var target)) copy.NaturalHistory.Add(step with { Target=target });
            return copy;
        }
        public void Clear() { Rows.Clear(); NaturalHistory.Clear(); NaturalActor = ""; Confirmed = false; DisplayInitialized = false; }
        public Row? Basic(string key) => Basics.FirstOrDefault(r => r.FieldCode == key);
        public Dictionary<string, string> FinalBasics() => Basics.ToDictionary(r => r.FieldCode, r => r.FinalValue, StringComparer.Ordinal);
        public void SaveBasic(string key, string value)
        {
            var row = Basic(key);
            if (row != null) row.ValueMod = value == (row.AiApplied && row.ValueCorrection != null ? row.ValueCorrection : row.ValueAi) ? null : value;
        }
        public Row? IngredientField(int order, string code) => Rows.FirstOrDefault(r => r.Category == "구성성분" && r.Order == order && r.FieldCode == code);
        public void SetIngredient(int order, int index, string value)
        {
            var row = IngredientField(order, IngredientCodes[index]);
            if (row != null) row.ValueMod = value == (row.AiApplied && row.ValueCorrection != null ? row.ValueCorrection : row.ValueAi) ? null : value;
        }
    }
}
