# Analytic far-side termination prerequisite

This probe compares native Until construction with a private exact half-domain
of the original sphere/cylinder face. It does not implement a product feature,
change author selections, or infer topology identity. The existing near-only
face/Far rejection cases remain negative cases.

Supply the frozen WPF payload to publish, then pass that payload and a fresh
output directory to the executable. All OCCT consumers must run serially.
Do not start this probe while full tests, model, SDK or window consumers run.

```powershell
dotnet publish tools/FiniteSurfaceFarProbe/FiniteSurfaceFarProbe.csproj -c Release --self-contained false -p:CandidatePayload=C:/absolute/frozen/app -o C:/absolute/fresh/probe
```

The original 30 plain-profile cases retain their names and matrix. The expanded
90 cases also include a hole and a recursive island, covering sphere/cylinder,
identity/negative/tilted placement, raw full
Near, clipped full Near/Far, raw Far-only, and a Near-only/Far rejection. Checks
include independent analytic volume and cap area, complete start/profile
material, original finite cap coverage, unchanged source/stop BRep bytes,
exact validity/self-interference, and actual loaded native paths/hashes.
Any failed prerequisite prevents product adoption; there is no fitting fallback.

Independent circle/annulus and rectangle/ring integrals account for each material
component and its complete cap. Island cases require two changed solid components;
the original single-component cases still require one.

Volume and complete start/cap area checks retain the production D5 material
budgets: `max(1e-6, sourceVolume*1e-9)` and `max(1e-7, expectedArea*1e-9)`.
Successful rows report measured errors and their budgets separately. Earlier
unexecuted probe builds used looser checks; those source/build records are
preserved and do not qualify this stricter candidate.

Application recipe, reference, command, cancellation, undo/redo, parameter,
persistence, exchange and real creation/edit UI evidence remain separate work.

The current frozen 1081/86 builds are recorded in
`capability-evidence/diagnostics/af1081-local86/far90-build-verification.json`.
`audit-build.py` checks both payloads and freezes all 90 names;
`audit-runtime.py` independently checks the complete matrix, analytic values,
original BRep bytes, and loaded native identities. Failed prerequisites are
reported as failures and prevent product adoption.

`run-after-debug.ps1` follows the observed resumed Debug coordinator by PID and
creation time, requires its complete two-configuration audit, and runs both
prerequisite configurations serially. It then resumes the original model
scenarios after a fresh native-idle check. It stops on missing terminal evidence,
new regression failures or changed payloads; it never restarts another job.
The user's exact 24 deferred STEP failures remain failed results, listed in
`step24-user-deferral.json`; no parameterized method or other exchange case is
excluded. This follow-up does not control desktop input or run performance tests.

The model runner additionally observes a requested SDK CX/CY qualification
transition before native model launch. A request is not a grant: the root must
observe the complete Far terminal, the live provider hold, and actual native
idle state before sending an explicit SDK grant. Only explicit SDK return plus
fresh idle and frozen-payload checks can resume the original model scenarios.

Revision 5 stopped in the fifth case when destructive Common added edge
representations to the original selected stop. Revision 6 uses existing
`RepairSnapshot.CopyShape()` with independent construction and coverage copies;
TopoDS `Clone()` alone shares TShape. Original BRep byte checks remain mandatory.
Both revision 6 configurations completed 90 cases: 72 passed and 18 raw-full-near
volume checks failed. Their failures and all old outputs remain recorded; the
complete prerequisite is not passed and no product adoption is claimed.
Use revision argument `6` with both Python auditors and `-Revision 6` with the
coordinated runner/grant checker to preserve the original revision 5 evidence.
