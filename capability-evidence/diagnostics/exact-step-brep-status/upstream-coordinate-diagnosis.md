# Exact STEP BRep status and coordinate-frame diagnosis

Audited 2026-10-13 from retained Release stock-only runs. The original
`BRepCheck_Analyzer(shape, true, false, true)` remains FAIL for the original,
identity and 1024 STEP files. No accepted repair, production generator change,
new package or closure of the original 24 application rejections is claimed.
The audit started no native job while the application validation lane was busy.

## Original Analyzer verdict and the earlier logging discrepancy

The additional original-file run observes `analyzer.IsExactMethod()==true` and
all 12 original `BRepCheck_Edge` results with exact and geometric controls true.
All 24 edge-to-face contexts were computed by that original Analyzer. Direct
`StatusOnShape(face)` reads retrieve its existing cached results; they do not
call `InContext`, rebuild the Analyzer, or replace its verdict.

The first iterator-based dump captured only 12 of the 24 edge-to-face contexts
in **each** of the three cases. Its omitted original contexts include both
failing end-cap uses. The additional original-file log still shows that iterator
under-recording, then directly retrieves all 24 cached statuses. It establishes:

| Imported face | Imported boundary edges | Original direct cached errors | Independent fresh exact edge errors | Original face self status |
|---|---|---|---|---|
| 1 | 1, 2, 3, 4 | None: all code 0 | None | 0 |
| 2 | 5, 6, 1, 7 | None: all code 0 | None | 0 |
| 3 | 8, 9, 5, 10 | None: all code 0 | None | 0 |
| 4 | 3, 11, 8, 12 | None: all code 0 | None | 0 |
| 5 | 4, 7, 10, 12 | edge 10: `InvalidCurveOnSurface(8)` | Same edge/code | `UnorientableShape(27)` |
| 6 | 2, 6, 9, 11 | edge 9: `InvalidCurveOnSurface(8)` | Same edge/code | `UnorientableShape(27)` |

The direct original and fresh independent status lists agree for every use:
22 code 0 and two code 8. There is **no demonstrated original cached-verdict
versus independent-checker disagreement**. The earlier absence of code 8 was
an incomplete observation, not evidence that the original Analyzer ignored it.
The binary-internal reason for iterator under-recording is not established.
Pinned source alone cannot prove the shipped DLL's internal iteration behavior;
the audit does not claim a source-to-binary reproduction of stock OCCT.

These indices belong only to each actual imported shape's `TopExp::MapShapes`
domain. They are not persistent author IDs. The earlier projection and mapped
writer/reader receipts retain their actual STEP binder/copier correspondences;
this status probe does not guess author correspondence from traversal order.

## Identity and 1024: actual statuses versus unobserved statuses

| Case | Full graph / edge uses | Actual face self errors | Iterator-recorded edge/face contexts | Direct cached contexts |
|---|---|---|---|---|
| Original, first run | 34 nodes / 24 uses | faces 5, 6: code 27 | 12; all recorded codes 0 | NOT RUN in this run |
| Identity | 34 / 24 | faces 5, 6: code 27 | 12; all recorded codes 0 | NOT RUN |
| 1024 | 34 / 24 | all six faces: code 27 | 12; 11 have codes 8 and 11, one code 0 | NOT RUN |
| Original, flags/direct supplement | 34 / 24 | faces 5, 6: code 27 | Same 12-context under-recording | All 24; two code 8, 22 code 0 |

The 11 **actually recorded** 1024 error contexts are:
face1 edges1/2/3/4; face2 edges5/6/7; face3 edges8/9; face4 edges11/12.
Face3-edge10 is the one recorded code-0 context. Code 11 is
`InvalidSameParameterFlag`. Missing cached statuses remain unobserved; the
independent coordinate traces do not fill them in as original Analyzer statuses.

Each first-run matrix explicitly retains these 12 unobserved contexts:
face2-edge1; face3-edge5; face4-edges3/8; all four uses of faces5 and6.
Every one was observed as `computedByOriginalAnalyzer=true`; computed is not
the same as its status having been logged. A complete direct-status supplement
for identity/1024 is prepared using the existing flags executable, but is
**NOT RUN**. No new candidate STEP or compiler invocation is needed for that
supplement. It must wait for an explicitly available native lane.

The constructor's exact=true setting is identified in both frozen executable
source snapshots. Runtime exact/geometric getter observations were recorded
only in the original flags supplement; they are not presented as getter
observations from the identity/1024 runs.

## Concrete check path and coordinate frames

Eight retained source/header files were independently compared byte-for-byte
with OCCT commit `b8f597c677811d1f9f4d8a97f5ae2825c0353a42`.
`source-provenance.json` binds their SHA256 values and exact upstream URLs.
The real path in that source is:

`Analyzer::Init -> Put -> Perform -> BRepCheck_Edge::InContext ->
BRepLib_ValidateEdge::Process -> processExact -> GeomLib_CheckCurveOnSurface`.

`Put` sets each edge result's exact flag before `Perform` computes its contexts.
`Process` selects the exact branch when exact and SameParameter are true; all
24 traced uses retain SameRange and SameParameter true. The older header comment
mentioning `BRepLib_CheckCurveOnSurface` does not identify this actual call path.

| Stage / source lines | Coordinates and parameters used | Observed consequence |
|---|---|---|
| `BRepCheck_Edge::Minimum`, lines 166-205 | Transforms the stored 3D curve with `edge.Location * curveRepresentation.Location` and builds its reference adaptor with transformed parameter endpoints. | Reference curve and line parameter ranges are in the physical/world frame. |
| Stored-pcurve context, lines 401-427 | Transforms the face surface with `face.Location * TFace.Location`, but constructs the 2D adaptor from the stored local pcurve and original local range. | Under non-unit location, surface UV and/or parameter range can disagree with the world reference. Failure adds codes 8 and 11 for these non-seam uses. |
| Missing planar pcurve, lines 461-511 | Transforms the plane into world coordinates, freshly projects the already world-space reference curve with `GeomProjLib::ProjectOnPlane`, then derives a 2D curve using `ProjLib_ProjectedCurve`. | It does not reuse the larger-definition on-demand pcurve returned by `BRep_Tool::CurveOnSurface`. Failure adds code 8 in this branch. |
| `BRepLib_ValidateEdge`, lines 42-79, 89-101 and 234-244 | Uses `GeomLib_CheckCurveOnSurface` for the exact calculation; tolerance comparison adds curve/surface precision to the stored raw edge tolerance. `GetMaxDistance` applies the separate factor 1.00001. | The unchanged application `1e-7` gate, raw BRep tolerance, internal precision correction and reported distance must remain distinct. |
| `BRepCheck_Analyzer`, lines 161-195 and 265-282 | An edge's code 8 sets `performwire=false`; the face result then receives `SetUnorientable`. | Face code 27 can be propagated from failed edge geometry. It is not by itself proof that the authored wire orientation is wrong. |

The independent `Trace` reconstructs those coordinate branches separately; it
does not instrument the original Analyzer's call stack or replace its status.
It also measures the location-aware physical comparison using
`BRepAdaptor_Curve`, `BRepAdaptor_Surface` and the local pcurve in a matching
parameter contract. Both outputs are retained side by side.

| Independent observation | Original | Identity | 1024 |
|---|---|---|---|
| End-cap fresh world projection: corrected kernel maximum | `7.729996834243354e-7` | `7.729979339318579e-7` | `7.729979339318579e-7` |
| End-cap raw checked edge tolerance | `3.2460099206167287e-7` | `3.246002646760699e-7` | `4.893393540980725e-7` |
| End-cap original-frame trace tolerance verdict | Both FAIL | Both FAIL | Both FAIL |
| Location-aware physical end-cap kernel maximum | `7.729919535048003e-7` | `7.729902040298176e-7` | `1.4236254946239222e-9` |
| Stored-pcurve trace failures | 0 | 0 | 15; 14 calculations not done |

In the 1024 case, line examples have local curve/pcurve range `[0,3072]`
and checker world range `[0,3]`. Fourteen stored-pcurve trace calculations are
not done and their distance is **null**, never zero. The remaining failing
stored spline use, face3-edge9, retains range approximately `[0,1]` but produces
corrected original-frame distance `3069.03069`, versus location-aware physical
distance `1.1102230246251565e-15`. Frame consistency is geometry-dependent;
not every stored pcurve fails.

These are continuous-domain kernel calculations, not certified mathematical
bounds. The smaller 1024 physical projection result does not change the original
stock FAIL. Neither this diagnosis nor `SetUnorientable` is a new exact STEP
representation. The mapped-scale candidate remains rejected, including its
changed raw tolerance reports, failed auxiliary volume calibration and unproved
independent standard scale semantics.

## Audit, preserved failures and next action

All actual native runs exited 1 with complete strict-failure summaries, retained
the imported BRep serialization, and observed 32 exact stock DLLs per process
without `OcctSharp.Native.dll`. Those are partial loaded-module snapshots, not
proof that all runtime dependencies were loaded. Build1's missing export/link
failure remains; build2 and build3 logs show successful Release compilation.
The diagnostic removed only its unavailable `SetExitIfToleranceExceeded` call:
the pinned exact branch does not use that early-exit setting, and the actual
Analyzer and `CheckTolerance(rawTolerance)` remain unchanged.

The first exact source text was recovered by reversing only the later flag/direct
logging additions and requiring its historical SHA256 before saving it. The
flags source is separately retained with its recorded SHA256. Shared header and
CMake identities are current audit observations, not newly invented historical
build receipts.

Authority directory:
`OcctSharp/artifacts/exact-step-brep-status-static/run-543ec745111b4b6c9f782aaa647cb28e/`.
It retains `both-result.json`, `original-flags-result.json`, original JSONL,
four separate `*-status-matrix.json` files and `independent-status-audit.json`.
The audit checks frozen inputs, executables, source snapshots, topology, every
use, stock DLL identities and direct-original/independent agreement. Its six
in-memory rejection fixtures reject a replaced strict verdict, relaxed gate,
missing direct context, independent override, loaded SDK bridge and lost node.
None runs OCCT or changes a native log.

```powershell
# Repository root; audits existing evidence only, no native executable launch.
./OcctSharp/tools/ExactStepBrepStatusProbe/audit-results.ps1 `
    -RunRoot ./OcctSharp/artifacts/exact-step-brep-status-static/run-543ec745111b4b6c9f782aaa647cb28e `
    -SelfTest
```

Audit and six rejection fixtures PASS. Native follow-up for identity/1024 direct
status, new exact representation, generated repair, original 24 product retest,
new packages and public publication: **NOT RUN**. Own diagnostic processes are
checked independently in `native-lane-release.json`; no other job is stopped.

No new admissible exact strategy has been established. The next implementation
requires a standards-correct representation that preserves the author graph,
original parameter domain, topology and tolerances, survives the unchanged stock
Analyzer, and then passes the original product checks. It must not re-run the
rejected knot insertion, planar replacement or scaled-location candidates as if
they were new solutions. Only a demonstrated repair belongs in generator inputs
and normal regeneration. See [projection diagnosis](EXACT_STEP_PROJECTION_DIAGNOSTIC.md)
and [mapped-scale review](EXACT_STEP_MAPPED_SCALE_REVIEW.md) for the retained
preceding evidence and rejected approaches.
