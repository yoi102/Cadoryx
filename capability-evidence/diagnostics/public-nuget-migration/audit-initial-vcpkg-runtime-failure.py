from pathlib import Path
import datetime, hashlib, json, xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
publisher = root.parent / 'OcctSharp/OcctSharp'
run = publisher / 'artifacts/cadoryx-public-sdk85/vcpkg-runtime-release/run-959c8cb8fa084c52962913a179b92456'
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
ref = lambda p: {'path':str(p),'sha256':sha(p)}
trx = run / 'vcpkg-runtime.trx'
ns = {'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
tree = ET.parse(trx)
rows = tree.findall('.//t:UnitTestResult',ns)
counts = {outcome:sum(r.attrib['outcome']==outcome for r in rows) for outcome in ('Passed','Failed','NotExecuted')}
assert len(rows) == 2255 and counts == {'Passed':2253,'Failed':2,'NotExecuted':0}
failures = []
for row in rows:
    if row.attrib['outcome'] != 'Failed':
        continue
    assert 'BatchXProjectionTests.Signed64BitTimeValuesPreserveSignAndExceed32BitRange' in row.attrib['testName']
    failures.append({'name':row.attrib['testName'],'message':row.findtext('t:Output/t:ErrorInfo/t:Message','',ns),'stack':row.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)})
manifest = publisher / 'build/vcpkg-native85/runtime-manifest-Release.json'
test_assembly = run / 'OcctSharp.Runtime.Tests.dll'
passing_test_assembly = publisher / 'tests/OcctSharp.Runtime.Tests/bin/Release/net10.0/win-x64/OcctSharp.Runtime.Tests.dll'
assert sha(test_assembly) == sha(passing_test_assembly)
for row in read(manifest)['files']:
    assert sha(run / 'occt' / Path(row['path']).name) == row['sha256']
source = Path('C:/Users/yoiri/.cache/occtsharp-dependencies/vcpkg-e456309/buildtrees/opencascade/src/V8_0_1-2f604eca2a.clean/src/Visualization/TKService/Media/Media_FormatContext.cxx')
text = source.read_text(encoding='utf-8')
start = text.index('double Media_FormatContext::FormatUnitsToSeconds')
snippet = text[start:text.index('//========',start)]
assert '#ifdef HAVE_FFMPEG' in snippet and 'return 0.0;' in snippet
target = root / 'capability-evidence/diagnostics/public-nuget-migration/vcpkg-initial-runtime-failure.json'
assert not target.exists()
target.write_text(json.dumps({
    'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':'FIRST_VCPKG_RELEASE_FULL_RUNTIME2253_OF2255_FAILED_NOT_DELIVERED',
    'configuration':'Release','passed':2253,'failed':2,'skipped':0,'failures':failures,
    'trx':ref(trx),'stdout':ref(run / 'runtime.stdout.log'),'stderr':ref(run / 'runtime.stderr.log'),
    'intendedNativeManifest':ref(manifest),'isolatedDiskPayloadMatchesManifest':True,
    'successfulLoadedModuleReportAvailable':False,
    'occtSource':ref(source),'formatUnitsToSecondsSource':snippet,
    'providerFfmpegDisabled':True,'originalAssertionsChanged':False,
    'publicPrecompiledProviderResultIsSeparate':True,'CadoryxRequiresVcpkg':False,
    'productionApplied':False,'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'
},indent=2)+'\n',encoding='utf-8')
print('PASS audit of2255 outcomes and isolated79 DLL bytes;two Media_FormatContext failures retained;vcpkg NOT delivered.')
