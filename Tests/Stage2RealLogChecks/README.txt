QCS Stage2 S2-06: real-log seven-case safety-policy replay
1. Extract ZIP to QCS project root (no extra parent directory).
2. Open Tests/Stage2RealLogChecks/Stage2RealLogChecks.csproj in Visual Studio.
3. Ctrl+F5. Expect PASS: S03 seven-case safety-policy replay (7/7).
This checks only the 7 logged S03 cases against the approved corrosivity safety policy.
It does NOT reconstruct all historical S00-S06 candidate decisions or test a live QCS database.
The test reads bundled evidence CSV and uses existing Analysis/*.cs; it never writes the operational database.
