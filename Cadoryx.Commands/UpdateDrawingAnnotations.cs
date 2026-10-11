using Cadoryx.Db;

namespace Cadoryx.Commands;

internal static class UpdateDrawingAnnotations
{
    internal static async Task<TechnicalDrawingSheet> PrepareAsync(DocumentCommandContext context,TechnicalDrawingSheet sheet,CancellationToken token)
    {
        if(sheet.DatumSymbols.IsEmpty&&sheet.ControlFrames.IsEmpty&&sheet.Balloons.IsEmpty)return sheet;
        async Task<(DrawingTopologyReference Source,DrawingCylinderSizeEvidence? Size,DrawingPlanarSizeEvidence? Planar,string? Problem)> Check(Guid viewId,
            DrawingTopologyReference source,DrawingCylinderSizeEvidence? size=null,bool requireSize=false,DrawingPlanarSizeEvidence? planar=null,bool requirePlanar=false)
        {
            try
            {
                var current=await DrawingAttachmentContinuation.ResolveAsync(context,sheet.Views.Single(v=>v.Id==viewId),source,token).ConfigureAwait(false);
                DrawingCylinderSizeEvidence? proof=size;
                if(requireSize)
                {
                    if(size is null)throw new CadValidationException("Legacy axis annotation requires explicit original size reselection.");
                    proof=await DrawingAnnotationCommands.SizeAsync(context,current,current==source?size:null,token).ConfigureAwait(false);
                }
                DrawingPlanarSizeEvidence? planarProof=planar;
                if(requirePlanar)
                {
                    if(planar is null)throw new CadValidationException("A median plane requires explicit original two-face size reselection.");
                    var view=sheet.Views.Single(v=>v.Id==viewId);
                    var opposite=await DrawingAttachmentContinuation.ResolveAsync(context,view,planar.OppositeSource,token).ConfigureAwait(false);
                    planarProof=await DrawingAnnotationCommands.PlanarSizeAsync(context,view,current,opposite,planar.Internal,
                        current==source&&opposite==planar.OppositeSource?planar:null,token).ConfigureAwait(false);
                }
                return (current,proof,planarProof,null);
            }
            catch(Exception error) when(error is not OperationCanceledException)
            {var problem="Manufacturing attachment is stale; explicitly reselect it: "+error.Message;return (source,size,planar,problem[..Math.Min(2048,problem.Length)]);}
        }
        var datums=sheet.DatumSymbols.ToBuilder();
        for(int i=0;i<datums.Count;i++)
        {
            var checkedDatum=await Check(datums[i].ViewId,datums[i].Source,datums[i].SizeEvidence,datums[i].FeatureType==DrawingDatumFeatureType.CylinderSizeAxis,
                datums[i].PlanarSizeEvidence,datums[i].FeatureType==DrawingDatumFeatureType.PlanarSizeMedianPlane).ConfigureAwait(false);
            datums[i]=datums[i] with{Source=checkedDatum.Source,SizeEvidence=checkedDatum.Size,PlanarSizeEvidence=checkedDatum.Planar,StaleReason=checkedDatum.Problem};
        }
        var frames=sheet.ControlFrames.ToBuilder();
        for(int i=0;i<frames.Count;i++)
        {
            var frame=frames[i];var checkedFrame=await Check(frame.ViewId,frame.Source,frame.SizeEvidence,frame.Target==DrawingToleranceTarget.Axis,
                frame.PlanarSizeEvidence,frame.Target==DrawingToleranceTarget.MedianPlane).ConfigureAwait(false);
            var problem=checkedFrame.Problem;
            var refreshed=frame with{Source=checkedFrame.Source,SizeEvidence=checkedFrame.Size,PlanarSizeEvidence=checkedFrame.Planar};
            if(problem is null&&frame.ReferencedDatumIds.Any(id=>datums.Single(d=>d.Id==id).StaleReason is not null))problem="A referenced datum symbol is stale.";
            if(problem is null)
            {
                try
                {
                    var currentSheet=sheet with{DatumSymbols=datums.ToImmutable()};var bindings=DrawingManufacturingRules.Bind(currentSheet,frame);
                    if(bindings.Length!=frame.DatumBindings.Length||bindings.Where((b,j)=>b.Id!=frame.DatumBindings[j].Id||
                        b.FeatureType!=frame.DatumBindings[j].FeatureType||b.Source.AuthorSelection()!=frame.DatumBindings[j].Source.AuthorSelection()||
                        b.OppositeSource?.AuthorSelection()!=frame.DatumBindings[j].OppositeSource?.AuthorSelection()).Any())
                        throw new CadValidationException("A referenced datum author selection changed; explicitly confirm the control frame.");
                    if(!bindings.SequenceEqual(frame.DatumBindings))refreshed=refreshed with{DatumBindings=bindings};
                    DrawingManufacturingRules.Validate(currentSheet,refreshed with{StaleReason=null});
                }
                catch(CadValidationException error){problem=error.Message;}
            }
            frames[i]=problem is null?refreshed with{StaleReason=null}:frame with{StaleReason=problem};
        }
        var balloons=sheet.Balloons.ToBuilder();
        for(int i=0;i<balloons.Count;i++)
        {
            var balloon=balloons[i];var checkedBalloon=await Check(balloon.ViewId,balloon.Source).ConfigureAwait(false);
            balloons[i]=checkedBalloon.Problem is not null?balloon with{StaleReason=checkedBalloon.Problem}:balloon with{Source=checkedBalloon.Source,StaleReason=null,
                Quantity=DrawingAssemblyParts.Quantity(context.Snapshot,sheet,balloon.Source.DefinitionId),
                PartName=context.Snapshot.Definitions[balloon.Source.DefinitionId].Name};
        }
        return sheet with{DatumSymbols=datums.ToImmutable(),ControlFrames=frames.ToImmutable(),Balloons=balloons.ToImmutable()};
    }
}
