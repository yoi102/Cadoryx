# Current application candidate for D7

This tool prepares the current 1082 application with the exact independently
received `8.0.1-preview.87.cu1` NuGet packages and the D7 revision 4 implementation.
Production continues to use public Preview.85 until qualification and integration.

`prepare.py prepare` freezes 1194 current inputs, overlays the application kernel
and the unchanged 144-case test source, and records 1198 candidate inputs. It keeps
all 50 projects and original model prompts, assertions and budgets.

`run-after-focused.ps1` waits for both complete original 99 plus new 144 tests to
pass before starting locked restore, both full solution builds, provider publish
and actual full test discovery. `prepare.py locks` checks that non-OCCT dependencies
stay identical. `prepare.py freeze` requires SDK13/native62 payload equality,
shared provider16 equality and actual discovery of all 6991 cases in both builds.

Discovery and builds are preparation, not full regression or product acceptance.
The precise user-deferred STEP24 failures, original window/recovery/material,
MiniPC performance and real provider checks remain distinct required records.
