using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Rendering.Occt;

public sealed partial class OcctViewport
{
    private Shape? selectionRectangleShape;
    private ViewerPresentation? selectionRectangle;
    private (int X,int Y)? selectionRectangleEnd;
    public bool HasSelectionRectangle=>selectionRectangle is not null;

    private bool ClearSelectionRectangle()
    {
        bool shown=HasSelectionRectangle;
        Exception? failure=null;
        DisposeOwnedResource(selectionRectangle,ref failure);selectionRectangle=null;
        DisposeOwnedResource(selectionRectangleShape,ref failure);selectionRectangleShape=null;
        selectionRectangleEnd=null;ThrowCleanupFailure(failure);
        return shown;
    }

    private void CancelSelectionRectangleGesture()
    {
        // Camera/scene changes invalidate screen coordinates for an ordinary
        // selection gesture. Handle and construction gestures keep their routes.
        if(selectionDragAllowed&&pressedButtons==1&&!constructing&&!cubeClick&&
            dragOccurrence is null&&draggingSolidHandle is null)
            CancelInput();
        else if(ClearSelectionRectangle())viewer.Redraw();
    }

    private void UpdateSelectionRectangle(int x,int y,int buttons)
    {
        if(!selectionDragAllowed||pressedButtons!=1||buttons!=1||constructing||cubeClick||selectionClick||
            dragOccurrence is not null||draggingSolidHandle is not null||Math.Abs(x-pressX)<=2||Math.Abs(y-pressY)<=2)
        {
            if(ClearSelectionRectangle())viewer.Redraw();
            return;
        }
        if(selectionRectangleEnd==(x,y))return;
        var camera=CaptureCamera();var normal=(camera.Eye-camera.Target).Normalized();
        Vector3d At(int sx,int sy)
        {
            // OCCT's integer pick coordinates address pixel boundaries; wire
            // rasterization uses pixel centers. Align the visible stroke with
            // the pointer rectangle, including the first/last client pixels.
            var ray=viewer.GetPickRay(sx+1,sy-1);
            var origin=new Vector3d(ray.Origin.X,ray.Origin.Y,ray.Origin.Z);
            var direction=new Vector3d(ray.Direction.X,ray.Direction.Y,ray.Direction.Z);
            double denominator=normal.Dot(direction);
            if(denominator==0)throw new InvalidOperationException("Selection outline ray is parallel to the camera plane.");
            double distance=normal.Dot(camera.Target-origin)/denominator;
            if(!double.IsFinite(distance)||distance<0)throw new InvalidOperationException("Selection outline is outside the camera plane.");
            return origin+direction*distance;
        }
        Vector3d[] points=[At(pressX,pressY),At(x,pressY),At(x,y),At(pressX,y)];
        var edges=new List<Shape>();Shape? shape=null;ViewerPresentation? presentation=null;
        try
        {
            for(int index=0;index<4;index++)
            {
                var a=points[index];var b=points[(index+1)%4];
                edges.Add(ShapeFactory.CreateEdge(new(a.X,a.Y,a.Z),new(b.X,b.Y,b.Z)));
            }
            shape=ShapeFactory.CreateCompound(edges);presentation=viewer.Display(shape);
            presentation.SetDisplayMode(ViewerDisplayMode.Wireframe);presentation.SetColor(new(.05,.65,.95));
            presentation.SetOverlay(true);
            // The compound contains edges only. Face selection creates no
            // sensitive entities, so the outline cannot become a model pick.
            presentation.SetSelectionKind(ShapeKind.Face);
            ClearSelectionRectangle();selectionRectangleShape=shape;selectionRectangle=presentation;
            selectionRectangleEnd=(x,y);shape=null;presentation=null;viewer.Redraw();
        }
        finally{presentation?.Dispose();shape?.Dispose();foreach(var edge in edges)edge.Dispose();}
    }
}
