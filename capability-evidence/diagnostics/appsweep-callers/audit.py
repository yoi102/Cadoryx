"""Record a bounded source call-chain audit; this does not execute native code."""
from datetime import datetime, timezone
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SDK = ROOT.parent / 'OcctSharp' / 'OcctSharp'
OUT = Path(__file__).resolve().parent
ARCHIVE = ROOT / 'artifacts/appsweep-caller-static-1'
COMMIT = 'b8f597c677811d1f9f4d8a97f5ae2825c0353a42'
receipt = json.loads((ARCHIVE / 'source-receipt.json').read_text(encoding='utf-8'))
assert receipt['commit'] == COMMIT
sources = []

def record(path, snippets, role, expected_hash=None):
    data = path.read_bytes()
    digest = hashlib.sha256(data).hexdigest().upper()
    if expected_hash is not None:
        assert digest == expected_hash, path
    content = data.decode('utf-8-sig')
    lines = content.splitlines()
    excerpts = []
    for needle in snippets:
        matches = [index for index, line in enumerate(lines) if needle in line]
        assert matches, (path, needle)
        excerpts.append({'needle': needle, 'lines': [i + 1 for i in matches],
                         'text': [lines[i] for i in matches]})
    row = {'path': str(path), 'sha256': digest, 'role': role, 'excerpts': excerpts}
    sources.append(row)
    return content

for row in receipt['sources']:
    assert 'error' not in row
    assert hashlib.sha256(Path(row['path']).read_bytes()).hexdigest().upper() == row['sha256']

record(ROOT / 'Cadoryx.Kernel.Occt/OcctGuidedAuthoringGeometry.cs',
       ['if(straightDelta is {} delta)', 'using var solid=face.Extrude(direction);',
        'using var plan=GuidedSweepPlan.Create', 'Frame=GuidedSweepFrame.CorrectedFrenet',
        'using var built=plan.Build();'], 'Current Cadoryx source1080 authoring route')
record(ROOT / 'Cadoryx.Kernel.Occt/NativeMetricThreadTool.cs',
       ['using var plan=GuidedSweepPlan.Create', 'Frame=GuidedSweepFrame.Frenet',
        'using var sweep=plan.Build();'], 'Current Cadoryx source1080 modeled thread route')
record(SDK / 'src/OcctSharp.Modeling/Authoring/GuidedSweepPlan.cs',
       ['public AuthoringResult Build() => Execute(2, 2);', 'NativeMethods.GuidedSweep'],
       'Current SDK qualification source; not a released85 binary provenance claim')
record(SDK / 'src/OcctSharp.Native/src/Modeling/GuidedSweep.cpp',
       ['occtsharp_guided_sweep(', 'OcctSharpGenerated::LocalMakePipeShellBuilder builder(spine)',
        'case 0: builder.SetMode(false);', 'case 1: builder.SetMode(true);',
        'builder.Build(); state.algorithm_status'], 'Current SDK qualification native route')
record(SDK / 'src/OcctSharp.Generator/Emission/local-feature-adapters-native.txt',
       ['class LocalMakePipeShellBuilder final', 'BRepOffsetAPI_MakePipeShell value;',
        'LocalMakePipeShellBuilder(const TopoDS_Wire & p0):value(p0){}',
        'void Build(const Message_ProgressRange'], 'Generator input, not hand-edited output')
record(SDK / 'artifacts/batch-bz/sdk/BRepOffsetAPI_MakePipeShell.cxx',
       ['myPipe = new (BRepFill_PipeShell)(Spine);', 'Ok = myPipe->Build();'],
       'Previously pinned SDK source',
       '947F2FCE9BF88AC78CA32D3B31ED92F0971AB9C0898776C4F9E0734FDFF67697')

route_files = [
    ('BRepFill_PipeShell.cxx', ['BRepFill_Sweep MkSw(mySection, myLocation, true);',
                              'MkSw.Build(Dummy,']),
    ('BRepFill_Sweep.cxx', ['GeomFill_Sweep Sweep(myLoc->Law(ipath), KPart);',
                          'GeomFill_Sweep Sweep(myLoc->Law(IPath), KPart);',
                          'Sweep.Build(mySec->Law(isec),']),
    ('GeomFill_Sweep.cxx', ['done = BuildAll(Continuity, Degmax, Segmax);',
                          'new (GeomFill_SweepFunction)(mySec, myLoc,',
                          'Approx_SweepApproximation Approx(Func);']),
]
bounded_absence = []
for filename, needles in route_files:
    expected = next(row['sha256'] for row in receipt['sources'] if Path(row['path']).name == filename)
    content = record(ARCHIVE / filename, needles, 'Unmodified upstream at fixed OCCT commit', expected)
    for symbol in ['GeomFill_SweepSectionGenerator', 'GeomFill_AppSweep', 'GeomFill_Pipe']:
        # Exact identifiers: GeomFill_PipeError is an enum, not a GeomFill_Pipe call.
        assert re.search(r'\b' + re.escape(symbol) + r'\b', content) is None, (filename, symbol)
    bounded_absence.append({'file': filename, 'symbolsAbsent': [
        'GeomFill_SweepSectionGenerator', 'GeomFill_AppSweep', 'GeomFill_Pipe']})
record(ARCHIVE / 'GeomFill_Pipe.cxx',
       ['void GeomFill_Pipe::ApproxSurf(const bool WithParameters)',
        'GeomFill_SweepSectionGenerator Section(', 'GeomFill_AppSweep          App(',
        'App.Perform(Line, Section, 30);'],
       'A distinct explicit caller exists; not evidence that every Pipe route uses AppSweep')
record(SDK / 'artifacts/batch-bw/sdk/GeomFill_AppSweep_0.cxx',
       ['#define TheSectionGenerator GeomFill_SweepSectionGenerator', '#include <AppBlend_AppSurf.gxx>'],
       'Previously accepted archived source',
       'A789303EFDB221ED0DA50FA145292C66E4F28B4C49DB061EDD924E5A31291A12')
record(SDK / 'artifacts/batch-bw/sdk/GeomFill_SweepSectionGenerator.cxx',
       ['Trans.SetTranslation(PRef, P);', 'else if (D1Ref.IsOpposite',
        'TR = Rot * Trans;', 'cumulTR = TR * cumulTR;'], 'Archived defective transformation branch',
       '6C8308E7640DAA4E7A8BCF8957E9E04238FF4865A96F1C7456047AA93C1FA13A')
metadata = ROOT / 'capability-evidence/diagnostics/af1080-final-ready/occt-api-usage.json'
metadata_text = record(metadata, ['OcctSharp.GuidedSweepPlan'], 'Existing actual compiled metadata audit')
assert 'GeomFill_SweepSectionGenerator' not in metadata_text
assert 'GeomFill_AppSweep' not in metadata_text
assert 'GeomFill_Pipe' not in metadata_text

result = {
    'recordedAt': datetime.now(timezone.utc).isoformat(),
    'state': 'BOUNDED_SOURCE_CHAIN_IDENTIFIED_NOT_NATIVE_RUNTIME_OR_TRANSITIVE_SAFETY_PROOF',
    'occtCommit': COMMIT,
    'sources': sources,
    'upstreamSourceReceipt': receipt,
    'boundedDirectSymbolAbsence': bounded_absence,
    'currentApplicationDirectRoutes': {
        'straightAuthoringSweep': 'Exact prism Extrude',
        'curvedAuthoringSweep': 'GuidedSweepPlan CorrectedFrenet',
        'modeledThread': 'GuidedSweepPlan Frenet',
        'sdkQualificationSource': 'LocalMakePipeShellBuilder -> BRepOffsetAPI_MakePipeShell -> BRepFill_PipeShell -> BRepFill_Sweep -> GeomFill_Sweep',
        'generalApproximationBranch': 'GeomFill_SweepFunction -> Approx_SweepApproximation',
    },
    'knownDifferentCaller': 'GeomFill_Pipe::ApproxSurf constructs SweepSectionGenerator and AppSweep',
    'limitations': [
        'Only listed source edges and direct symbol absence are audited; full transitive calls are not proved.',
        'Current migrating SDK source is not a released85 loaded DLL provenance proof.',
        'No native process was run; output geometry and end-section placement remain NOT RUN on current final payload.',
        'Existing upstream archived defect reproduction remains a defect, not a repaired algorithm.',
        'No new SDK API has yet been justified by this call-chain audit.',
    ],
    'nativeRuntime': 'NOT RUN', 'algorithmRepair': False, 'productionCodeChanged': False,
    'publicOrGeneratedSdkModified': False, 'desktopInput': False, 'cleanup': False,
    'AF': 'ACTIVE', 'G': 'USER_DEFERRED',
}
target = OUT / 'verification.json'
if target.exists():
    raise RuntimeError('Immutable audit result already exists')
target.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'state': result['state'], 'sources': len(sources),
                  'nativeRuntime': result['nativeRuntime'], 'newApiRequired': False}))
