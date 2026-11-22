using System;
using System.Collections.Generic;

namespace QCS.Analysis
{
    // Stage 2 contract only. No operational DB or MSDS pipeline connection.
    // Implementations must commit all three records in one transaction.
    internal sealed record AiCorrectionSnapshot(
        string SnapshotId, string PdfSha256, string FieldKey,
        string? OriginalValue, string? SourceText, string? SourceContext);

    internal sealed record AiCorrectionAudit(
        string EvaluationId, string SnapshotId, string RuleId, int RuleVersion,
        AiDecisionKind Decision, string Reason, string? BeforeValue,
        string? AfterValue, bool ChangedValue);

    internal interface IAiCorrectionWriteTransaction : IDisposable
    {
        // Must reject a duplicate snapshot with different original content.
        void InsertSnapshotIfAbsent(AiCorrectionSnapshot snapshot);
        // Must reject a duplicate rule version with different content.
        void InsertRuleIfAbsent(AiCorrectionRule rule);
        // Must be idempotent for an identical evaluation ID and payload.
        void InsertAuditIfAbsent(AiCorrectionAudit audit);
        void Commit();
    }

    internal interface IAiCorrectionTransactionFactory
    {
        IAiCorrectionWriteTransaction BeginTransaction();
    }

    internal static class AiCorrectionPersistenceContract
    {
        internal static void SaveAtomically(
            IAiCorrectionTransactionFactory factory,
            AiCorrectionSnapshot snapshot,
            AiCorrectionRule rule,
            AiCorrectionAudit audit)
        {
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(rule);
            ArgumentNullException.ThrowIfNull(audit);
            if (string.IsNullOrWhiteSpace(snapshot.SnapshotId) ||
                string.IsNullOrWhiteSpace(audit.EvaluationId) ||
                string.IsNullOrWhiteSpace(rule.Id) || rule.Version < 1 ||
                !string.Equals(snapshot.SnapshotId, audit.SnapshotId, StringComparison.Ordinal) ||
                !string.Equals(rule.Id, audit.RuleId, StringComparison.Ordinal) ||
                rule.Version != audit.RuleVersion ||
                !string.Equals(snapshot.FieldKey, rule.FieldKey, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid or inconsistent correction persistence identifiers.");
            if (audit.ChangedValue != (audit.Decision == AiDecisionKind.Apply))
                throw new ArgumentException("Audit change flag and decision disagree.");
            using var transaction = factory.BeginTransaction();
            transaction.InsertSnapshotIfAbsent(snapshot);
            transaction.InsertRuleIfAbsent(rule);
            transaction.InsertAuditIfAbsent(audit);
            // No commit on exception: transaction implementation must rollback on Dispose.
            transaction.Commit();
        }
    }
}
