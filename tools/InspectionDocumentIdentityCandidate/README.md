# Document identity filtering feedback candidate

The original workspace model supplied documentId as an object id filter and saw
empty bodies/parts despite unfiltered queries having objects in the same state.
This candidate explains all 18 existing object-filter roles and eight part-filter
roles after the original inspection guards. It preserves unknown-object empty
pages, typed GUID sharing, paging, selection and explicitly scoped part filtering.
Closing-requested and closing/disposed sessions do not return inspection feedback.

The implementation is isolated from production in
`artifacts/inspection-document-id-draft-1/workspace`. It changes one existing
ViewModels source file and adds one helper. SDK wrappers or schema are unchanged.

The managed probe directly invokes compiled assertions, using opaque metadata
and a kernel which forbids geometry work. It checks no OCCT module loads and no
document, history, generation, assets or selection changes. This is not a full
xUnit, native, desktop or real provider acceptance.

`prepare.py` copies the frozen current 1082+D7 input set. Restore/build ViewModels
with the parent's exact NuGet cache. `run-managed.ps1 -RunOrdinal 6` preserves
prior numbered logs and outputs; pick a new ordinal for a later run. `audit.py`
checks the final numbered runs, source, actual assemblies and old companion bytes.
It requires both baseline/candidate modes to have the same 184 case names: new125
and unchanged related59. Baseline failures and fixture errors are retained.

The original complete 6991 regression continues against its immutable candidate;
this source is not spliced into its running process or substituted for that result.
