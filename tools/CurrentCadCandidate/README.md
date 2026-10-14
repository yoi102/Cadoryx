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

The original full Release/Debug runs are terminal: each has 7093 passes and the
exact original 24 user-deferred strict STEP failures, with no skipped tests or
new failures. Do not retry or relax those STEP cases while the deferral applies.

The independent WPF recovery-culture correction keeps the original frozen
candidate. Main36 revision2 prepares only its private initial empty document and
the selected application culture; all original recovery assertions remain.
Runtime5 passed 36/36, and all 42 unique full frames (144 total, 36 whole shells)
were directly reviewed. Shell RenderTargetBitmap images do not include native
HwndHost content; native images and physical desktop acceptance remain separate.

`qualify-ui-followups.py` creates new isolated original24 and D6 hosts by replacing
only the qualified WPF DLL/PDB; it also proves the unchanged provider16 and the
final WPF performance payload. Preserve existing outputs. Run original24 with
`run-recovery.ps1 -Label recovery24 -Iteration 3 -UseUiCulturePatch
-UiCultureRevision 2`, then `audit-recovery24.py 3 --ui-culture`. Run Main36 with
`-UseRevisedMainFixture -UseUiCulturePatch -UiCultureRevision 2` and a fresh
iteration only when a new source change justifies another run.

After both original provider outcomes and final recovery24 are audited,
`require-followup-evidence.py --record` records explicit resumed qualification;
it preserves the original failed queue and every model failure. D6 and the two
performance runners accept `-UseUiCulturePatch`; their original matrices and
budgets remain fixed. `integrate.py --with-ui-culture` checks the exact existing
UI fix instead of overwriting it; `--apply` additionally requires actual recovery,
model and full-image-review receipts. These tools do not mark A–F complete.

The root integration has now applied the 38 exact D7/development-NuGet changes
while preserving the proven recovery-culture fix. `audit-root-integration.py`
verifies root1200 inputs, locked restore and both full solution builds, SDK13 and
native62 in WPF/Tests, and the original Release absent-branch36 replay with actual
testhost62/process ancestry. It does not call that replay a root full7117 run.
Ordinary root DLLs embed the later Git revision; their raw equality with frozen
production DLLs is not claimed.

The original two direct MiniPC processes passed the fixed 6482ms target (4571.4236
and 4605.6025ms), each with 20 dual-pane cycles. Two production opens kept the whole
reference PNG. `audit-performance.py` records these independently of D6/material
desktop acceptance, the remaining model failure, and physical display latency.

The current224 SDK reservation was explicitly returned after terminal results
and actual idle observation. `require-current-return.ps1` now rejects reuse of
the earlier204 receipt; current224 requires a separately accepted explicit return
linked to the exact grant and SDK source receipt. Do not infer return from elapsed
time or an idle process snapshot.
