using System;
using System.Text.RegularExpressions;

namespace QCS.Analysis
{
    // Stage 3 S3-03: additional read-only veto on skin-hazard evidence.
    // It cannot approve an automatic write or override the existing storage contract.
    internal static class AiSkinSafetyGuard
    {
        internal static AiSkinEvidenceResult Evaluate(string? section2, string? section11,
            bool section2ProvenanceConfirmed, bool section11ProvenanceConfirmed = true,
            bool sameDocumentEvidence = true, bool supplierSimilarityOnly = false)
        {
            var baseline = AiSkinHazardEvidence.Evaluate(section2, section11,
                section2ProvenanceConfirmed, section11ProvenanceConfirmed);
            if (!sameDocumentEvidence || supplierSimilarityOnly)
                return baseline with { Decision = AiSkinEvidenceDecision.Exclude,
                    Reason = "CROSS_DOCUMENT_OR_SUPPLIER_ONLY", Classification = null };

            if (!section2ProvenanceConfirmed || string.IsNullOrWhiteSpace(section2))
                return baseline;

            string s2 = Regex.Replace(section2.Normalize(System.Text.NormalizationForm.FormKC), @"\s+", " ");
            string s11 = Regex.Replace((section11 ?? "").Normalize(System.Text.NormalizationForm.FormKC), @"\s+", " ");
            // Explicit negative classification, not merely an absent category.
            if (Regex.IsMatch(s2, @"(?:피부\s*(?:부식성|자극성)\s*(?:분류\s*)?(?:되지\s*않음|없음|해당\s*없음)|not\s+classified\s+(?:for\s+)?skin\s+(?:corrosion|irritation))", RegexOptions.IgnoreCase))
                return baseline with { Decision = AiSkinEvidenceDecision.Exclude,
                    Reason = "SECTION2_NEGATIVE_CLASSIFICATION", Classification = null };

            // Multiple distinct categories in the same section require human review.
            var categories = Regex.Matches(s2,
                @"(?:피부\s*(?:부식성|자극성)|Skin\s*(?:Corrosion|Irritation))\s*(?:/\s*(?:피부\s*)?(?:부식성|자극성|Irritation))?\s*(?:구분|분류|Category|Cat\.?|:|：|-)?\s*(1A|1B|1C|1|2|3)\b",
                RegexOptions.IgnoreCase);
            string? first = null;
            foreach (Match m in categories)
            {
                var kind = Regex.IsMatch(m.Value, "부식성|Corrosion", RegexOptions.IgnoreCase) ? "C" : "I";
                var key = kind + m.Groups[1].Value.ToUpperInvariant();
                if (first != null && first != key)
                    return baseline with { Decision = AiSkinEvidenceDecision.Suggest,
                        Reason = "SECTION2_MULTIPLE_CLASSIFICATIONS_REVIEW" };
                first = key;
            }
            if (baseline.Classification != null && Regex.IsMatch(s11,
                @"(?:피부\s*(?:자극|부식)\s*(?:이\s*)?(?:없음|없다|관찰되지\s*않음)|(?:no|without)\s+(?:evidence\s+of\s+)?skin\s+(?:irritation|corrosion)|not\s+(?:skin\s+)?(?:irritating|corrosive))",
                RegexOptions.IgnoreCase))
                return baseline with { Decision = AiSkinEvidenceDecision.Suggest,
                    Reason = "SECTION2_SECTION11_CONFLICT_REVIEW" };
            return baseline;
        }
    }
}
