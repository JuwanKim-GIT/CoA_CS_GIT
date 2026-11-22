using System;
using System.Collections.Generic;
using System.Linq;

namespace QCS.Analysis
{
    // Opt-in checks: not invoked from application startup or the MSDS pipeline.
    internal static class AiCorrectionEngineChecks
    {
        internal static IReadOnlyList<string> Run()
        {
            var failures = new List<string>();
            void Check(string id, bool passed) { if (!passed) failures.Add(id); }
            const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var input = new AiCorrectionInput("msds_manufacturer", "Old", hash, "Supplier A", "Section 1");
            var rule = new AiCorrectionRule("R1", 1, AiRuleKind.DocumentSpecific,
                input.FieldKey, hash, null, "Supplier A", true, true);
            AiCorrectionDecision Eval(AiCorrectionInput x, params AiCorrectionRule[] rules) =>
                AiCorrectionDecisionEngine.Evaluate(x, rules);

            Check("T2-01-deterministic", Enumerable.Range(0, 10)
                .Select(_ => Eval(input, rule))
                .Select(d => (d.Kind, d.Value, d.Reason, d.ChangesValue))
                .Distinct().Count() == 1);
            Check("T2-02-wrong-hash", Eval(input with { PdfSha256 = new string('b', 64) }, rule).Kind == AiDecisionKind.Exclude);
            Check("T2-02-corrosivity", Eval(input with { FieldKey = "msds_corrosive_flag" },
                rule with { FieldKey = "msds_corrosive_flag" }).Reason == "CORROSIVITY_CONTRACT_PENDING");
            Check("T2-02-twa", Eval(input with { FieldKey = "xylene_twa" },
                rule with { FieldKey = "xylene_twa", ReplacementValue = "100 ppm 8 Hr" }).Kind == AiDecisionKind.Suggest);
            Check("T2-03-same-value", Eval(input with { CurrentValue = "Supplier A" }, rule).Reason == "UNCHANGED_VALUE");
            Check("T2-03-empty-value", Eval(input, rule with { ReplacementValue = "" }).Kind == AiDecisionKind.Apply);
            Check("T2-04-conflict", Eval(input, rule,
                rule with { Id = "R2", ReplacementValue = "Supplier B" }).Reason == "CONFLICTING_RULES");
            Check("T2-05-disabled", Eval(input, rule with { Enabled = false }).Kind == AiDecisionKind.Exclude);
            Check("T2-05-unapproved", Eval(input, rule with { Approved = false }).Kind == AiDecisionKind.Exclude);
            Check("T2-05-source-unchanged", input.CurrentValue == "Old");
            var comparison = AiCorrectionComparison.Compare(new[] {
                new AiComparisonCase("C1", input, new[] { rule }, null, false)
            });
            Check("T2-06-readonly-comparison", comparison.Summary.Total == 1 &&
                comparison.Summary.Disagreements == 1 && input.CurrentValue == "Old");
            Check("T2-06-duplicate-case", Throws<ArgumentException>(() => AiCorrectionComparison.Compare(new[] {
                new AiComparisonCase("C1", input, new[] { rule }, null, false),
                new AiComparisonCase("C1", input, new[] { rule }, null, false)
            })));
            Check("T2-06-atomic-rollback", CheckRollback());
            Check("T2-06-idempotent", CheckIdempotent());
            Check("T2-06-reject-mismatch", CheckMismatch());
            return failures.AsReadOnly();
        }

        // A test double exercises the persistence contract without touching QCS DB.
        private sealed class MemoryFactory : IAiCorrectionTransactionFactory
        {
            internal readonly Dictionary<string, AiCorrectionSnapshot> Snapshots = new();
            internal readonly Dictionary<string, AiCorrectionRule> Rules = new();
            internal readonly Dictionary<string, AiCorrectionAudit> Audits = new();
            internal bool FailOnAudit;
            public IAiCorrectionWriteTransaction BeginTransaction() => new MemoryTx(this);

            private sealed class MemoryTx : IAiCorrectionWriteTransaction
            {
                private readonly MemoryFactory _owner;
                private readonly Dictionary<string, AiCorrectionSnapshot> _snapshots;
                private readonly Dictionary<string, AiCorrectionRule> _rules;
                private readonly Dictionary<string, AiCorrectionAudit> _audits;
                private bool _committed;
                internal MemoryTx(MemoryFactory owner)
                {
                    _owner = owner;
                    _snapshots = new(owner.Snapshots);
                    _rules = new(owner.Rules);
                    _audits = new(owner.Audits);
                }
                public void InsertSnapshotIfAbsent(AiCorrectionSnapshot item) => Insert(_snapshots, item.SnapshotId, item);
                public void InsertRuleIfAbsent(AiCorrectionRule item) => Insert(_rules, item.Id + ":" + item.Version, item);
                public void InsertAuditIfAbsent(AiCorrectionAudit item)
                {
                    if (_owner.FailOnAudit) throw new InvalidOperationException("Injected audit failure");
                    Insert(_audits, item.EvaluationId, item);
                }
                private static void Insert<T>(Dictionary<string, T> target, string key, T item)
                {
                    if (target.TryGetValue(key, out var existing))
                    {
                        if (!EqualityComparer<T>.Default.Equals(existing, item))
                            throw new InvalidOperationException("Conflicting duplicate record");
                        return;
                    }
                    target.Add(key, item);
                }
                public void Commit()
                {
                    if (_committed) throw new InvalidOperationException("Already committed");
                    _owner.Snapshots.Clear(); foreach (var x in _snapshots) _owner.Snapshots.Add(x.Key, x.Value);
                    _owner.Rules.Clear(); foreach (var x in _rules) _owner.Rules.Add(x.Key, x.Value);
                    _owner.Audits.Clear(); foreach (var x in _audits) _owner.Audits.Add(x.Key, x.Value);
                    _committed = true;
                }
                public void Dispose() { /* uncommitted copies are discarded */ }
            }
        }

        private static (AiCorrectionSnapshot, AiCorrectionRule, AiCorrectionAudit) PersistenceFixture()
        {
            const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            return (
                new AiCorrectionSnapshot("S1", hash, "msds_manufacturer", "Old", "Supplier A", "Section 1"),
                new AiCorrectionRule("R1", 1, AiRuleKind.DocumentSpecific, "msds_manufacturer", hash, null, "Supplier A", true, true),
                new AiCorrectionAudit("E1", "S1", "R1", 1, AiDecisionKind.Apply, "ELIGIBLE", "Old", "Supplier A", true));
        }
        private static bool CheckRollback()
        {
            var f = new MemoryFactory { FailOnAudit = true };
            var (s, r, a) = PersistenceFixture();
            bool failed = Throws<InvalidOperationException>(() => AiCorrectionPersistenceContract.SaveAtomically(f, s, r, a));
            return failed && f.Snapshots.Count == 0 && f.Rules.Count == 0 && f.Audits.Count == 0;
        }
        private static bool CheckIdempotent()
        {
            var f = new MemoryFactory();
            var (s, r, a) = PersistenceFixture();
            AiCorrectionPersistenceContract.SaveAtomically(f, s, r, a);
            AiCorrectionPersistenceContract.SaveAtomically(f, s, r, a);
            return f.Snapshots.Count == 1 && f.Rules.Count == 1 && f.Audits.Count == 1;
        }
        private static bool CheckMismatch()
        {
            var f = new MemoryFactory();
            var (s, r, a) = PersistenceFixture();
            return Throws<ArgumentException>(() => AiCorrectionPersistenceContract.SaveAtomically(
                f, s, r, a with { RuleVersion = 2 })) && f.Audits.Count == 0;
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); return false; }
            catch (T) { return true; }
        }
    }
}
