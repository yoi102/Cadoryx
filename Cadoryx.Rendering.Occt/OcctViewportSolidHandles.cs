using Cadoryx.Db;
using Cadoryx.Kernel.Occt;
using OcctSharp;

namespace Cadoryx.Rendering.Occt;

public sealed partial class OcctViewport
{
    private readonly List<(SolidDimensionHandle Handle,Shape Shape,ViewerPresentation Presentation)> solidHandles=[];
    private SolidDimensionHandle? draggingSolidHandle;
    private (int X,int Y) dragHandlePixel,dragAxisPixel;
    private int dragStartX,dragStartY;
    private double lastDragValue;
    private GeometryRecipe? solidHandleRecipe;
    private RigidTransform3d solidHandleOccurrence=RigidTransform3d.Identity;
    public event EventHandler<(SolidDimension Dimension,double Value)>? SolidHandleChanged;
    public event EventHandler<(SolidDimension Dimension,double Initial,bool Commit)>? SolidHandleFinished;
    public IReadOnlyList<double> SolidHandleScreenRadii=>solidHandles.Select(h=>
        (h.Shape.GetBoundingBox().Maximum.X-h.Shape.GetBoundingBox().Minimum.X)*
        PixelsPerMillimeter(h.Handle.WorldPoint)/2).ToArray();

    public void SetSolidHandles(GeometryRecipe? recipe,RigidTransform3d occurrence)
    {
        ClearSolidHandles();
        solidHandleRecipe=recipe;solidHandleOccurrence=occurrence;
        if(recipe is null)return;
        var handles=SolidDimensionHandles.Describe(recipe,occurrence);
        try
        {
            foreach(var handle in handles)
            {
                var shape=ShapeFactory.CreateSphere(Math.Clamp(8/PixelsPerMillimeter(handle.WorldPoint),0.5,8));
                ViewerPresentation? presentation=null;
                try
                {
                    presentation=viewer.Display(shape);
                    using var transform=OcctGeometryBridge.ToNative(RigidTransform3d.Translate(
                        handle.WorldPoint.X,handle.WorldPoint.Y,handle.WorldPoint.Z));
                    presentation.SetTransform(transform);
                    presentation.SetColor(new(1,0.58,0.08));
                    presentation.SetOverlay(true);
                    presentation.SetSelectionKind(ShapeKind.Edge); // Sphere has no edges; model picking stays intact.
                }
                catch{presentation?.Dispose();shape.Dispose();throw;}
                solidHandles.Add((handle,shape,presentation));
            }
            viewer.Redraw();
        }
        catch{ClearSolidHandles();throw;}
    }

    private void ClearSolidHandles()
    {
        foreach(var (_,shape,presentation) in solidHandles){presentation.Dispose();shape.Dispose();}
        solidHandles.Clear();
        solidHandleRecipe=null;
        if(!disposed)viewer.Redraw();
    }
    public void UpdateSolidHandlePositions(GeometryRecipe? recipe,RigidTransform3d occurrence)
    {
        if(recipe is null||solidHandles.Count==0)return;
        var points=SolidDimensionHandles.Describe(recipe,occurrence).ToDictionary(h=>h.Dimension);
        foreach(var (handle,_,presentation) in solidHandles)
        {
            if(!points.TryGetValue(handle.Dimension,out var next))continue;
            using var transform=OcctGeometryBridge.ToNative(RigidTransform3d.Translate(
                next.WorldPoint.X,next.WorldPoint.Y,next.WorldPoint.Z));
            presentation.SetTransform(transform);
        }
        viewer.Redraw();
    }

    private bool TryStartSolidHandle(int x,int y)
    {
        foreach(var (handle,_,_) in solidHandles)
        {
            var point=WorldToScreen(handle.WorldPoint);
            if(Math.Pow(point.X-x,2)+Math.Pow(point.Y-y,2)>14*14)continue;
            var axis=WorldToScreen(handle.WorldPoint+handle.WorldAxis);
            double dx=axis.X-point.X,dy=axis.Y-point.Y;
            if(dx*dx+dy*dy<4)continue; // Axis is edge-on; leave camera navigation available.
            draggingSolidHandle=handle;dragHandlePixel=point;dragAxisPixel=axis;
            dragStartX=x;dragStartY=y;lastDragValue=handle.Value;
            return true;
        }
        return false;
    }

    private void MoveSolidHandle(int x,int y)
    {
        if(draggingSolidHandle is not {} handle)return;
        double value=SolidDimensionHandles.DragValue(handle.Value,dragStartX,dragStartY,x,y,
            dragHandlePixel,dragAxisPixel);
        if(handle.Dimension==SolidDimension.RevolveAngle)value=Math.Clamp(value,0.001,Math.PI*2);
        if(Math.Abs(value-lastDragValue)<1e-7)return;
        lastDragValue=value;
        SolidHandleChanged?.Invoke(this,(handle.Dimension,value));
    }

    private void FinishSolidHandle(bool commit)
    {
        if(draggingSolidHandle is not {} handle)return;
        draggingSolidHandle=null;
        SolidHandleFinished?.Invoke(this,(handle.Dimension,handle.Value,commit));
    }
}
