using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace QCS.Analysis
{
    // Stage 2: isolated, side-effect-free decision engine. Not wired into production.
    internal enum AiRuleKind { DocumentSpecific, Expression, Conditional }
    internal enum AiDecisionKind { Apply, Suggest, Exclude }

    internal sealed record AiCorrectionInput(
        string FieldKey, string? CurrentValue, string? PdfSha256,
        string? SourceText, string? SourceContext, string? CasNumber = null);

    internal sealed record AiCorrectionRule(
        string Id, int Version, AiRuleKind Kind, string FieldKey,
        string? PdfSha256, string? ExpectedSourceText, string? ReplacementValue,
        bool Approved, bool Enabled, string? RequiredContext = null,
        string? RequiredCasNumber = null);

    internal sealed record AiCorrectionDecision(
        AiDecisionKind Kind, string? Value, string Reason,
        IReadOnlyList<string> RuleIds, bool ChangesValue);

    internal static class AiCorrectionDecisionEngine
    {
        private static string Normalize(string? text) => Regex.Replace(
            (text ?? string.Empty).Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim();

        private static bool Same(string? a, string? b) =>
            string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

        private static bool IsHash(string? value) =>
            !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[0-9a-fA-F]{64}$");

        internal static AiCorrectionDecision Evaluate(AiCorrectionInput input, IEnumerable<AiCorrectionRule> rules)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(rules);
            if (string.IsNullOrWhiteSpace(input.FieldKey))
                return new(AiDecisionKind.Exclude, null, "FIELD_MISSING", Array.Empty<string>(), false);

            // The approved stage-1 contract does not define persisted corrosivity values.
            // Do not automatically write this field until the contract is validated.
            if (Same(input.FieldKey, "msds_corrosive_flag"))
                return new(AiDecisionKind.Suggest, null, "CORROSIVITY_CONTRACT_PENDING",
                    Array.Empty<string>(), false);

            var eligible = new List<AiCorrectionRule>();
            bool reviewNeeded = false;
            foreach (var rule in rules.OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Version))
            {
                if (!rule.Approved || !rule.Enabled || rule.Version < 1 ||
                    string.IsNullOrWhiteSpace(rule.Id) || !Same(rule.FieldKey, input.FieldKey)) continue;

                if (rule.Kind == AiRuleKind.DocumentSpecific)
                {
                    if (!IsHash(input.PdfSha256) || !IsHash(rule.PdfSha256) ||
                        !Same(input.PdfSha256, rule.PdfSha256)) continue;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(rule.ExpectedSourceText) ||
                        !Same(input.SourceText, rule.ExpectedSourceText)) continue;
                    if (rule.Kind == AiRuleKind.Conditional)
                    {
                        if (string.IsNullOrWhiteSpace(rule.RequiredContext) ||
                            string.IsNullOrWhiteSpace(input.SourceContext) ||
                            !Normalize(input.SourceContext).Contains(Normalize(rule.RequiredContext), StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrWhiteSpace(rule.RequiredCasNumber) &&
                            !Same(rule.RequiredCasNumber, input.CasNumber)) continue;
                    }
                }
                // Numeric exposure limits require CAS, type, unit, and evidence checks.
                // Those field-specific checks are not yet part of this isolated engine.
                if (input.FieldKey.Contains("twa", StringComparison.OrdinalIgnoreCase) ||
                    input.FieldKey.Contains("stel", StringComparison.OrdinalIgnoreCase))
                { reviewNeeded = true; continue; }

                if (rule.Kind != AiRuleKind.DocumentSpecific &&
                    !Same(rule.ExpectedSourceText, rule.ReplacementValue))
                {
                    // Cross-document semantic transformations need manual verification.
                    reviewNeeded = true;
                    continue;
                }
                eligible.Add(rule);
            }

            if (eligible.Count == 0)
                return new(reviewNeeded ? AiDecisionKind.Suggest : AiDecisionKind.Exclude,
                    null, reviewNeeded ? "SEMANTIC_REVIEW_REQUIRED" : "NO_ELIGIBLE_RULE",
                    Array.Empty<string>(), false);

            var values = eligible.Select(r => Normalize(r.ReplacementValue))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var ids = eligible.Select(r => r.Id).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (values.Length != 1)
                return new(AiDecisionKind.Suggest, null, "CONFLICTING_RULES", ids, false);
            if (Same(input.CurrentValue, values[0]))
                return new(AiDecisionKind.Exclude, null, "UNCHANGED_VALUE", ids, false);
            if (reviewNeeded)
                return new(AiDecisionKind.Suggest, null, "SEMANTIC_REVIEW_REQUIRED", ids, false);
            return new(AiDecisionKind.Apply, values[0], "ELIGIBLE", ids, true);
        }
    }
}
