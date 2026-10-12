from pathlib import Path
import datetime, hashlib, json

root = Path(__file__).resolve().parents[1]
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: {'path': str(p), 'sha256': sha(p)}
diag = root / 'capability-evidence/diagnostics/public-nuget-migration'
old = read(root / 'capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT2.json')
snap = diag / 'public-scripts-before-native-manifest'
assert sha(snap / 'verify-public-occt-restore.ps1') == old['publicRestoreScript']['sha256']
old_lock = (snap / 'verify-public-occt-locks.ps1').read_bytes()
old_lock = old_lock.replace(
    b"if(!$cadConfig.SelectSingleNode('/configuration/packageSources/clear'))",
    b"if(!$cadConfig.configuration.packageSources.clear -and !$cadConfig.SelectSingleNode('/configuration/packageSources/clear'))")
added = b'''    $cadProjectLockPath=Join-Path (Split-Path -Parent $cadPath) 'packages.lock.json'
    if(!(Test-Path -LiteralPath $cadProjectLockPath)){throw "Consumer lock missing: $cadRelative"}
    $cadProjectLock=Get-Content -LiteralPath $cadProjectLockPath -Raw|ConvertFrom-Json
    $cadLockedReference=@($cadProjectLock.dependencies.PSObject.Properties|ForEach-Object {$_.Value.PSObject.Properties|Where-Object Name -CEQ $cadId})
    if(!$cadLockedReference.Count){throw "Direct SDK reference absent from consumer lock: $cadRelative / $cadId"}'''
before = b'''    if(!(Test-Path -LiteralPath (Join-Path (Split-Path -Parent $cadPath) 'packages.lock.json'))){throw "Consumer lock missing: $cadRelative"}'''
assert added in old_lock
old_lock = old_lock.replace(added, before)
assert hashlib.sha256(old_lock).hexdigest().upper() == old['publicLockScript']['sha256']
old_snapshot = snap / 'verify-public-occt-locks-checkpoint2.ps1'
assert not old_snapshot.exists()
old_snapshot.write_bytes(old_lock)
native_fixture = diag / 'publisher-manifest-fixture-verification.json'
lock_fixture = diag / 'public-lock-fixture-verification.json'
assert read(native_fixture)['count'] == 8 and not read(native_fixture)['nativeCodeExecuted']
assert read(lock_fixture)['count'] == 7 and read(lock_fixture)['publicMetadataIsSynthetic']
lease_before = diag / 'priority-lease-before-extension1.json'
lease_after = diag / 'priority-lease-after-extension1.json'
assert sha(lease_before) == old['priorityLease']['sha256']
assert read(lease_after)['until'] == '2026-10-12T11:00:00+00:00'
assert read(lease_after)['originalMaximumWaitHours'] == 6
out = root / 'capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT3.json'
assert not out.exists()
out.write_text(json.dumps({
    'recordedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state': 'USER_AUTHORIZED_VCPKG_RESTORE_BUILD_ADDED_PUBLIC_CONSUMER_PREPARATION_CONTINUES',
    'previousCheckpoint': ref(root / 'capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT2.json'),
    'checkpoint2ExactScriptSnapshots': [ref(snap / 'verify-public-occt-restore.ps1'), ref(old_snapshot)],
    'currentConsumerScripts': [ref(root / 'scripts' / name) for name in ('nuget-package-cache.ps1', 'verify-public-occt-restore.ps1', 'verify-public-occt-locks.ps1', 'verify-publisher-native-manifest.ps1', 'verify-public-occt-lock-fixtures.ps1')],
    'publisherNativeManifestMetadataFixtures': ref(native_fixture),
    'syntheticConsumerMetadataFixtures': ref(lock_fixture),
    'pinnedVcpkgReadOnlyAudit': ref(diag / 'vcpkg-baseline-audit.json'),
    'upstreamDependencyBuildOwner': '01a0f7b3-ff5f-7cb0-bff9-a5caad1a49b0',
    'upstreamReportedSdk85': {'generatorPassed': 410, 'nativeReleaseCompiled': True, 'runtimePassed': 2251, 'runtimeFailed': 4, 'runtimeTotal': 2255, 'note': 'CB/CC visible-selection repaint assertions remain under diagnosis; no tolerance/assertion relaxation.'},
    'priorityLeaseBefore': ref(lease_before), 'priorityLeaseAfter': ref(lease_after),
    'originalSchedulingMaximumHours': 6, 'originalConsumersStopped': False,
    'publicConsumersActuallyRun': False, 'vcpkgRestoreOrBuildActuallyRun': False,
    'publicationConfirmed': False, 'productionPackageChanged': False,
    'cleanupInCadoryx': False, 'AF': 'ACTIVE', 'G': 'USER_DEFERRED'
}, indent=2) + '\n', encoding='utf-8')
print('PASS checkpoint3: exact checkpoint2 source bytes retained, eight publisher/seven synthetic consumer fixtures, pinned vcpkg audit, bounded lane extension; actual public/vcpkg acceptance pending.')
