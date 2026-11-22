using System;
using System.Collections.Generic;
using System.Linq;

namespace QCS.Analysis
{
    // Read-only shadow comparison: never writes MSDS values, rules, or learning history.
    internal sealed record AiComparisonCase(
        string CaseId, AiCorrectionInput Input, IReadOnlyList<AiCorrectionRule> Rules,
        string? LegacyValue, bool LegacyChangedValue);

    internal sealed record AiComparisonRow(
        string CaseId, AiDecisionKind NewDecision, string? NewValue,
        string Reason, bool NewChangedValue, string? LegacyValue,
        bool LegacyChangedValue, bool DiffersFromLegacy,
        IReadOnlyList<string> RuleIds);

    internal sealed record AiComparisonSummary(
        int Total, int NewApply, int NewSuggest, int NewExclude,
        int LegacyChanges, int Disagreements);

    internal sealed record AiComparisonReport(
        IReadOnlyList<AiComparisonRow> Rows, AiComparisonSummary Summary);

    internal static class AiCorrectionComparison
    {
        internal static AiComparisonReport Compare(IEnumerable<AiComparisonCase> cases)
        {
            ArgumentNullException.ThrowIfNull(cases);
            var rows = new List<AiComparisonRow>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in cases)
            {
                ArgumentNullException.ThrowIfNull(item);
                if (string.IsNullOrWhiteSpace(item.CaseId) || !ids.Add(item.CaseId))
                    throw new ArgumentException("Comparison case IDs must be unique and nonempty.", nameof(cases));
                var decision = AiCorrectionDecisionEngine.Evaluate(item.Input, item.Rules);
                bool newChanged = decision.Kind == AiDecisionKind.Apply && decision.ChangesValue;
                bool differs = newChanged != item.LegacyChangedValue ||
                    (newChanged && !string.Equals(decision.Value?.Trim(), item.LegacyValue?.Trim(), StringComparison.OrdinalIgnoreCase));
                rows.Add(new AiComparisonRow(item.CaseId, decision.Kind, decision.Value,
                    decision.Reason, newChanged, item.LegacyValue, item.LegacyChangedValue,
                    differs, decision.RuleIds));
            }
            var summary = new AiComparisonSummary(rows.Count,
                rows.Count(x => x.NewDecision == AiDecisionKind.Apply),
                rows.Count(x => x.NewDecision == AiDecisionKind.Suggest),
                rows.Count(x => x.NewDecision == AiDecisionKind.Exclude),
                rows.Count(x => x.LegacyChangedValue),
                rows.Count(x => x.DiffersFromLegacy));
            return new AiComparisonReport(rows.AsReadOnly(), summary);
        }
    }
}
