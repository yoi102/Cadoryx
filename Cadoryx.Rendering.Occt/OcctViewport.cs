using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;

namespace Cadoryx.Rendering.Occt;

/// <summary>UI-thread-owned OCCT scene adapter. Its source geometry is independent of worker inputs.</summary>
public sealed class OcctViewport : ICadViewport
{
    private readonly OcctViewer viewer;
    private readonly IAssetStore assets;
    private readonly Dictionary<(OccurrencePath,BodyId),Entry> entries=[];
    private readonly Dictionary<GeometryRevisionId,GeometryResource> geometry=[];
    private ViewerPresentation? preview;
    private Shape? previewShape;
    private CadDisplayMode mode;
    private bool disposed;
    public ViewportCapabilities Capabilities {get;}=new(false,false,true);
    public event EventHandler<IReadOnlyList<SceneItem>>? SelectionChanged;
    public OcctViewport(nint windowHandle,IAssetStore assets)
    {
        this.assets=assets;viewer=OcctViewer.Create(windowHandle);
        try
        {
            viewer.SetBackgroundColor(new ViewerColor(0.035,0.05,0.075));viewer.SetProjection(ViewerProjection.Axonometric);
            viewer.ShowTrihedron(ViewerTrihedronPosition.LeftLower,new(0.9,0.9,0.9),0.08);
            viewer.Rendering.SetProfile(new ViewerRenderProfile{Shading=ViewerShading.Phong});
            viewer.Rendering.ReplaceLightRig([
                new(ViewerLightKind.Ambient,new(1,1,1)){Intensity=0.25},
                new(ViewerLightKind.Directional,new(1,1,1)){Direction=new(-1,-2,-3),Intensity=1,Headlight=true}]);
        }
        catch{viewer.Dispose();throw;}
    }
    public void SetScene(CadScene scene)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var staged=new Dictionary<(OccurrencePath,BodyId),Entry>();
        var created=new List<ViewerPresentation>();
        try
        {
            foreach(var item in scene.Items)
            {
                var key=(item.Path,item.BodyId);
                if(entries.TryGetValue(key,out var existing)&&existing.Item==item){staged.Add(key,existing);continue;}
                var resource=Resource(item.Geometry);
                ViewerPresentation presentation=resource.Label is {} label?viewer.Display(label):viewer.Display(resource.Shape);
                created.Add(presentation);Apply(presentation,item,resource);staged.Add(key,new(item,presentation,resource));
            }
        }
        catch{foreach(var p in created)p.Dispose();PruneGeometry();viewer.Redraw();throw;}
        foreach(var (key,entry) in entries)if(!staged.TryGetValue(key,out var replacement)||!ReferenceEquals(entry,replacement))entry.Presentation.Dispose();
        entries.Clear();foreach(var pair in staged)entries.Add(pair.Key,pair.Value);
        PruneGeometry();viewer.Redraw();
    }
    private void PruneGeometry()
    {
        var used=entries.Values.Select(e=>e.Item.Geometry.Revision).ToHashSet();
        foreach(var key in geometry.Keys.Where(k=>!used.Contains(k)).ToArray()){geometry[key].Dispose();geometry.Remove(key);}
    }
    private GeometryResource Resource(GeometryAssetRef reference)
    {
        if(geometry.TryGetValue(reference.Revision,out var resource))return resource;
        resource=new(reference,assets);geometry.Add(reference.Revision,resource);return resource;
    }
    private void Apply(ViewerPresentation presentation,SceneItem item,GeometryResource resource)
    {
        using var transform=OcctGeometryBridge.ToNative(item.WorldTransform*resource.SourceLocationInverse);
        presentation.SetTransform(transform);
        var color=OcctGeometryBridge.ToXdeColor(item.Argb);presentation.SetColor(new(color.Red,color.Green,color.Blue));
        presentation.SetTransparency(1-color.Alpha);
        presentation.SetDisplayMode(mode==CadDisplayMode.Shaded?ViewerDisplayMode.Shaded:ViewerDisplayMode.Wireframe);
    }
    public void Highlight(IEnumerable<(OccurrencePath Path,BodyId Body)> selected)
    {
        var keys=selected.ToHashSet();
        foreach(var pair in entries)
        {
            pair.Value.Presentation.ClearAllSubshapeOverrides();
            if(keys.Contains(pair.Key))pair.Value.Presentation.SetSubshapeColor(pair.Value.Geometry.Shape,new ViewerColor(1,0.42,0.06));
        }
        viewer.Redraw();
    }
    public void SetPreview(GeometryAssetRef? reference)
    {
        preview?.Dispose();preview=null;previewShape?.Dispose();previewShape=null;
        if(reference is not null&&reference.Kind!=BodyKind.Empty)
        {
            previewShape=OcctGeometryBridge.ReadShape(reference,assets);
            try{preview=viewer.Display(previewShape);preview.SetColor(new(0.1,0.8,0.55));preview.SetTransparency(0.55);}
            catch{previewShape.Dispose();previewShape=null;throw;}
        }
        viewer.Redraw();
    }
    public void FitAll()=>viewer.FitAll();
    public void SetProjection(CadProjection projection)=>viewer.SetProjection(projection switch
    {
        CadProjection.Front=>ViewerProjection.Front,CadProjection.Top=>ViewerProjection.Top,
        CadProjection.Right=>ViewerProjection.Right,CadProjection.Left=>ViewerProjection.Left,
        CadProjection.Back=>ViewerProjection.Back,CadProjection.Bottom=>ViewerProjection.Bottom,_=>ViewerProjection.Axonometric
    });
    public void SetDisplayMode(CadDisplayMode displayMode)
    {
        mode=displayMode;foreach(var e in entries.Values)e.Presentation.SetDisplayMode(mode==CadDisplayMode.Shaded?ViewerDisplayMode.Shaded:ViewerDisplayMode.Wireframe);viewer.Redraw();
    }
    public void Resize()=>viewer.Resize();
    public void Redraw()=>viewer.Redraw();
    public void PointerMoved(int x,int y,int buttons,int modifiers)=>viewer.Input.PointerMoved(x,y,(ViewerPointerButtons)buttons,(ViewerModifierKeys)modifiers);
    public void PointerPressed(int button,int x,int y,int modifiers)=>viewer.Input.PointerPressed(Button(button),x,y,(ViewerModifierKeys)modifiers);
    public void PointerReleased(int button,int x,int y,int modifiers)
    {
        var selected=viewer.Input.PointerReleased(Button(button),x,y,(ViewerModifierKeys)modifiers);
        if(button!=0)return;
        var items=entries.Values.Where(e=>selected.Contains(e.Presentation)).Select(e=>e.Item).ToArray();
        SelectionChanged?.Invoke(this,items);
    }
    public void CancelInput()=>viewer.Input.PointerReleased(ViewerPointerButton.Right,0,0);
    public void ClearSelection()=>viewer.ClearSelection();
    public void MouseWheel(int delta,int x,int y,int modifiers)=>viewer.Input.MouseWheel(delta,x,y,(ViewerModifierKeys)modifiers);
    private static ViewerPointerButton Button(int button)=>button switch{0=>ViewerPointerButton.Left,1=>ViewerPointerButton.Middle,_=>ViewerPointerButton.Right};
    public CadCamera CaptureCamera()
    {
        var c=viewer.Rendering.GetCamera();return new(new(c.Eye.X,c.Eye.Y,c.Eye.Z),new(c.Target.X,c.Target.Y,c.Target.Z),new(c.Up.X,c.Up.Y,c.Up.Z),
            c.Aspect,c.Scale,c.FieldOfViewY,c.NearPlane,c.FarPlane,c.Perspective,c.AutoFitDepth);
    }
    public void RestoreCamera(CadCamera c)=>viewer.Rendering.SetCamera(new(new(c.Eye.X,c.Eye.Y,c.Eye.Z),new(c.Target.X,c.Target.Y,c.Target.Z),new(c.Up.X,c.Up.Y,c.Up.Z),
        c.Aspect,c.Scale,c.FieldOfViewY,c.NearPlane,c.FarPlane,c.Perspective,c.AutoFitDepth));
    public void SaveScreenshot(string path)=>viewer.SaveScreenshot(path,overwrite:true);
    public void Dispose()
    {
        if(disposed)return;
        preview?.Dispose();previewShape?.Dispose();
        foreach(var e in entries.Values)e.Presentation.Dispose();entries.Clear();
        viewer.Dispose();foreach(var g in geometry.Values)g.Dispose();geometry.Clear();disposed=true;
    }
    private sealed record Entry(SceneItem Item,ViewerPresentation Presentation,GeometryResource Geometry);
    private sealed class GeometryResource : IDisposable
    {
        private readonly IAssetLease lease;
        private IAssetLease? sourceLease;
        private XdeDocument? context;
        public Shape Shape {get;}
        public XdeLabel? Label {get;}
        public RigidTransform3d SourceLocationInverse {get;}=RigidTransform3d.Identity;
        public GeometryResource(GeometryAssetRef reference,IAssetStore assets)
        {
            lease=assets.Acquire(reference.AssetId);
            try
            {
                if(reference.Source is {} source)
                {
                    sourceLease=assets.Acquire(source.ContextAssetId);
                    context=OcctGeometryBridge.ReadContext(source.ContextAssetId,assets);Label=context.GetLabel(source.DefinitionEntry);
                    Shape=Label.Shape;using var location=Label.Location;using var t=location.ToTransform();
                    SourceLocationInverse=OcctGeometryBridge.FromNative(t).Inverse();
                }
                else Shape=OcctGeometryBridge.ReadShape(reference,assets);
            }
            catch{Shape?.Dispose();context?.Dispose();sourceLease?.Dispose();lease.Dispose();throw;}
        }
        public void Dispose(){Shape.Dispose();context?.Dispose();sourceLease?.Dispose();lease.Dispose();}
    }
}
