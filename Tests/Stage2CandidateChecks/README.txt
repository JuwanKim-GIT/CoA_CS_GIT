Stage 2 candidate eligibility / matching checks
1. Extract this ZIP into the existing QCS project root (e.g. C:\QCS\QCS-개발).
2. Open Tests\Stage2CandidateChecks\Stage2CandidateChecks.csproj in Visual Studio.
3. Run Ctrl+F5. Expect 18/18 PASS and process exit code 0.
This project compiles the existing Analysis\AiCorrectionDecisionEngine.cs through a linked source reference.
No changes to production QCS files or databases; no PDF or production runtime is executed.
