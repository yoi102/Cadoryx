from pathlib import Path
import datetime, hashlib, json, shutil

root = Path(__file__).resolve().parents[1]
publisher = root.parent / 'OcctSharp/OcctSharp'
upstream = publisher / 'artifacts/cadoryx-public-sdk85'
evidence = root / 'capability-evidence/diagnostics/public-nuget-migration/corrected-provider-checkpoint4'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
assert not evidence.exists()
evidence.mkdir()
references = {}
def freeze(key, original):
    target = evidence / (key + '.json')
    shutil.copy2(original, target)
    assert sha(original) == sha(target)
    references[key] = {'path':target.relative_to(root).as_posix(),'sha256':sha(target),'origin':str(original)}
    return read(target)

for configuration in ('release','debug'):
    full = freeze('public-' + configuration + '-full', upstream / f'template-fix-{configuration}-full.json')
    assert full['state'] == 'PASS' and full['generatorTests'] == 410 and full['runtimeTests'] == 2255
cold = freeze('corrected-cold', upstream / 'template-fix-cold.json')
assert cold['state'] == 'PASS' and cold['generatedFiles'] == 399 and cold['generatorTests'] == 410 and cold['runtimeTests'] == 2255
exports = freeze('corrected-native-exports', upstream / 'template-fix-native-exports.json')
assert exports['state'] == 'PASS' and exports['removed'] == 0 and exports['debugMatchesRelease']
debug = freeze('public-actual-debug', upstream / 'template-fix-actual-debug/result.json')
assert debug['state'] == 'PASS' and debug['tests'] == 2255 and len(debug['files']) == 62
modules = freeze('public-actual-debug-loaded', upstream / 'template-fix-actual-debug-loaded-modules.json')
assert modules['state'] == 'PASS' and modules['expectedDlls'] == 62 and modules['processes']
expected_debug = {r['name']:r['sha256'] for r in debug['files']}
for process in modules['processes']:
    assert process['loadedNativeDlls'] == 62 and {r['name']:r['sha256'] for r in process['files']} == expected_debug
    for row in process['files']:
        assert Path(row['path']).parent.resolve() == (Path(debug['runDirectory']) / 'occt').resolve()
        assert sha(Path(row['path'])) == row['sha256']
manifest = freeze('final-public-runtime-manifest', publisher / 'runtime/win-x64/runtime-manifest.json')
assert manifest['packageVersion'] == '8.0.1-preview.85' and manifest['nativeAbi'] == '1.118' and manifest['bridgeVersion'] == '0.133.0'
native = {Path(r['path']).name:r['sha256'] for r in manifest['files'] if r['path'].startswith('occt/') and r['path'].endswith('.dll')}
assert len(native) == 62 and native['OcctSharp.Native.dll'] == 'D44F3F272386EA2CC87E7107AB2E3375D8100283872372F1FBC234A3B4E38CB3'
for row in manifest['files']:
    assert sha(publisher / 'runtime/win-x64' / row['path']) == row['sha256']
vcpkg = []
for configuration in ('Release','Debug'):
    build = publisher / 'build/vcpkg-native85'
    runtime = freeze('vcpkg-' + configuration.lower() + '-manifest', build / f'runtime-manifest-{configuration}.json')
    provenance = freeze('vcpkg-' + configuration.lower() + '-provenance', build / f'runtime-evidence/{configuration}/provenance.json')
    assert runtime['state'] == 'BUILD_PASS_RUNTIME_NOT_RUN' and runtime['generationChecked']
    assert provenance['state'] == 'PASS' and len(runtime['files']) == 79 and len(provenance['nativeFiles']) == 79
    assert len(provenance['licenses']) == 21
    assert {Path(r['path']).name:r['sha256'] for r in runtime['files']} == {r['name']:r['sha256'] for r in provenance['nativeFiles']}
    for row in runtime['files']:
        assert sha(build / 'runtime' / configuration / Path(row['path']).name) == row['sha256']
    for row in provenance['licenses']:
        assert sha(build / 'runtime-evidence' / configuration / row['path']) == row['sha256']
    vcpkg.append({'configuration':configuration,'generationChecked':True,'nativeDlls':79,'licenses':21,'runtimePassed':False})
target = root / 'capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT4.json'
assert not target.exists()
target.write_text(json.dumps({
    'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':'CORRECTED_PUBLIC_PROVIDER_FULL_LOCAL_GATES_AND_VCPKG_BOTH_COMPILES_PROVENANCE_AUDITED_PUBLICATION_NOT_RUN',
    'version':'8.0.1-preview.85','nativeAbi':'1.118','bridgeVersion':'0.133.0',
    'finalPublicBridgeSha256':native['OcctSharp.Native.dll'],'publicNativeDlls':62,
    'priorBridgeD7IsHistoricalOnly':True,'correctedColdPassed':True,
    'publicBothGenerator410Runtime2255':True,'actualDebugRuntime2255And62LoadedModulesPassed':True,
    'vcpkg':vcpkg,'references':references,
    'preparedPublicConsumer':{'path':'artifacts/public85-final1073-1/preparation.json','sha256':sha(root / 'artifacts/public85-final1073-1/preparation.json'),'sourceAndToolInputs':1186,'helperInputs':1,'projects':50},
    'finalRepackAccepted':False,'publicationConfirmed':False,'freshPublicRestorePassed':False,
    'vcpkgRuntimePassed':False,'productionApplied':False,'CadoryxRequiresVcpkg':False,
    'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'
},indent=2)+'\n',encoding='utf-8')
print('PASS corrected public local gates/native62 and both vcpkg compile/provenance79/21 independently audited;remote/actual vcpkg runtime NOT RUN.')
