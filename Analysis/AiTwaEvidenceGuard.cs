using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QCS.Analysis
{
    // S3-04: pure, read-only evidence gate. Does not write to QCS or bypass Stage 2.
    internal enum AiTwaDecision { Apply, Suggest, Exclude }
    internal sealed record AiTwaEvidence(string? SourceCas, string? TargetCas,
        string? SourceType, string? TargetType, string? SourceValue, string? ProposedValue,
        string? SourceUnit, string? ProposedUnit, string? SourceHours, string? TargetHours,
        string? SourceAuthority, string? TargetAuthority, bool SourceProvenanceConfirmed,
        bool SameDocument = true);
    internal sealed record AiTwaResult(AiTwaDecision Decision, string Reason, string? SafeValue);

    internal static class AiTwaEvidenceGuard
    {
        private static string N(string? v) => Regex.Replace((v ?? "").Normalize(System.Text.NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();
        private static string Cas(string? v) => Regex.Replace(N(v), @"\s+", "");
        private static bool Number(string? v, out decimal result) => decimal.TryParse(
            N(v), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out result) && result >= 0;
        private static string Unit(string? v) => N(v).Replace("³", "3").Replace("^3", "3").Replace(" ", "");
        private static string Hours(string? v) => N(v).Replace("HOURS", "H").Replace("HOUR", "H").Replace("시간", "H").Replace(" ", "");
        internal static AiTwaResult Evaluate(AiTwaEvidence e)
        {
            if (e is null) throw new ArgumentNullException(nameof(e));
            if (!e.SameDocument) return new(AiTwaDecision.Exclude, "CROSS_DOCUMENT_EVIDENCE", null);
            if (!e.SourceProvenanceConfirmed) return new(AiTwaDecision.Suggest, "SOURCE_PROVENANCE_MISSING", null);
            if (string.IsNullOrWhiteSpace(e.SourceCas) || string.IsNullOrWhiteSpace(e.TargetCas) ||
                string.IsNullOrWhiteSpace(e.SourceType) || string.IsNullOrWhiteSpace(e.TargetType) ||
                string.IsNullOrWhiteSpace(e.SourceUnit) || string.IsNullOrWhiteSpace(e.ProposedUnit) ||
                string.IsNullOrWhiteSpace(e.SourceHours) || string.IsNullOrWhiteSpace(e.TargetHours) ||
                string.IsNullOrWhiteSpace(e.SourceAuthority) || string.IsNullOrWhiteSpace(e.TargetAuthority))
                return new(AiTwaDecision.Suggest, "REQUIRED_CONTEXT_MISSING", null);
            if (!Regex.IsMatch(Cas(e.SourceCas), @"^\d{2,7}-\d{2}-\d$") ||
                !Regex.IsMatch(Cas(e.TargetCas), @"^\d{2,7}-\d{2}-\d$"))
                return new(AiTwaDecision.Suggest, "CAS_FORMAT_REVIEW", null);
            if (Cas(e.SourceCas) != Cas(e.TargetCas)) return new(AiTwaDecision.Exclude, "CAS_MISMATCH", null);
            if (N(e.SourceType) != "TWA" || N(e.TargetType) != "TWA")
                return new(AiTwaDecision.Exclude, "EXPOSURE_TYPE_MISMATCH", null);
            if (!Number(e.SourceValue, out var source) || !Number(e.ProposedValue, out var proposed))
                return new(AiTwaDecision.Suggest, "NUMERIC_EVIDENCE_MISSING", null);
            if (source != proposed) return new(AiTwaDecision.Exclude, "NUMERIC_VALUE_CHANGED", null);
            if (Unit(e.SourceUnit) != Unit(e.ProposedUnit)) return new(AiTwaDecision.Suggest, "UNIT_CONVERSION_REVIEW", null);
            if (Hours(e.SourceHours) != Hours(e.TargetHours)) return new(AiTwaDecision.Suggest, "EXPOSURE_PERIOD_MISMATCH", null);
            if (N(e.SourceAuthority) != N(e.TargetAuthority)) return new(AiTwaDecision.Suggest, "AUTHORITY_MISMATCH", null);
            // SafeValue is the exact original numeric evidence, not a cross-document replacement.
            return new(AiTwaDecision.Apply, "EXPRESSION_ONLY_SAME_EVIDENCE", e.SourceValue!.Trim());
        }
    }
}
