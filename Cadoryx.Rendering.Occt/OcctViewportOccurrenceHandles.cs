using Cadoryx.Db;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using OcctSharp;

namespace Cadoryx.Rendering.Occt;

public sealed partial class OcctViewport
{
    private readonly List<(Vector3d Point,Vector3d Axis,double Span,Shape Shape,ViewerPresentation Presentation,
        Shape Line,ViewerPresentation LinePresentation)> occurrenceHandles=[];
    private OccurrencePath? handleOccurrence,dragOccurrence;
    private Vector3d dragAxis;
    private double dragSpan;
    private (int X,int Y) occurrenceHandlePixel,occurrenceAxisPixel;
    private int occurrenceStartX,occurrenceStartY;
    private Vector3d occurrenceDelta;
    public event EventHandler<(OccurrencePath Path,Vector3d WorldDelta,bool Commit)>? OccurrenceHandleFinished;
    public IReadOnlyList<(Vector3d Point,Vector3d Axis)> OccurrenceHandleTargets=>
        occurrenceHandles.Select(h=>(h.Point,h.Axis*h.Span)).ToArray();

    public void SetOccurrenceHandles(OccurrencePath? path,BodyId? focusBody=null)
    {
        if(dragOccurrence is not null)FinishOccurrenceHandle(false);
        ClearOccurrenceHandles();
        if(path is null||constructing||assemblyDatumSelection is not null||selectionKind is not null)return;
        var selected=entries.Values.Where(e=>OccurrenceDrag.Contains(path,e.Item.Path)).Select(e=>e.Item).ToArray();
        if(selected.Length==0)return;
        var focus=selected.FirstOrDefault(i=>i.BodyId==focusBody)??
            selected.OrderByDescending(i=>(i.Geometry.Bounds.Max-i.Geometry.Bounds.Min).Length).First();
        var bounds=SceneEnvelope.Measure([focus])!.Value;
        var center=(bounds.Min+bounds.Max)/2;
        // OCCT's point conversion can overestimate a local pixel/mm derivative near
        // the camera depth plane. Keep a minimum fraction of the visible camera span.
        double initialSpan=Math.Clamp(Math.Max(28/PixelsPerMillimeter(center),
            CaptureCamera().Scale*0.06),0.2,1_000_000);
        var axes=new[]{(new Vector3d(1,0,0),new ViewerColor(0.95,0.25,0.25)),
            (new Vector3d(0,1,0),new ViewerColor(0.25,0.85,0.35)),
            (new Vector3d(0,0,1),new ViewerColor(0.25,0.55,0.95))};
        try
        {
            foreach(var (axis,color) in axes)
            {
                double extent=axis.X!=0?(bounds.Max.X-bounds.Min.X)/2:
                    axis.Y!=0?(bounds.Max.Y-bounds.Min.Y)/2:(bounds.Max.Z-bounds.Min.Z)/2;
                var facePoint=center+axis*extent;
                var facePixel=WorldToScreen(facePoint);
                double span=initialSpan;
                var point=facePoint+axis*span;var pixel=WorldToScreen(point);
                while(Math.Pow(pixel.X-facePixel.X,2)+Math.Pow(pixel.Y-facePixel.Y,2)<24*24&&span<1_000_000)
                {span*=2;point=facePoint+axis*span;pixel=WorldToScreen(point);}
                if(Math.Pow(pixel.X-facePixel.X,2)+Math.Pow(pixel.Y-facePixel.Y,2)<10*10)continue;
                var shape=ShapeFactory.CreateSphere(Math.Clamp(span*0.18,0.2,1_000_000));
                Shape? line=null;ViewerPresentation? presentation=null,linePresentation=null;
                try
                {
                    line=ShapeFactory.CreateEdge(new(facePoint.X,facePoint.Y,facePoint.Z),
                        new(point.X,point.Y,point.Z));
                    linePresentation=viewer.Display(line);linePresentation.SetColor(color);
                    linePresentation.SetDisplayMode(ViewerDisplayMode.Wireframe);
                    linePresentation.SetSelectionKind(ShapeKind.Face);
                    presentation=viewer.Display(shape);
                    using var transform=OcctGeometryBridge.ToNative(RigidTransform3d.Translate(point.X,point.Y,point.Z));
                    presentation.SetTransform(transform);presentation.SetColor(color);
                    presentation.SetDisplayMode(ViewerDisplayMode.Shaded);
                    presentation.SetSelectionKind(ShapeKind.Edge);
                    occurrenceHandles.Add((point,axis,span,shape,presentation,line,linePresentation));
                }
                catch{presentation?.Dispose();shape.Dispose();linePresentation?.Dispose();line?.Dispose();throw;}
            }
            handleOccurrence=path;viewer.Redraw();
        }
        catch{ClearOccurrenceHandles();throw;}
    }

    private void ClearOccurrenceHandles()
    {
        bool hadHandles=occurrenceHandles.Count>0;
        foreach(var (_,_,_,shape,presentation,line,linePresentation) in occurrenceHandles)
        {presentation.Dispose();shape.Dispose();linePresentation.Dispose();line.Dispose();}
        occurrenceHandles.Clear();handleOccurrence=null;
        if(hadHandles&&!disposed)viewer.Redraw();
    }

    private bool TryStartOccurrenceHandle(int x,int y)
    {
        if(handleOccurrence is not {} path)return false;
        foreach(var (point,axis,span,_,_,_,_) in occurrenceHandles)
        {
            var pixel=WorldToScreen(point);
            if(Math.Pow(pixel.X-x,2)+Math.Pow(pixel.Y-y,2)>14*14)continue;
            var next=WorldToScreen(point+axis*span);
            if(Math.Pow(next.X-pixel.X,2)+Math.Pow(next.Y-pixel.Y,2)<4)continue;
            dragOccurrence=path;dragAxis=axis;dragSpan=span;occurrenceHandlePixel=pixel;occurrenceAxisPixel=next;
            occurrenceStartX=x;occurrenceStartY=y;occurrenceDelta=Vector3d.Zero;
            return true;
        }
        return false;
    }

    private void MoveOccurrenceHandle(int x,int y)
    {
        if(dragOccurrence is null)return;
        double dx=occurrenceAxisPixel.X-occurrenceHandlePixel.X,dy=occurrenceAxisPixel.Y-occurrenceHandlePixel.Y;
        double amount=((x-occurrenceStartX)*dx+(y-occurrenceStartY)*dy)/(dx*dx+dy*dy);
        if(!double.IsFinite(amount))return;
        var delta=dragAxis*Math.Clamp(amount*dragSpan,-1_000_000,1_000_000);
        if(delta==occurrenceDelta)return;
        occurrenceDelta=delta;
        foreach(var entry in entries.Values.Where(e=>OccurrenceDrag.Contains(dragOccurrence,e.Item.Path)))
        {
            var transform=entry.Item.WorldTransform with{Translation=entry.Item.WorldTransform.Translation+delta};
            using var native=OcctGeometryBridge.ToNative(entry.Geometry.Label is null?transform:
                transform*entry.Geometry.SourceLocationInverse);
            entry.Presentation.SetTransform(native);
        }
        viewer.Redraw();
    }

    private void FinishOccurrenceHandle(bool commit)
    {
        if(dragOccurrence is not {} path)return;
        var delta=occurrenceDelta;dragOccurrence=null;occurrenceDelta=Vector3d.Zero;
        if(!commit||delta.Length<1e-7)
        {
            foreach(var entry in entries.Values.Where(e=>OccurrenceDrag.Contains(path,e.Item.Path)))
            {
                using var native=OcctGeometryBridge.ToNative(entry.Geometry.Label is null?entry.Item.WorldTransform:
                    entry.Item.WorldTransform*entry.Geometry.SourceLocationInverse);
                entry.Presentation.SetTransform(native);
            }
            viewer.Redraw();
        }
        OccurrenceHandleFinished?.Invoke(this,(path,delta,commit&&delta.Length>=1e-7));
    }
}
