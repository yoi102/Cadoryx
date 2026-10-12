from pathlib import Path
import datetime,hashlib,json,re
root=Path('C:/Users/yoiri/source/repos/Cadoryx')
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:{'path':str(p),'sha256':sha(p)}
writer_path=root/'artifacts/sdk31-exact-knot-subdivision-2/both-1-exit.json'
coverage_path=root/'artifacts/sdk31-exact-knot-coverage-3/both-1-exit.json'
writer=read(writer_path);coverage=read(coverage_path)
assert len(writer['records'])==len(coverage['records'])==14
assert coverage['previousWriterMatrix']['sha256']==sha(writer_path)
prior_path=root/'capability-evidence/AF_PENDING_SDK31_PLANAR_REPRESENTATION_REJECTIONS.json';prior=read(prior_path)
expected_modules={}
for cfg in ('Release','Debug'):
 row=next(r for r in prior['writerReaderPairs'] if r['configuration']==cfg and r['mode']==0)
 native=read(Path(row['stockExit']['path']))['actualModules']+read(Path(row['writerExit']['path']))['actualModules']
 expected_modules[cfg]={Path(r['path']).name:r['sha256'] for r in native}
rows=[];empty_passes=0;strict_failures=0
for old,current in zip(writer['records'],coverage['records']):
 assert (old['configuration'],old['segments'],old['multiplicity'])==(current['configuration'],current['segments'],current['multiplicity'])
 cfg=old['configuration'];w=read(Path(old['writer']['path']));s=read(Path(old['stock']['path']));c=read(Path(current['terminal']['path']))
 for item in (old['writer'],old['stock'],old['step'],current['terminal']):assert sha(Path(item['path']))==item['sha256']
 assert w['exitCode']==0 and c['exitCode']==1
 for item in (w,s,c):
  assert item['actualModules']
  for module in item['actualModules']:
   p=Path(module['path']);assert p.name!='OcctSharp.Native.dll'
   assert '/artifacts/native/'+cfg+'/' in p.as_posix()
   assert p.name in expected_modules[cfg] and module['sha256']==expected_modules[cfg][p.name]
 line=next(line for line in w['summary'] if line.startswith('EXACT_KNOT_OFFSETS='))
 fields=dict(re.findall(r'(\w+)=([^ ]+)',line))
 assert fields['SOURCE_KEPT']=='1' and int(fields['FACES'])==6 and int(fields['EDGES'])==12
 assert float(fields['POINT_DELTA'])<1e-10 and float(fields['DERIVATIVE_DELTA'])<1e-10
 counts=dict(re.findall(r'(\w+)=([^ ]+)',c['summary'][0]))
 assert [int(counts[k]) for k in ('SOURCE_SOLIDS','SOURCE_FACES','SOURCE_EDGES','SOURCE_VERTICES')]==[1,6,12,8]
 if old['multiplicity']==3:
  assert s['exitCode']==0 and [int(counts[k]) for k in ('SOLIDS','FACES','EDGES','VERTICES')]==[0,0,0,0]
  assert counts['COMPLETE']=='0';empty_passes+=1;reason='EMPTY_MODEL_REJECTED_DESPITE_ORIGINAL_READER_EXIT0'
 else:
  assert s['exitCode']==1 and counts['COMPLETE']=='1'
  detail=dict(re.findall(r'(\w+)=([^ ]+)',c['summary'][1]))
  assert int(detail['EDGE_USES'])==24 and detail['STRICT_VALID']=='0'
  assert float(detail['MAX_DELTA'])>1e-7 and float(detail['VOLUME_ERROR'])<1e-7
  strict_failures+=1;reason='ORIGINAL_STRICT_PCURVE_ERROR_REMAINS'
 rows.append({'configuration':cfg,'segments':old['segments'],'multiplicity':old['multiplicity'],'writer':old['writer'],
  'originalFrozenStockReader':old['stock'],'completeScopeReader':current['terminal'],'step':old['step'],
  'writerSummary':w['summary'],'coverageSummary':c['summary'],'reason':reason,'productAccepted':False})
assert empty_passes==6 and strict_failures==8
for item in writer['generatedWriterUnchanged']:assert sha(Path(item['path']))==item['sha256']
for cfg in ('Release','Debug'):
 for task in ('sdk31-exact-knot-subdivision-2','sdk31-exact-knot-coverage-3'):
  assert read(root/'artifacts'/task/f'build-{cfg.lower()}-1-exit.json')['exitCode']==0
  assert not re.search(r'\bwarning\s+C\d+', (root/'artifacts'/task/f'build-{cfg.lower()}-1.log').read_text(encoding='utf-8-sig'))
out=root/'capability-evidence/AF_PENDING_SDK31_EXACT_KNOT_SUBDIVISION_REJECTIONS.json';assert not out.exists()
out.write_text(json.dumps({'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
 'state':'ALL14_EXACT_KNOT_WRITER_CASES_REJECTED_BY_FULL_ORIGINAL_GEOMETRY_SCOPE',
 'writerMatrix':ref(writer_path),'coverageMatrix':ref(coverage_path),'originalStrictRepresentationProof':ref(prior_path),
 'cases':rows,'completeShapeFailures':6,'originalStrictFailures':8,'productCasesAccepted':0,
 'generatedWriterSourceUnchanged':writer['generatedWriterUnchanged'],
 'actualToolkitHashesCapturedAtExecutionAndMatchFrozenConfigBaseline':True,
 'initialAggregateAuditFailure':'Stock-reader-only baseline did not list three Debug binary toolkits loaded by the writer. Corrected only the baseline union to use the original frozen writer and stock-reader module records; executions and assertions unchanged.',
 'nativeDllHashesRecomputedAtAggregateAudit':False,
 'source':[ref(root/'capability-evidence/diagnostics/sdk31-exact-knot-subdivision'/f) for f in ('knot-step.cpp','stock-shape-coverage.cpp','CMakeLists.txt')],
 'firstCompilerWarningRetained':writer['firstNativeBuildWarningRetained'],
 'firstCompilerSourceRetained':writer['firstNativeSourceRetained'],
 'originalFrozenReaderModified':False,'occtReaderImplementationModified':False,
 'newCoverageWrapperIsAdditionalDiagnostic':True,'fittingUsed':False,'toleranceRaised':False,
 'applicationExportGuardChanged':False,'newNativeBindingsAdded':False,'publicUpstreamModified':False,
 'productionApplied':False,'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'},indent=2)+'\n',encoding='utf-8')
print('PASS audit:14 candidates rejected; eight unchanged strict failures plus six empty-shape false passes. No product or generator adoption.')
