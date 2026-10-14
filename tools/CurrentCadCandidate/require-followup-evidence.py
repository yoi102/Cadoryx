"""Resume from audited terminal results without overwriting the failed queue."""
from datetime import datetime, timezone
from pathlib import Path
import importlib.util
import json
import runpy
import sys

spec = importlib.util.spec_from_file_location('ui_followups', Path(__file__).with_name('qualify-ui-followups.py'))
ui = importlib.util.module_from_spec(spec); spec.loader.exec_module(ui)
ROOT, DRAFT, read, ref, check = ui.ROOT, ui.DRAFT, ui.read, ui.ref, ui.check
runpy.run_path(str(Path(__file__).with_name('require-provider-evidence.py')))
qualified, qualification_path = ui.verified_followups()
old_queue_path = DRAFT / 'followup-queue-exit.json'
old_queue = read(old_queue_path)
assert old_queue['state'] == 'STOPPED_AT_FAILED_GATE_OR_RUNTIME'
recovery_path = DRAFT / 'recovery24/runtime-3-verification.json'
recovery = read(recovery_path)
assert recovery['state'] == 'PASS_LOCAL87CU1_CURRENT_F5_D7_ORIGINAL_WPF24_NATIVE62_HISTORY_SAVE48_IMAGES52_PENDING_DIRECT_REVIEW'
assert check(recovery['semanticBuild']) == check(qualified['application'])
assert len(recovery['cases']) == 24 and len(recovery['images']) == 52 and len(recovery['saves']) == 48
for key in ('fixture', 'fullBoth7117', 'terminal', 'result', 'binding'): check(recovery[key])
for row in recovery['images'] + recovery['saves']: check(row)
main_path = check(qualified['main36'])
provider_path = ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/provider-verification.json'
provider = read(provider_path)
assert len(provider['scenarios']) == 2
assert provider['allPassed'] == all(c['passed'] for c in provider['scenarios'])
out = DRAFT / 'followup-resume1-verification.json'
record = dict(state='AUDITED_TERMINAL_FOLLOWUPS_RESUMED_ORIGINAL_QUEUE_FAILURE_PRESERVED',
    originalQueue=ref(old_queue_path), qualification=ref(qualification_path), recovery24=ref(recovery_path),
    main36=ref(main_path), main36DirectReview=qualified['main36DirectReview'], provider=ref(provider_path),
    providerAllPassed=provider['allPassed'], providerFailures=[c['scenario'] for c in provider['scenarios'] if not c['passed']],
    exactSTEP24StillDeferredFailures=True, originalConditionsChanged=False,
    desktopAcceptanceProven=False, performanceProven=False, AFComplete=False)
arguments = sys.argv[1:] if __name__ == '__main__' else []
if arguments == ['--record']:
    assert not out.exists(), 'Preserve immutable resumed result.'
    record['recordedAt'] = datetime.now(timezone.utc).isoformat()
    out.write_text(json.dumps(record, indent=2)+'\n', encoding='utf-8')
else:
    assert not arguments
    existing = read(out)
    assert {k: existing[k] for k in record} == record
print(json.dumps(dict(auditedTerminalFollowups=True, providerAllPassed=provider['allPassed'], AFComplete=False)))
