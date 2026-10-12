from pathlib import Path
from collections import Counter
import datetime, hashlib, json, subprocess, xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: {'path': str(p), 'sha256': sha(p)}
report_path = root / 'artifacts/b18-prescribed-complete-authors-draft-2/full6556-release-2-verification.json'
report = read(report_path)
assert (report['count'], report['passed'], report['failed'], report['skipped']) == (6556, 6531, 25, 0)
assert report['allOriginalNamesAndAssertionsUnchanged'] and not report['originalBudgetsChanged']
for key in ('build', 'terminal', 'trx', 'loadedNative'):
    assert sha(Path(report[key]['path'])) == report[key]['sha256']
names = [row['name'] for row in report['failures']]
assert len(names) == len(set(names)) == 25
assert sum('SketchRegionSingleLoopMixedNativeTests.' in name for name in names) == 24
assert sum('AgentEmptyResponseRecoveryTests.' in name for name in names) == 1
files = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '--', '**/packages.lock.json', '*.csproj'], cwd=root, text=True).splitlines()
locks = []
refs = []
for relative in sorted(set(files)):
    if relative.startswith('capability-evidence/'):
        continue
    path = root / relative
    if relative.endswith('/packages.lock.json'):
        versions = Counter()
        for group in read(path)['dependencies'].values():
            for package, value in group.items():
                if package == 'OcctSharp' or package.startswith('OcctSharp.'):
                    versions[value['resolved']] += 1
        if versions:
            locks.append({'path': relative, 'sha256': sha(path), 'sdkVersions': dict(versions)})
    elif relative.endswith('.csproj'):
        for item in ET.parse(path).iter('PackageReference'):
            name = item.attrib.get('Include', '')
            if name == 'OcctSharp' or name.startswith('OcctSharp.'):
                refs.append({'project': relative, 'package': name, 'expression': item.attrib.get('Version')})
out = root / 'capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT2.json'
assert not out.exists()
out.write_text(json.dumps({
    'recordedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state': 'ORIGINAL_RELEASE6556_COMPLETED_NEXT_NATIVE_LANE_HANDED_TO_PUBLIC_SDK_OWNER',
    'full6556ReleaseAttempt2': {'audit': ref(report_path), 'count': 6556, 'passed': 6531, 'failed': 25, 'skipped': 0, 'failureNames': names, 'build': report['build'], 'terminal': report['terminal'], 'trx': report['trx'], 'loadedNative': report['loadedNative']},
    'originalNamesAssertionsAndBudgetsChanged': False,
    'upstreamThread': '01a0f7b3-ff5f-7cb0-bff9-a5caad1a49b0',
    'priorityLease': ref(root / 'artifacts/native-lane-priority-preview85-1.json'),
    'activeConsumerLocksBeforeMigration': locks,
    'activeConsumerDirectSdkReferencesBeforeMigration': refs,
    'explicitStaleSdkReferenceCount': sum(row['expression'] != '[$(OcctSharpVersion)]' for row in refs),
    'publicRestoreScript': ref(root / 'scripts/verify-public-occt-restore.ps1'),
    'publicLockScript': ref(root / 'scripts/verify-public-occt-locks.ps1'),
    'publicScriptsSyntaxPassed': True, 'publicRestoreAndLockScriptsActuallyRun': False,
    'publishedVersionConfirmed': False, 'productionPackageChanged': False,
    'currentProductionPackage': ET.parse(root / 'Directory.Build.props').findtext('.//OcctSharpVersion'),
    'productionBaseline': ref(root / 'Cadoryx.wpf/runtime-baseline.json'),
    'cleanupInCadoryx': False, 'AF': 'ACTIVE', 'G': 'USER_DEFERRED'
}, indent=2) + '\n', encoding='utf-8')
print(f'PASS immutable checkpoint: original full6556 6531/25; {len(locks)} SDK locks, {len(refs)} direct references, public migration not yet applied.')
