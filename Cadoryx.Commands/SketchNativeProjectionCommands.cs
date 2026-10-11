using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

internal static class SketchNativeProjectionCommands
{
    internal static ExactSketchCurve Curve(CadSketch sketch,SketchEntityId id)
    {
        if(sketch.Freeforms.SingleOrDefault(e=>e.Id==id) is {} freeform)
            return new(freeform.Geometry.At(0),freeform.Geometry.At(1),Freeform:freeform.Geometry);
        if(sketch.Ellipses.SingleOrDefault(e=>e.Id==id) is {} ellipse)
            return new(ellipse.Geometry.At(0),ellipse.Geometry.At(1),Ellipse:ellipse.Geometry);
        var line=sketch.Lines.Single(l=>l.Id==id);
        return new(sketch.Points.Single(p=>p.Id==line.Start).Position,sketch.Points.Single(p=>p.Id==line.End).Position);
    }
    internal static async Task ConfirmAsync(DocumentCommandContext context,CadSketch sketch,CancellationToken token)
    {
        if(sketch.NativeProjections.IsEmpty)return;
        if(context.Kernel is not ISketchEdgeProjector projector)throw new NotSupportedException("The active kernel cannot confirm exact edge projections.");
        foreach(var p in sketch.NativeProjections)
        {
            token.ThrowIfCancellationRequested();
            if(!SketchNativeProjectionGeometry.IsCurrent(context.Snapshot,sketch,p))
                throw SketchCurveFailure.Create("NativeProjectionFrozen","Native projection source or sketch plane changed. Detach or explicitly reselect it.");
            await projector.ConfirmEdgeProjectionAsync(new(sketch.PartId,p.Selection,p.Source,p.TargetPlane,Curve(sketch,p.TargetEntity)),context.Assets,token).ConfigureAwait(false);
        }
    }
}
