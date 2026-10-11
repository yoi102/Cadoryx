using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

public static class DrawingAnnotationCommands
{
    public static ICadDocumentCommand PutDatum(Guid sheet,DrawingDatumSymbol value,bool replace=false)=>
        new PutAnnotationCommand(sheet,value,replace);
    public static ICadDocumentCommand PutControlFrame(Guid sheet,DrawingFeatureControlFrame value,bool replace=false)=>
        new PutAnnotationCommand(sheet,value,replace);
    public static ICadDocumentCommand PutBalloon(Guid sheet,DrawingAssemblyBalloon value,bool replace=false)=>
        new PutAnnotationCommand(sheet,value,replace);
    public static ICadDocumentCommand Delete(Guid sheetId,Guid id)=>new EditDocumentCommand("Delete manufacturing annotation",document=>
    {
        var sheet=document.DrawingSheets[sheetId];
        if(sheet.ControlFrames.Any(f=>f.ReferencedDatumIds.Contains(id)))throw new CadValidationException("Delete dependent control frames before deleting their datum.");
        return document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,sheet with
        {DatumSymbols=[..sheet.DatumSymbols.Where(d=>d.Id!=id)],ControlFrames=[..sheet.ControlFrames.Where(f=>f.Id!=id)],Balloons=[..sheet.Balloons.Where(b=>b.Id!=id)]})};
    });
    public static ICadDocumentCommand Move(Guid sheetId,Guid id,Point2d position)=>new EditDocumentCommand("Move manufacturing annotation",document=>
    {
        var sheet=document.DrawingSheets[sheetId];
        if(!sheet.DatumSymbols.Any(d=>d.Id==id)&&!sheet.ControlFrames.Any(f=>f.Id==id)&&!sheet.Balloons.Any(b=>b.Id==id))
            throw new CadValidationException("Manufacturing annotation is missing.");
        var next=sheet with{DatumSymbols=[..sheet.DatumSymbols.Select(d=>d.Id==id?d with{PositionMm=position}:d)],
            ControlFrames=[..sheet.ControlFrames.Select(f=>f.Id==id?f with{PositionMm=position}:f)],
            Balloons=[..sheet.Balloons.Select(b=>b.Id==id?b with{PositionMm=position}:b)]};next.Validate(document.Id);
        return document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,next)};
    });
    public static ICadDocumentCommand RenameDatum(Guid sheetId,Guid id,string name)=>new EditDocumentCommand("Rename drawing datum",document=>
    {
        var sheet=document.DrawingSheets[sheetId];var index=sheet.DatumSymbols.FindIndex(d=>d.Id==id);
        if(index<0)throw new CadValidationException("Drawing datum is missing.");
        var next=sheet with{DatumSymbols=sheet.DatumSymbols.SetItem(index,sheet.DatumSymbols[index] with{Name=name})};next.Validate(document.Id);
        return document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,next)};
    });
    public static ICadDocumentCommand SetPartsTable(Guid sheetId,Point2d? position)=>new EditDocumentCommand("Place assembly parts table",document=>
    {
        var sheet=document.DrawingSheets[sheetId] with{PartsTablePositionMm=position};sheet.Validate(document.Id);
        return document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,sheet)};
    });

    internal static async Task VerifyAsync(DocumentCommandContext context,TechnicalDrawingView view,DrawingTopologyReference reference,CancellationToken token)
    {
        if(view.StaleReason is not null||!reference.IsCurrent(context.Snapshot)||
           !view.Sources.Any(s=>s.Path.Equals(reference.Path)&&s.Body==reference.BodyId&&s.Asset==reference.Asset&&s.Revision==reference.Revision))
            throw new CadValidationException("Attachment is stale or does not belong to the current drawing view.");
        var resolver=context.Kernel as IDrawingTopologyResolver??throw new NotSupportedException("Exact drawing attachment resolver unavailable.");
        DrawingTopologyReference? actual;
        if(reference.HistoryOrigin is {} author)
        {
            var history=context.Kernel as IDrawingTopologyHistoryResolver??throw new NotSupportedException("Manufacturing attachment history resolver unavailable.");
            var result=await history.ResolveDrawingHistoryAsync(context.Snapshot,author,view.Sources.Single(s=>s.Path.Equals(reference.Path)&&s.Body==reference.BodyId),context.Assets,token).ConfigureAwait(false);
            actual=result.Current is {} current?current with{HistoryOrigin=author}:null;
        }
        else actual=await resolver.ResolveDrawingTopologyAsync(context.Snapshot,reference.Path,reference.BodyId,
            reference.FullTopologyIndex,reference.Fingerprint,context.Assets,token).ConfigureAwait(false);
        if(actual!=reference)throw new CadValidationException("Attachment differs from the original BRep evidence.");
    }
    internal static async Task<DrawingCylinderSizeEvidence> SizeAsync(DocumentCommandContext context,DrawingTopologyReference source,
        DrawingCylinderSizeEvidence? expected,CancellationToken token)
    {
        var resolver=context.Kernel as IDrawingSizeResolver??throw new NotSupportedException("Original cylindrical size resolver unavailable.");
        var actual=await resolver.ResolveDrawingCylinderSizeAsync(context.Snapshot,source,context.Assets,token).ConfigureAwait(false);
        if(expected is not null&&!expected.SameProof(actual))throw new CadValidationException("Size dimensions or original topology proof differ from the current solid.");
        return expected??actual;
    }
    internal static async Task<DrawingPlanarSizeEvidence> PlanarSizeAsync(DocumentCommandContext context,TechnicalDrawingView view,
        DrawingTopologyReference source,DrawingTopologyReference opposite,bool internalWidth,DrawingPlanarSizeEvidence? expected,CancellationToken token)
    {
        await VerifyAsync(context,view,opposite,token).ConfigureAwait(false);
        var resolver=context.Kernel as IDrawingPlanarSizeResolver??throw new NotSupportedException("Original planar size resolver unavailable.");
        var actual=await resolver.ResolveDrawingPlanarSizeAsync(context.Snapshot,source,opposite,internalWidth,context.Assets,token).ConfigureAwait(false);
        if(expected is not null&&!expected.SameProof(actual))throw new CadValidationException("Planar width or either original finite domain differs from the current solid.");
        return expected??actual;
    }
    private sealed class PutAnnotationCommand(Guid sheetId,object value,bool replace):ICadDocumentCommand
    {
        public string Name=>"Set manufacturing annotation";
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            var document=context.Snapshot;var sheet=document.DrawingSheets[sheetId];
            var (id,owner,reference)=value switch
            {
                DrawingDatumSymbol d=>(d.Id,d.ViewId,d.Source),DrawingFeatureControlFrame f=>(f.Id,f.ViewId,f.Source),
                DrawingAssemblyBalloon b=>(b.Id,b.ViewId,b.Source),_=>throw new NotSupportedException()
            };
            bool exists=value switch{DrawingDatumSymbol=>sheet.DatumSymbols.Any(d=>d.Id==id),
                DrawingFeatureControlFrame=>sheet.ControlFrames.Any(f=>f.Id==id),_=>sheet.Balloons.Any(b=>b.Id==id)};
            if(exists!=replace)throw new CadValidationException("Annotation identity does not match the requested edit.");
            var view=sheet.Views.SingleOrDefault(v=>v.Id==owner)??throw new CadValidationException("Annotation drawing view is missing.");
            await VerifyAsync(context,view,reference,token).ConfigureAwait(false);
            object verified=value switch
            {
                DrawingDatumSymbol {FeatureType:DrawingDatumFeatureType.CylinderSizeAxis} d=>d with{SizeEvidence=await SizeAsync(context,d.Source,d.SizeEvidence,token).ConfigureAwait(false)},
                DrawingFeatureControlFrame {Target:DrawingToleranceTarget.Axis} f=>f with{SizeEvidence=await SizeAsync(context,f.Source,f.SizeEvidence,token).ConfigureAwait(false)},
                DrawingDatumSymbol {FeatureType:DrawingDatumFeatureType.PlanarSizeMedianPlane} d=>d with{PlanarSizeEvidence=await Planar(d.PlanarSizeEvidence).ConfigureAwait(false)},
                DrawingFeatureControlFrame {Target:DrawingToleranceTarget.MedianPlane} f=>f with{PlanarSizeEvidence=await Planar(f.PlanarSizeEvidence).ConfigureAwait(false)},
                _=>value
            };
            Task<DrawingPlanarSizeEvidence> Planar(DrawingPlanarSizeEvidence? proof)=>proof is not null?
                PlanarSizeAsync(context,view,reference,proof.OppositeSource,proof.Internal,proof,token):
                throw new CadValidationException("Explicitly select and verify both original planar size faces.");
            if(verified is DrawingFeatureControlFrame bound)
            {
                var bindings=DrawingManufacturingRules.Bind(sheet,bound);
                if(!bound.DatumBindings.IsEmpty&&!bound.DatumBindings.SequenceEqual(bindings))
                    throw new CadValidationException("Explicitly reselect the changed datum system before confirming this frame.");
                verified=bound with{DatumBindings=bindings};
            }
            var next=verified switch
            {
                DrawingDatumSymbol d=>sheet with{DatumSymbols=replace?sheet.DatumSymbols.SetItem(sheet.DatumSymbols.FindIndex(x=>x.Id==id),d with{StaleReason=null}):sheet.DatumSymbols.Add(d with{StaleReason=null})},
                DrawingFeatureControlFrame f=>sheet with{ControlFrames=replace?sheet.ControlFrames.SetItem(sheet.ControlFrames.FindIndex(x=>x.Id==id),f with{StaleReason=null}):sheet.ControlFrames.Add(f with{StaleReason=null})},
                DrawingAssemblyBalloon b=>sheet with{Balloons=replace?sheet.Balloons.SetItem(sheet.Balloons.FindIndex(x=>x.Id==id),Balloon(b)):sheet.Balloons.Add(Balloon(b))},
                _=>throw new NotSupportedException()
            };
            if(verified is DrawingDatumSymbol changed&&replace)
            {
                var old=sheet.DatumSymbols.Single(d=>d.Id==id);
                if(old.Source.AuthorSelection()!=changed.Source.AuthorSelection()||old.FeatureType!=changed.FeatureType||
                    old.PlanarSizeEvidence?.OppositeSource.AuthorSelection()!=changed.PlanarSizeEvidence?.OppositeSource.AuthorSelection())
                    next=next with{ControlFrames=[..next.ControlFrames.Select(f=>f.ReferencedDatumIds.Contains(id)?
                        f with{StaleReason="A datum feature was reselected; explicitly confirm the control frame."}:f)]};
            }
            next.Validate(document.Id);
            if(value is DrawingFeatureControlFrame frame&&frame.ReferencedDatumIds.Any(d=>sheet.DatumSymbols.Single(x=>x.Id==d).StaleReason is not null))
                throw new CadValidationException("Reselect stale datum symbols before using them in a control frame.");
            token.ThrowIfCancellationRequested();
            return new((document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,next)}).WithNewState());
            DrawingAssemblyBalloon Balloon(DrawingAssemblyBalloon b)=>b with{StaleReason=null,
                Quantity=DrawingAssemblyParts.Quantity(document,sheet,b.Source.DefinitionId),PartName=document.Definitions[b.Source.DefinitionId].Name};
        }
    }
}
