"""Prepare a reviewable, hash-guarded upstream patch. Does not write OcctSharp."""
from pathlib import Path
import base64
import difflib
import hashlib
import json

here = Path(__file__).resolve().parent
upstream = Path(r'C:\Users\yoiri\source\repos\OcctSharp')
changes = {}

def edit(path, old, new):
    text = changes.get(path, (upstream / path).read_text(encoding='utf-8-sig'))
    if text.count(old) != 1:
        raise ValueError(f'Expected one replacement in {path}')
    changes[path] = text.replace(old, new)

def append(path, text):
    changes[path] = (upstream / path).read_text(encoding='utf-8-sig').rstrip() + '\n\n' + text

for file, destination in {
    'BooleanTopologyHistory.cpp': 'OcctSharp/src/OcctSharp.Native/src/Modeling/BooleanTopologyHistory.cpp',
    'BooleanHistoryModeling.cs': 'OcctSharp/src/OcctSharp.Modeling/LocalFeatures/BooleanHistoryModeling.cs',
    'BooleanTopologyHistoryTests.cs': 'OcctSharp/tests/OcctSharp.Runtime.Tests/BooleanTopologyHistoryTests.cs',
}.items():
    if (upstream / destination).exists():
        raise ValueError(f'New upstream file already exists: {destination}')
    changes[destination] = (here / file).read_text(encoding='utf-8')

edit('OcctSharp/src/OcctSharp.Native/CMakeLists.txt', '  src/Modeling/LocalFeatureData.cpp',
     '  src/Modeling/LocalFeatureData.cpp\n  src/Modeling/BooleanTopologyHistory.cpp')
edit('OcctSharp/src/OcctSharp.Native/include/OcctSharp.Native.LocalFeatures.h',
     '/* All-zero null buffers query counts.',
     '''/* One private graph copy and one Boolean build. Input slots retain each original
   full-map order; result slots are in the final returned topology. Operation 0/1/2
   means Fuse/Cut/Common. Both argument and tool groups must be nonempty. */
OCCTSHARP_API OcctSharp_Status OCCTSHARP_CALL occtsharp_boolean_topology_history(
  const OcctSharp_ShapeHandle* const* inputs, int32_t count, int32_t argument_count,
  int32_t operation, OcctSharp_FeatureResultHandle** output);

/* All-zero null buffers query counts.''')
edit('OcctSharp/src/OcctSharp/Interop/LocalFeatureNativeMethods.cs',
     'internal static partial class NativeMethods\n{',
     '''internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "occtsharp_boolean_topology_history")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeStatus BooleanTopologyHistory(nint* inputs, int count,
        int argumentCount, int operation, out nint result);
''')
edit('OcctSharp/src/OcctSharp.Modeling/LocalFeatures/LocalFeatureResult.cs',
     'LinearRibSlot, RevolutionRibSlot, Hole }', 'LinearRibSlot, RevolutionRibSlot, Hole, Boolean }')
append('docs/SPECIAL_CASES.md', '''## Cadoryx H2-B2: exact Boolean topology-history companion

Manual composition export `occtsharp_boolean_topology_history` extends the accepted
ADR-0088 copied-input/FeatureResult boundary. It reuses existing generated OCCT
bindings without changing their IDs; generated binding delta is zero. One added
manual C ABI entry composes BRepAlgoAPI_Fuse/Cut/Common with LocalFeatures::InputGraph
and History, preserving each source full-map index and the final result-map index.
No native builder, iterator or registry category escapes. Both input groups are
nonempty, inputs are bounded to 256, and algorithms run non-destructively without
parallel mutation. Missing final membership remains explicit. The existing feature
API is unchanged. Validation status is recorded in BOOLEAN_TOPOLOGY_HISTORY.md.
''')
append('docs/OWNERSHIP.md', '''## Boolean topology-history companion

BooleanHistoryModeling holds SafeHandle references to every RepairSnapshot input
while native InputGraph copies the entire graph. Builders and history iterators
remain call-local. Existing FeatureResult and Shape owners publish the result and
history shapes; input disposal after Build cannot invalidate outputs. History
result indices refer to the exact final graph, with final traversal orientation.
There is no new registry or borrowed pointer; existing release paths are reused.
''')
changes['docs/BOOLEAN_TOPOLOGY_HISTORY.md'] = '''# Boolean topology history for Cadoryx H2-B2

Development extension on the Preview.28 source baseline; not a public release.
BooleanHistoryModeling.Build accepts ordered RepairSnapshot inputs and an argument
group count, and returns owning LocalFeatureResult evidence. Source.ArgumentIndex
and TopologyIndex identify an input slot and its full-map topology. ResultTopologyIndex
identifies membership in the final result graph. PlanId scopes all relations to
this invocation. The supplied snapshots remain caller-owned and unchanged.

The implementation uses one private graph copy, then one non-destructive serial
Fuse/Cut/Common build. Modified/Generated/Deleted come from that builder; Unchanged
uses exact TShape/location membership. Split and shared-result relations are kept.
No geometry-proximity mapping or persistent naming is implied. Generated/modified
items absent from the final graph have no final index and consumers must fail closed.
No same-domain simplification or multi-step propagation is enabled.

New ABI export: occtsharp_boolean_topology_history. Existing fixed structs and
enum values remain unchanged; LocalFeatureOperation.Boolean is appended as 11.
The generated binding set is untouched. The manual composition is recorded in
SPECIAL_CASES; the existing ownership category is recorded in OWNERSHIP.

Validation: NOT RUN until actual Release/Debug native builds and the new runtime
tests execute. Tests cover analytic Cut/Fuse/Common volumes, input fingerprint
preservation, all source face/edge/vertex coverage, exact final slots, real split,
coincident independent-source shared targets, deletion, lifetime and invalid input.
Clean NuGet consumers and Cadoryx integration remain required before enabling
production tracing. Do not reuse the historical Preview.28 release PASS as proof
of this extension. Full release gates, signing and public publication are NOT RUN.
'''
edit('docs/STATUS.md', '- Current work: [Batch AC]',
     '- Development follow-up: [Cadoryx Boolean topology history](BOOLEAN_TOPOLOGY_HISTORY.md), source implementation prepared; native/runtime/package validation NOT RUN. The following Batch AC delivery describes the unchanged Preview.28 baseline.\n- Baseline work: [Batch AC]')

records = []
patch = []
for path, text in changes.items():
    target = upstream / path
    old = target.read_bytes() if target.exists() else None
    new = text.replace('\r\n', '\n').encode('utf-8')
    records.append(dict(path=path, before=hashlib.sha256(old).hexdigest() if old is not None else None,
                        after=hashlib.sha256(new).hexdigest(), content=base64.b64encode(new).decode('ascii')))
    patch.extend(difflib.unified_diff(old.decode('utf-8-sig').splitlines(keepends=True) if old else [],
        text.splitlines(keepends=True), fromfile='a/'+path if old else '/dev/null', tofile='b/'+path))
(here / 'upstream.patch').write_text(''.join(patch), encoding='utf-8')
(here / 'changes.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
print(f'Prepared {len(records)} hash-guarded upstream changes; upstream unchanged.')
