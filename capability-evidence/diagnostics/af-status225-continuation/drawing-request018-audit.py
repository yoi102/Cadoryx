from pathlib import Path
import datetime,hashlib,json
root=Path('C:/Users/yoiri/source/repos/Cadoryx')
run=root/'artifacts/f5-drawing-source-path-feedback-draft-2/lmstudio-drawing-1'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:{'path':str(p),'sha256':sha(p)}
request=read(run/'request-018.json')
calls=read(run/'tool-calls.json')
sources=[c for c in calls if c['Name']=='cad_drawing' and json.loads(c['ArgumentsJson']).get('action')=='inspect_sources']
successful=[c for c in sources if json.loads(c['result']).get('success')]
assert successful
source=successful[-1];raw=json.loads(source['result'])
cylinders=[s for s in raw['items'] if s.get('geometry')=='CylinderFace']
assert len(cylinders)==1
messages=[]
for index,message in enumerate(request['Messages']):
    content=message.get('Content') or ''
    parsed=None;error=None
    try:parsed=json.loads(content)
    except ValueError as e:error=str(e)
    messages.append({'index':index,'role':message['Role'],'characters':len(content),
        'jsonParseError':error,'jsonKeys':list(parsed) if isinstance(parsed,dict) else None,
        'containsCylinderGeometry': 'CylinderFace' in content,
        'containsExactCylinderSelection': isinstance(parsed,dict) and any(
            item.get('selection')==cylinders[0]['selection'] for item in parsed.get('items',[]) if isinstance(item,dict)),
        'containsTruncationMarker':'truncat' in content.lower(),'toolCallId':message.get('ToolCallId')})
out=root/'capability-evidence/AF_PENDING_F5_DRAWING_RECOVERY_CONTEXT_DIAGNOSTIC_V2.json'
assert not out.exists()
data={'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':'FINAL_RECOVERY_REQUEST_AND_ACTUAL_NATIVE_SELECTION_CONTENT_AUDITED_PROVIDER_STILL_FAILED',
    'request':ref(run/'request-018.json'),'actualToolCalls':ref(run/'tool-calls.json'),
    'providerResult':ref(run/'result.json'),'messages':messages,
    'actualSourceCharacters':len(source['result']),'actualSourceItemCount':len(raw['items']),
    'actualCylinderItemIndex':cylinders[0]['index'],'actualCylinderSelection':cylinders[0]['selection'],
    'actualCylinderPresentInFinalRequest':any(m['containsCylinderGeometry'] for m in messages),
    'exactCylinderSelectionPresentInFinalRequest':any(m['containsExactCylinderSelection'] for m in messages),
    'requestMessageCount':len(request['Messages']),'requestToolCount':len(request['Tools']),
    'selectionInferredOrResubmitted':False,'productChanged':False,'originalBudgetsChanged':False,
    'comparison':'Parsed JSON selection objects, not serialized substring ordering/escaping.',
    'firstSubstringComparisonPreserved':ref(root/'capability-evidence/AF_PENDING_F5_DRAWING_RECOVERY_CONTEXT_DIAGNOSTIC.json'),
    'rootCauseProven':False,'providerPassed':False,'cleanup':False,'AF':'ACTIVE'}
out.write_text(json.dumps(data,indent=2)+'\n',encoding='utf-8')
print(json.dumps({k:data[k] for k in ('actualSourceCharacters','actualCylinderPresentInFinalRequest','exactCylinderSelectionPresentInFinalRequest','requestMessageCount','requestToolCount')}))
for message in messages:print(json.dumps(message))
