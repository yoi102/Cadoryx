# Current F5 and D7 application candidate

This candidate combines the production development inspection identity feedback
with the exact qualified D7 revision 4 kernel and `8.0.1-preview.87.cu1` NuGet feed.
It copies 1196 current application/test/tool inputs and records 1200 after the D7
overlay. Production remains on public Preview.85 until a later integration boundary.

Run `prepare.py prepare`, then `build.ps1`. The preparation keeps all 50 projects,
non-OCCT dependency locks, original model prompts, assertions and budgets. The
freeze requires both full solution builds with zero warnings/errors, actual 7117
test discovery, all original 6991 names plus exactly the new 126 feedback names,
SDK13/native62 payload equality, and provider shared16 equality.

`run-full.ps1` starts native consumers only after exact SDK receipt and actual
idle checks. Coordinate the native lane with the existing SDK conversation;
preparation/build/discovery do not authorize starting during its reserved runtime.
`audit-full.py` checks complete TRX names, both source and DLL bytes, actual native
paths/hashes and the exact user-deferred STEP24 list. It reports every failure;
unknown failures are never converted into deferrals or passes.

The prior 6991 full run keeps its immutable source and results. The final candidate
still needs real provider, window/recovery/material and MiniPC performance checks.
A–F completion is not implied by any build, discovery or focused test result.
