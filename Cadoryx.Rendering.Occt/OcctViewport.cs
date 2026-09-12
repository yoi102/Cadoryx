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
    private HashSet<(OccurrencePath,BodyId)> highlighted=[];
    private int pressedButtons;
    private int lastX,lastY;
    private int pressX,pressY;
    private bool selectionClick;
    private ViewerPresentation? preview;
    private Shape? previewShape;
    private CadDisplayMode mode;
    private bool disposed;
    public ViewportCapabilities Capabilities {get;}=new(false,true,true);
    public event EventHandler<IReadOnlyList<SceneItem>>? SelectionChanged;
    public event EventHandler<(BoxBoundary First,BoxBoundary? Second)?>? BoxSubshapeSelected;
    private BoxRecipe? selectionBox;
    private TopologyKind? selectionKind;
    public void SetBoxSelection(BoxRecipe? box,TopologyKind? kind)
    {
        selectionBox=box;selectionKind=kind;viewer.ClearSelection();
        foreach(var entry in entries.Values)entry.Presentation.SetSelectionKind(kind is null?null:kind==TopologyKind.Face?ShapeKind.Face:ShapeKind.Edge);
    }
    public void HighlightBoxSelection(TopologyReference? reference)
    {
        foreach(var entry in entries.Values)
        {
            entry.Presentation.ClearAllSubshapeOverrides();
            if(reference is null||selectionBox is not {} box)continue;
            using var topology=entry.Geometry.Shape.GetTopologyAdjacency(ShapeKind.Edge,ShapeKind.Face);
            var parts=reference.Kind==TopologyKind.Face?topology.Ancestors:topology.Items;
            var candidates=parts.Where(s=>BoxTopology.Matches(s,box,reference.Kind,reference.Boundary,reference.SecondBoundary)).ToArray();
            if(candidates.Length==1)
            {
                entry.Presentation.SetSubshapeColor(candidates[0],new(1,0.65,0));
                if(reference.Kind==TopologyKind.Edge)entry.Presentation.SetSubshapeWidth(candidates[0],4);
            }
        }
        viewer.Redraw();
    }
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
                var presentation=CreatePresentation(item,resource,highlighted.Contains(key));
                created.Add(presentation);staged.Add(key,new(item,presentation,resource));
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
    private ViewerPresentation CreatePresentation(SceneItem item,GeometryResource resource,bool selected)
    {
        bool sourceStyles=item.PreserveSourceStyles&&!selected&&resource.Label is not null;
        var presentation=sourceStyles?viewer.Display(resource.Label!):viewer.Display(resource.Shape);
        try
        {
            using var transform=OcctGeometryBridge.ToNative(item.WorldTransform*resource.SourceLocationInverse);
            presentation.SetTransform(transform);
            if(!sourceStyles)
            {
                var color=OcctGeometryBridge.ToXdeColor(selected?0xFFFF6B0Au:item.Argb);
                presentation.SetColor(new(color.Red,color.Green,color.Blue));presentation.SetTransparency(1-color.Alpha);
            }
            presentation.SetDisplayMode(mode==CadDisplayMode.Shaded?ViewerDisplayMode.Shaded:ViewerDisplayMode.Wireframe);
            if(selectionKind is {} kind)presentation.SetSelectionKind(kind==TopologyKind.Face?ShapeKind.Face:ShapeKind.Edge);
            return presentation;
        }
        catch{presentation.Dispose();throw;}
    }
    public void Highlight(IEnumerable<(OccurrencePath Path,BodyId Body)> selected)
    {
        var keys=selected.ToHashSet();
        var replacements=new Dictionary<(OccurrencePath,BodyId),Entry>();
        try
        {
            foreach(var (key,entry) in entries)
                if(keys.Contains(key)!=highlighted.Contains(key))
                    replacements.Add(key,new(entry.Item,CreatePresentation(entry.Item,entry.Geometry,keys.Contains(key)),entry.Geometry));
        }
        catch{foreach(var entry in replacements.Values)entry.Presentation.Dispose();throw;}
        // Rebuild only changed highlights. Clearing XCAFPrs custom aspects also erases source face styles.
        foreach(var (key,entry) in replacements){entries[key].Presentation.Dispose();entries[key]=entry;}
        highlighted=keys;
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
    public bool HasPointerCapture=>pressedButtons!=0;
    public void PointerMoved(int x,int y,int buttons,int modifiers)
    {
        lastX=x;lastY=y;
        if(Math.Abs(x-pressX)>2||Math.Abs(y-pressY)>2)selectionClick=false;
        viewer.Input.PointerMoved(x,y,(ViewerPointerButtons)(buttons&pressedButtons),(ViewerModifierKeys)modifiers);
    }
    public void PointerPressed(int button,int x,int y,int modifiers)
    {
        lastX=x;lastY=y;pressedButtons|=1<<button;
        pressX=x;pressY=y;selectionClick=button==0;
        viewer.Input.PointerPressed(Button(button),x,y,(ViewerModifierKeys)modifiers);
    }
    public void PointerReleased(int button,int x,int y,int modifiers)
    {
        if((pressedButtons&(1<<button))==0)return; // Late release after capture loss must not clear model selection.
        pressedButtons&=~(1<<button);lastX=x;lastY=y;
        var selected=viewer.Input.PointerReleased(Button(button),x,y);
        bool clicked=button==0&&selectionClick;selectionClick=false;
        if(!clicked)return;
        if(selectionKind is {} kind&&selectionBox is {} box)
        {
            var pickedItems=viewer.GetSelectedItems();
            try
            {
                (BoxBoundary First,BoxBoundary? Second)? value=null;
                if(pickedItems.Count==1)
                {
                    var candidates=BoxTopology.Classify(pickedItems[0].Shape,box,kind);
                    if(candidates.Count==1)value=candidates[0];
                }
                BoxSubshapeSelected?.Invoke(this,value);
            }
            finally{foreach(var item in pickedItems)item.Dispose();}
            return;
        }
        var hit=entries.Where(e=>selected.Contains(e.Value.Presentation)).Select(e=>e.Key).ToHashSet();
        var keys=new HashSet<(OccurrencePath,BodyId)>(highlighted);
        if((modifiers&2)!=0)keys.SymmetricExceptWith(hit);
        else if((modifiers&4)!=0)keys.ExceptWith(hit);
        else if((modifiers&1)!=0)keys.UnionWith(hit);
        else keys=hit;
        var items=entries.Where(e=>keys.Contains(e.Key)).Select(e=>e.Value.Item).ToArray();
        SelectionChanged?.Invoke(this,items);
    }
    public void CancelInput()
    {
        pressedButtons=0;selectionClick=false;
        // preview.26 clears any pressed button on release. Right avoids synthesizing a left click.
        viewer.Input.PointerReleased(ViewerPointerButton.Right,lastX,lastY);
    }
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
    public (int X,int Y) WorldToScreen(Vector3d point)
    {
        var pixel=viewer.WorldToScreen(new(point.X,point.Y,point.Z));return(pixel.X,pixel.Y);
    }
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
                    context=OcctGeometryBridge.ReadContext(source.ContextAssetId,assets,source.Format);Label=context.GetLabel(source.DefinitionEntry);
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
