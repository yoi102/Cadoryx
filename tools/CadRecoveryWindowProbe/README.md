# Recovery window acceptance probes

These two auxiliary programs retain the original 24 viewport recovery cases
and 36 production main-window status cases. They use a supplied frozen WPF
payload; they neither compile the application nor consume a second SDK package.
Their C# files are byte-identical to the previously prepared acceptance sources.

Build either project with an absolute `CandidatePayload`, for example:

```powershell
dotnet publish tools/CadRecoveryWindowProbe/ViewportRecovery/WpfAutoRecoveryConsumer.csproj -c Release --self-contained false -p:CandidatePayload=C:/absolute/frozen/workspace/Cadoryx.wpf/bin/Release/net10.0-windows/win-x64 -o C:/absolute/fresh/recovery24
dotnet publish tools/CadRecoveryWindowProbe/MainStatusRecovery/MainStatusRecovery.csproj -c Release --self-contained false -p:CandidatePayload=C:/absolute/frozen/workspace/Cadoryx.wpf/bin/Release/net10.0-windows/win-x64 -o C:/absolute/fresh/main-status36
```

The viewport executable takes one fresh output directory. The status library is
a .NET startup hook for the supplied production executable, using its existing
`--a5-recovery-status-smoke <fresh-output>` argument and memory asset store.
The current coordinated runner is
`capability-evidence/diagnostics/af1081-local86/run-recovery.ps1`; it verifies the
completed regression gate, candidate and fixture hashes, actual loaded native
modules, raw logs and original deadlines before accepting a process result.

Both programs create owned WPF windows and use real native presentation loss or
owner disposal. They do not inject mouse or keyboard input. Notify the user
before running; serialize all OCCT consumers and preserve other user windows.
Never replace the original 30-second readiness or 900-second observation budget,
case matrix, document/history/asset/camera checks, 48 saved documents, or 52/144
full-image requirements. A successful build or process exit does not substitute
for independent result auditing and direct review of the full images.

These checks do not establish physical GPU removal, an actual absent/invalid
graphics context, physical display scanout, or interactive pointer acceptance.
