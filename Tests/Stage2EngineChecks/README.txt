QCS Stage 2 standalone test runner (no QCS operational DB access)

1. Unzip at the QCS project root, preserving Tests/Stage2EngineChecks.
2. Open a terminal at QCS project root.
3. Run: dotnet run --project Tests/Stage2EngineChecks/Stage2EngineChecks.csproj -c Release
4. Exit code 0 means all built-in checks returned no failures.

The runner compiles the existing Analysis/*.cs files by relative reference.
It does not edit QCS application code or database. It does not test real SQLite
persistence, WPF execution, or SMB concurrency.
