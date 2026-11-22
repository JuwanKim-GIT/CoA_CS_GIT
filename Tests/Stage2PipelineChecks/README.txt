QCS Stage 2 decision-to-SQLite integration test

1. Extract this ZIP into C:\QCS\QCS-개발\ (merge Tests only).
2. Open Tests\Stage2PipelineChecks\Stage2PipelineChecks.csproj in Visual Studio.
3. Ctrl+F5. Expect 8/8 and exit code 0.

Uses existing Analysis\AiCorrectionDecisionEngine.cs and Analysis\AiCorrectionPersistenceContract.cs.
Uses a temporary SQLite database in the OS temp directory; no production DB writes.
Does not invoke PDF parsing or the production QCS application.
