using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;

namespace Cadoryx.Rendering.Occt;

public sealed record LocalTopologyPick((BoxBoundary First,BoxBoundary? Second)? Box,ExactTopologySelection? Exact);

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
    private bool cubeClick;
    private ViewerPresentation? preview;
    private Shape? previewShape;
    private ViewerPresentation? workGrid;
    private Shape? workGridShape;
    private readonly List<(Shape Shape,ViewerColor Color)> originShapes=[];
    private readonly List<ViewerPresentation> originPresentations=[];
    private ViewerPresentation? constructionGhost;
    private Shape? constructionShape;
    private bool constructing;
    private CadDisplayMode mode;
    private bool disposed;
    public ViewportCapabilities Capabilities {get;}=new(false,true,true);
    public event EventHandler<IReadOnlyList<SceneItem>>? SelectionChanged;
    public event EventHandler<ViewerCubeOrientation>? ViewCubeOrientationRequested;
    public event EventHandler<ViewerCubeTurn>? ViewCubeTurnRequested;
    public event EventHandler<(BoxBoundary First,BoxBoundary? Second)?>? BoxSubshapeSelected;
    public event EventHandler<LocalTopologyPick>? LocalTopologySelected;
    public event EventHandler<(int X,int Y,bool Click)>? ConstructionPointer;
    public event EventHandler? NavigationStarted;
    public void SetConstructionMode(bool enabled){constructing=enabled;if(!enabled)SetConstructionGhost(null);viewer.ClearSelection();}
    public bool TryWorkplanePoint(int x,int y,double spacing,bool snap,out Vector3d point) =>
        TryWorkplanePoint(x,y,spacing,snap,new DocumentWorkPlaneSettings(),out point);
    public bool TryWorkplanePoint(int x,int y,double spacing,bool snap,DocumentWorkPlaneSettings plane,out Vector3d point)
    {
        ArgumentNullException.ThrowIfNull(plane);plane.Validate();
        if(snap&&(!double.IsFinite(spacing)||spacing<=0))throw new ArgumentOutOfRangeException(nameof(spacing));
        var ray=viewer.GetPickRay(x,y);point=default;
        var normal=plane.Normal;
        var direction=new Vector3d(ray.Direction.X,ray.Direction.Y,ray.Direction.Z);
        var origin=new Vector3d(ray.Origin.X,ray.Origin.Y,ray.Origin.Z);
        double denominator=normal.Dot(direction);
        if(Math.Abs(denominator)<1e-8)return false;
        double t=normal.Dot(plane.Origin-origin)/denominator;
        if(!double.IsFinite(t)||t<0)return false;
        var local=plane.ToLocal(origin+direction*t);
        double u=local.X,v=local.Y;
        if(snap){u=Math.Round(u/spacing)*spacing;v=Math.Round(v/spacing)*spacing;}
        if(!double.IsFinite(u)||!double.IsFinite(v))return false;
        point=plane.ToWorld(u,v);return true;
    }
    public double PixelsPerMillimeter(Vector3d at)=>PixelsPerMillimeter(at,new DocumentWorkPlaneSettings());
    public double PixelsPerMillimeter(Vector3d at,DocumentWorkPlaneSettings plane)
    {
        var a=WorldToScreen(at);var u=plane.Rotation.Rotate(new Vector3d(10,0,0));var v=plane.Rotation.Rotate(new Vector3d(0,10,0));
        var x=WorldToScreen(at+u);var y=WorldToScreen(at+v);
        return Math.Max(0.01,Math.Max(Math.Sqrt(Math.Pow(x.X-a.X,2)+Math.Pow(x.Y-a.Y,2)),Math.Sqrt(Math.Pow(y.X-a.X,2)+Math.Pow(y.Y-a.Y,2)))/10);
    }
    public void SetBackgroundGradient(uint topArgb,uint bottomArgb)
    {
        if((topArgb&0xFF000000u)!=0xFF000000u)throw new ArgumentOutOfRangeException(nameof(topArgb));
        if((bottomArgb&0xFF000000u)!=0xFF000000u)throw new ArgumentOutOfRangeException(nameof(bottomArgb));
        viewer.SetBackgroundGradient(ToViewerColor(topArgb),ToViewerColor(bottomArgb));
        viewer.Redraw();
    }
    private static ViewerColor ToViewerColor(uint argb)=>new(
        ToLinear((byte)(argb>>16)),ToLinear((byte)(argb>>8)),ToLinear((byte)argb));
    private static double ToLinear(byte channel)
    {
        double srgb=channel/255.0;
        return srgb<=0.04045?srgb/12.92:Math.Pow((srgb+0.055)/1.055,2.4);
    }
    public void SetWorkGrid(bool visible,double spacing)=>SetWorkGrid(visible,spacing,new DocumentWorkPlaneSettings());
    public void SetWorkGrid(bool visible,double spacing,DocumentWorkPlaneSettings plane)
    {
        ArgumentNullException.ThrowIfNull(plane);plane.Validate();
        workGrid?.Dispose();workGrid=null;workGridShape?.Dispose();workGridShape=null;
        if(!visible)return;
        if(!double.IsFinite(spacing)||spacing<0.1||spacing>1000)throw new ArgumentOutOfRangeException(nameof(spacing));
        var lines=new List<Shape>();
        try
        {
            for(int i=-20;i<=20;i++)
            {
                double p=i*spacing,extent=20*spacing;
                var a=plane.ToWorld(-extent,p);var b=plane.ToWorld(extent,p);
                var c=plane.ToWorld(p,-extent);var d=plane.ToWorld(p,extent);
                lines.Add(ShapeFactory.CreateEdge(new(a.X,a.Y,a.Z),new(b.X,b.Y,b.Z)));
                lines.Add(ShapeFactory.CreateEdge(new(c.X,c.Y,c.Z),new(d.X,d.Y,d.Z)));
            }
            workGridShape=ShapeFactory.CreateCompound(lines);
            ShowWorkGrid();
        }
        catch{workGrid?.Dispose();workGrid=null;workGridShape?.Dispose();workGridShape=null;throw;}
        finally{foreach(var line in lines)line.Dispose();}
    }
    private void ShowWorkGrid()
    {
        if(workGridShape is null)return;
        workGrid=viewer.Display(workGridShape);
        workGrid.SetColor(new(0.22,0.31,0.40));workGrid.SetDisplayMode(ViewerDisplayMode.Wireframe);
        // Grid topology contains edges only, so face-only selection keeps it out of model picking.
        workGrid.SetSelectionKind(ShapeKind.Face);
        viewer.Redraw();
    }
    public void SetOriginAxes(DocumentOriginSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);settings.Validate();
        ClearOriginAxes();
        if(!settings.Visible){viewer.Redraw();return;}
        double size=settings.SizeMm,wing=size*0.12;
        try
        {
            for(int axis=0;axis<3;axis++)
            {
                var edges=new List<Shape>();
                try
                {
                    Shape Edge(Vector3d a,Vector3d b)=>ShapeFactory.CreateEdge(
                        new(a.X,a.Y,a.Z),new(b.X,b.Y,b.Z));
                    Vector3d Along(double value)=>axis switch
                    {
                        0=>new(value,0,0),1=>new(0,value,0),_=>new(0,0,value)
                    };
                    Vector3d Wing(double value)=>axis switch
                    {
                        0=>new(0,value,0),1=>new(0,0,value),_=>new(value,0,0)
                    };
                    if(settings.Style==DocumentOriginStyle.OriginMarker)
                        edges.Add(Edge(Along(-size/2),Along(size/2)));
                    else
                    {
                        edges.Add(Edge(Along(-size/2),Along(size)));
                        edges.Add(Edge(Along(size),Along(size-wing)+Wing(wing*0.55)));
                        edges.Add(Edge(Along(size),Along(size-wing)-Wing(wing*0.55)));
                    }
                    var shape=ShapeFactory.CreateCompound(edges);
                    var color=settings.Style switch
                    {
                        DocumentOriginStyle.ColorAxes=>axis switch
                        {
                            0=>new ViewerColor(0.95,0.18,0.16),
                            1=>new ViewerColor(0.17,0.75,0.25),
                            _=>new ViewerColor(0.16,0.42,0.96)
                        },
                        DocumentOriginStyle.SubtleAxes=>new ViewerColor(0.68,0.75,0.82),
                        _=>new ViewerColor(0.90,0.72,0.22)
                    };
                    originShapes.Add((shape,color));
                }
                finally{foreach(var edge in edges)edge.Dispose();}
            }
            ShowOriginAxes();
        }
        catch{ClearOriginAxes();throw;}
    }
    private void ShowOriginAxes()
    {
        foreach(var (shape,color) in originShapes)
        {
            var presentation=viewer.Display(shape);
            originPresentations.Add(presentation);
            presentation.SetColor(color);presentation.SetDisplayMode(ViewerDisplayMode.Wireframe);
            // Axis shapes contain edges only; face selection excludes them from model picking.
            presentation.SetSelectionKind(ShapeKind.Face);
        }
        viewer.Redraw();
    }
    private void ClearOriginAxes()
    {
        foreach(var presentation in originPresentations)presentation.Dispose();originPresentations.Clear();
        foreach(var (shape,_) in originShapes)shape.Dispose();originShapes.Clear();
    }
    public void SetConstructionGhost(GeometryRecipe? recipe)
    {
        constructionGhost?.Dispose();constructionGhost=null;constructionShape?.Dispose();constructionShape=null;
        if(recipe is null){viewer.Redraw();return;}
        recipe.Validate();
        constructionShape=recipe switch
        {
            BoxRecipe b=>ShapeFactory.CreateBox(b.X,b.Y,b.Z),
            CylinderRecipe c=>ShapeFactory.CreateCylinder(c.Radius,c.Height),
            ExtrudeRecipe e=>CreateProfileGhost(e.Profile,e.Distance,null),
            RevolveRecipe r=>CreateProfileGhost(r.Profile,null,r.AngleRadians),
            _=>throw new NotSupportedException("Only viewport construction recipes are supported.")
        };
        try
        {
            constructionGhost=viewer.Display(constructionShape);
            var placement=recipe switch
            {
                BoxRecipe b=>b.Placement,CylinderRecipe c=>c.Placement,
                ExtrudeRecipe e=>e.Placement,RevolveRecipe r=>r.Placement,
                _=>throw new NotSupportedException()
            };
            using var transform=OcctGeometryBridge.ToNative(placement);
            constructionGhost.SetTransform(transform);constructionGhost.SetDisplayMode(ViewerDisplayMode.Shaded);
            constructionGhost.SetColor(new(0.1,0.8,0.55));constructionGhost.SetTransparency(0.65);
            constructionGhost.SetSelectionKind(ShapeKind.Edge);
            viewer.Redraw();
        }
        catch{constructionGhost?.Dispose();constructionGhost=null;constructionShape.Dispose();constructionShape=null;throw;}
    }
    private static Shape CreateProfileGhost(SketchProfile profile,double? distance,double? angle)
    {
        using var wire=ShapeFactory.CreatePolygonWire(profile.Points.Select(p=>
            angle is null?new GpPoint(p.X,p.Y,0):new GpPoint(p.X,0,p.Y)).ToArray(),true);
        using var face=ShapeFactory.CreatePlanarFace(wire);
        if(distance is {} length)
        {
            using var direction=GpVec.Create(0,0,length);
            return face.Extrude(direction);
        }
        using var axis=GpAx1.Create(0,0,0,0,0,1);
        return face.Revolve(axis,angle!.Value);
    }
    private BoxRecipe? selectionBox;
    private TopologyKind? selectionKind;
    private (DocumentId Document,FeatureId Feature)? exactSelectionSource;
    public void SetExactSelectionSource(DocumentId document,FeatureId feature)=>exactSelectionSource=(document,feature);
    public void ClearExactSelectionSource()=>exactSelectionSource=null;
    public void SetBoxSelection(BoxRecipe? box,TopologyKind? kind)
    {
        selectionBox=box;selectionKind=kind;viewer.ClearSelection();
        foreach(var entry in entries.Values)entry.Presentation.SetSelectionKind(kind is null?null:kind==TopologyKind.Face?ShapeKind.Face:ShapeKind.Edge);
    }
    public void HighlightBoxSelection(TopologyReference? reference)=>HighlightBoxSelections(reference is null?[]:[reference]);
    public void HighlightBoxSelections(IEnumerable<TopologyReference> references)
    {
        var selected=references.ToArray();
        foreach(var entry in entries.Values)
        {
            entry.Presentation.ClearAllSubshapeOverrides();
            if(selected.Length==0||selectionBox is not {} box)continue;
            using var topology=entry.Geometry.Shape.GetTopologyAdjacency(ShapeKind.Edge,ShapeKind.Face);
            foreach(var reference in selected)
            {
                var parts=reference.Kind==TopologyKind.Face?topology.Ancestors:topology.Items;
                var candidates=parts.Where(s=>BoxTopology.Matches(s,box,reference.Kind,reference.Boundary,reference.SecondBoundary)).ToArray();
                if(candidates.Length==1)
                {
                    entry.Presentation.SetSubshapeColor(candidates[0],new(1,0.65,0));
                    if(reference.Kind==TopologyKind.Edge)entry.Presentation.SetSubshapeWidth(candidates[0],4);
                }
            }
        }
        viewer.Redraw();
    }
    public void HighlightExactSelections(IEnumerable<ExactTopologySelection> selections)
    {
        foreach(var entry in entries.Values)
        {
            var chosen=selections.Where(s=>s.Revision==entry.Item.Geometry.Revision&&s.Asset==entry.Item.Geometry.AssetId).ToArray();
            if(chosen.Length==0)continue;
            using var map=RepairSnapshot.Create(entry.Geometry.Shape);
            foreach(var exact in chosen)
            {
                if(map.Fingerprint!=exact.Fingerprint||exact.FullTopologyIndex>=map.Topology.Count)continue;
                var parts=entry.Geometry.Shape.GetSubShapes(exact.Kind==HistoryShapeKind.Edge?ShapeKind.Edge:ShapeKind.Face);
                try
                {
                    foreach(var part in parts.Where(part=>RepairSnapshot.FindTopologyIndex(entry.Geometry.Shape,part)==exact.FullTopologyIndex))
                    {
                        entry.Presentation.SetSubshapeColor(part,new(1,0.65,0));
                        if(exact.Kind==HistoryShapeKind.Edge)entry.Presentation.SetSubshapeWidth(part,4);
                    }
                }
                finally{foreach(var part in parts)part.Dispose();}
            }
        }
        viewer.Redraw();
    }
    public OcctViewport(nint windowHandle,IAssetStore assets)
    {
        this.assets=assets;viewer=OcctViewer.Create(windowHandle);
        try
        {
            SetBackgroundGradient(DocumentSettings.DefaultBackgroundTopArgb,DocumentSettings.DefaultBackgroundBottomArgb);
            viewer.SetProjection(ViewerProjection.Axonometric);
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
    public void FitAll()
    {
        workGrid?.Dispose();workGrid=null;
        foreach(var presentation in originPresentations)presentation.Dispose();originPresentations.Clear();
        constructionGhost?.Dispose();constructionGhost=null;
        viewer.FitAll();ShowWorkGrid();ShowOriginAxes();
    }
    public void SetProjection(CadProjection projection)=>viewer.SetProjection(ToViewerProjection(projection));
    public CadCamera CaptureProjectionTarget(CadProjection projection)
    {
        var current=CaptureCamera();
        var target=viewer.GetProjectionTargetCamera(ToViewerProjection(projection));
        return current with {Eye=new(target.Eye.X,target.Eye.Y,target.Eye.Z),
            Target=new(target.Target.X,target.Target.Y,target.Target.Z),Up=new(target.Up.X,target.Up.Y,target.Up.Z)};
    }
    public CadCamera CaptureCubeTarget(ViewerCubeOrientation orientation)
    {
        var current=CaptureCamera();
        var target=viewer.GetCubeTargetCamera(orientation);
        return current with {Eye=new(target.Eye.X,target.Eye.Y,target.Eye.Z),
            Target=new(target.Target.X,target.Target.Y,target.Target.Z),Up=new(target.Up.X,target.Up.Y,target.Up.Z)};
    }
    public void SetViewCubeVisible(bool visible)=>viewer.SetViewCubeVisible(visible);
    public ViewerCubeOrientation? HitViewCube(int x,int y)=>viewer.HitViewCube(x,y);
    public ViewerCubeHit? HitViewCubeControl(int x,int y)=>viewer.HitViewCubeControl(x,y);
    public CadCamera CaptureCubeTurnTarget(ViewerCubeTurn turn,int degrees)
    {
        if(turn is not (ViewerCubeTurn.Left or ViewerCubeTurn.Right))throw new ArgumentOutOfRangeException(nameof(turn));
        if(degrees is < 1 or > 180)throw new ArgumentOutOfRangeException(nameof(degrees));
        var current=CaptureCamera();
        var target=viewer.GetRolledCamera(turn==ViewerCubeTurn.Left?-degrees:degrees);
        return current with {Up=new(target.Up.X,target.Up.Y,target.Up.Z)};
    }
    private static ViewerProjection ToViewerProjection(CadProjection projection)=>projection switch
    {
        CadProjection.Front=>ViewerProjection.Front,CadProjection.Top=>ViewerProjection.Top,
        CadProjection.Right=>ViewerProjection.Right,CadProjection.Left=>ViewerProjection.Left,
        CadProjection.Back=>ViewerProjection.Back,CadProjection.Bottom=>ViewerProjection.Bottom,_=>ViewerProjection.Axonometric
    };
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
        if(cubeClick){viewer.MoveTo(x,y);return;}
        if(constructing&&(buttons&6)==0){ConstructionPointer?.Invoke(this,(x,y,false));return;}
        if(Math.Abs(x-pressX)>2||Math.Abs(y-pressY)>2)selectionClick=false;
        viewer.Input.PointerMoved(x,y,(ViewerPointerButtons)(buttons&pressedButtons),(ViewerModifierKeys)modifiers);
    }
    public void PointerPressed(int button,int x,int y,int modifiers)
    {
        NavigationStarted?.Invoke(this,EventArgs.Empty);
        lastX=x;lastY=y;pressedButtons|=1<<button;
        if(button==0&&viewer.HitViewCubeControl(x,y) is not null)
        {cubeClick=true;pressX=x;pressY=y;selectionClick=true;return;}
        if(constructing&&button==0)return;
        pressX=x;pressY=y;selectionClick=button==0;
        viewer.Input.PointerPressed(Button(button),x,y,(ViewerModifierKeys)modifiers);
    }
    public void PointerReleased(int button,int x,int y,int modifiers)
    {
        if((pressedButtons&(1<<button))==0)return; // Late release after capture loss must not clear model selection.
        pressedButtons&=~(1<<button);lastX=x;lastY=y;
        if(button==0&&cubeClick)
        {
            cubeClick=false;
            if(selectionClick&&Math.Abs(x-pressX)<=2&&Math.Abs(y-pressY)<=2&&viewer.HitViewCubeControl(x,y) is {} cubeHit)
            {
                if(cubeHit.Orientation is {} orientation)ViewCubeOrientationRequested?.Invoke(this,orientation);
                else if(cubeHit.Turn!=ViewerCubeTurn.None)ViewCubeTurnRequested?.Invoke(this,cubeHit.Turn);
            }
            selectionClick=false;return;
        }
        if(constructing&&button==0){ConstructionPointer?.Invoke(this,(x,y,true));return;}
        var selected=viewer.Input.PointerReleased(Button(button),x,y);
        bool clicked=button==0&&selectionClick;selectionClick=false;
        if(!clicked)return;
        if(selectionKind is {} kind&&(selectionBox is not null||exactSelectionSource is not null))
        {
            var pickedItems=viewer.GetSelectedItems();
            try
            {
                (BoxBoundary First,BoxBoundary? Second)? value=null;
                if(pickedItems.Count==1)
                {
                    if(selectionBox is {} box)
                    {
                        var candidates=BoxTopology.Classify(pickedItems[0].Shape,box,kind);
                        if(candidates.Count==1)value=candidates[0];
                    }
                }
                ExactTopologySelection? exact=null;
                if(exactSelectionSource is {} owner&&pickedItems.Count==1&&entries.Count==1)
                {
                    var entry=entries.Values.Single();
                    using var map=RepairSnapshot.Create(entry.Geometry.Shape);
                    int index=RepairSnapshot.FindTopologyIndex(entry.Geometry.Shape,pickedItems[0].Shape);
                    if(index>=0&&index<map.Topology.Count&&map.Topology[index].Kind==pickedItems[0].Shape.Kind)
                        exact=new(owner.Document,owner.Feature,entry.Item.Geometry.Revision,entry.Item.Geometry.AssetId,
                            map.Fingerprint,index,kind==TopologyKind.Edge?HistoryShapeKind.Edge:HistoryShapeKind.Face,
                            OcctGeometryKernel.HistoryAdapterVersion);
                }
                if(exactSelectionSource is not null)LocalTopologySelected?.Invoke(this,new(value,exact));
                else BoxSubshapeSelected?.Invoke(this,value);
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
        pressedButtons=0;selectionClick=false;cubeClick=false;
        // preview.26 clears any pressed button on release. Right avoids synthesizing a left click.
        viewer.Input.PointerReleased(ViewerPointerButton.Right,lastX,lastY);
    }
    public void ClearSelection()=>viewer.ClearSelection();
    public void MouseWheel(int delta,int x,int y,int modifiers)
    {
        NavigationStarted?.Invoke(this,EventArgs.Empty);
        viewer.Input.MouseWheel(delta,x,y,(ViewerModifierKeys)modifiers);
    }
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
        preview?.Dispose();previewShape?.Dispose();constructionGhost?.Dispose();constructionShape?.Dispose();workGrid?.Dispose();workGridShape?.Dispose();ClearOriginAxes();
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
