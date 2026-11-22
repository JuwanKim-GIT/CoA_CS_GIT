using System;
using System.Text.RegularExpressions;

namespace QCS.Analysis
{
    // Stage 3: read-only interpretation of MSDS skin-hazard evidence.
    // This module never writes msds_corrosive_flag or changes existing engine decisions.
    internal enum AiSkinEvidenceDecision { Suggest, Exclude }
    internal sealed record AiSkinEvidenceResult(
        AiSkinEvidenceDecision Decision, string Reason, string? Classification,
        string? Section2Evidence, string? Section11Evidence);

    internal static class AiSkinHazardEvidence
    {
        private static string Clean(string? value) =>
            Regex.Replace((value ?? string.Empty).Normalize(System.Text.NormalizationForm.FormKC), @"\s+", " ").Trim();

        internal static AiSkinEvidenceResult Evaluate(string? section2, string? section11,
            bool section2ProvenanceConfirmed, bool section11ProvenanceConfirmed = true)
        {
            var s2 = Clean(section2);
            var s11 = Clean(section11);
            if (!section2ProvenanceConfirmed || s2.Length == 0)
                return new(AiSkinEvidenceDecision.Exclude, "SECTION2_EVIDENCE_MISSING", null, s2, s11);

            // Classification must be explicit; section 11 headings cannot establish it.
            var match = Regex.Match(s2,
                @"(?:피부\s*(?:부식성|자극성)|Skin\s*(?:Corrosion|Irritation))\s*(?:/\s*(?:피부\s*)?(?:부식성|자극성|Irritation))?\s*(?:[-–—]\s*)?(?:구분|분류|Category|Cat\.?|:|：)?\s*(1A|1B|1C|1|2|3)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                return new(AiSkinEvidenceDecision.Exclude, "NO_EXPLICIT_SECTION2_CLASSIFICATION", null, s2, s11);

            string category = match.Groups[1].Value.ToUpperInvariant();
            bool corrosion = Regex.IsMatch(match.Value, @"부식성|Corrosion", RegexOptions.IgnoreCase);
            string classification = (corrosion && category.StartsWith("1", StringComparison.Ordinal) ? "SKIN_CORROSION_" : "SKIN_IRRITATION_") + category;

            if (s11.Length > 0 && !section11ProvenanceConfirmed)
                return new(AiSkinEvidenceDecision.Suggest, "SECTION11_PROVENANCE_UNCONFIRMED", classification, s2, s11);
            if (Regex.IsMatch(s11, @"(?:피부\s*자극\s*(?:없음|없다|없|관찰되지)|피부\s*부식\s*(?:없음|없다|없)|no\s+skin\s+(?:irritation|corrosion)|not\s+(?:irritating|corrosive))", RegexOptions.IgnoreCase))
                return new(AiSkinEvidenceDecision.Suggest, "SECTION2_SECTION11_CONFLICT_REVIEW", classification, s2, s11);

            // The persisted TEXT value contract is intentionally not defined at this stage.
            return new(AiSkinEvidenceDecision.Suggest, "CORROSIVITY_CONTRACT_PENDING", classification, s2, s11);
        }
    }
}
