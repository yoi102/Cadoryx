using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot = Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

/// <summary>Serializes native modeling/exchange; no Viewer or UI object enters this queue.</summary>
public sealed partial class OcctGeometryKernel : IGeometryKernel, ITopologyResolver
{
    private static readonly SemaphoreSlim Queue=new(1,1);
    public string Version=>"OcctSharp 8.0.1-preview.28.cadoryx.h2b2.2 / OCCT 8.0.1";
    public bool Supports(GeometryRecipe recipe)=>recipe is BoxRecipe or CylinderRecipe or ImportedRecipe or BooleanRecipe or TransformRecipe or ExtrudeRecipe or RevolveRecipe or LocalFeatureRecipe;
    private static async Task<T> Run<T>(Func<T> action,CancellationToken token)
    {
        await Queue.WaitAsync(token).ConfigureAwait(false);
        try{return await Task.Run(()=>{token.ThrowIfCancellationRequested();_ = OcctRuntime.Info;return action();},token).ConfigureAwait(false);}
        finally{Queue.Release();}
    }
    public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken cancellationToken=default)
    {
        recipe.Validate();
        return Run(()=>
        {
            if(recipe is LocalFeatureRecipe local)
            {
                return EvaluateLocal(local,assets,cancellationToken);
            }
            if(recipe is BooleanRecipe boolean)
            {
                if(boolean.Inputs.Length<=256&&boolean.Inputs.All(i=>i.Kind!=BodyKind.Empty))
                    return EvaluateBoolean(boolean,assets,cancellationToken);
                var shapes=new List<Shape>();
                try
                {
                    foreach(var input in boolean.Inputs)shapes.Add(OcctGeometryBridge.ReadShape(input,assets));
                    using var operation=FeatureModeling.Boolean((FeatureBooleanOperation)boolean.Operation,[shapes[0]],shapes.Skip(1).ToArray(),
                        new FeatureModelingOptions{NonDestructive=true,RunParallel=false});
                    cancellationToken.ThrowIfCancellationRequested();
                    return OcctGeometryBridge.StoreShape(operation.RequireShape(),assets);
                }
                finally{foreach(var shape in shapes)shape.Dispose();}
            }
            using var result=Create(recipe,assets);
            cancellationToken.ThrowIfCancellationRequested();
            return OcctGeometryBridge.StoreShape(result,assets);
        },cancellationToken);
    }
    private static Shape Create(GeometryRecipe recipe,IAssetStore assets)
    {
        static Shape Place(Shape source,RigidTransform3d placement)
        {
            using(source)using(var transform=OcctGeometryBridge.ToNative(placement))return source.Transformed(transform);
        }
        static Shape Profile(SketchProfile profile,bool radial)
        {
            using var wire=ShapeFactory.CreatePolygonWire(profile.Points.Select(p=>radial?new GpPoint(p.X,0,p.Y):new GpPoint(p.X,p.Y,0)).ToArray(),true);
            return ShapeFactory.CreatePlanarFace(wire);
        }
        switch(recipe)
        {
            case BoxRecipe b:return Place(ShapeFactory.CreateBox(b.X,b.Y,b.Z),b.Placement);
            case CylinderRecipe c:return Place(ShapeFactory.CreateCylinder(c.Radius,c.Height),c.Placement);
            case ImportedRecipe i:return OcctGeometryBridge.ReadShape(i.Source,assets);
            case TransformRecipe t:return Place(OcctGeometryBridge.ReadShape(t.Source,assets),t.Transform);
            case ExtrudeRecipe e:
                using(var face=Profile(e.Profile,false))using(var direction=GpVec.Create(0,0,e.Distance))
                    return Place(face.Extrude(direction),e.Placement);
            case RevolveRecipe r:
                using(var face=Profile(r.Profile,true))using(var axis=GpAx1.Create(0,0,0,0,0,1))
                    return Place(face.Revolve(axis,r.AngleRadians),r.Placement);
            default:throw new NotSupportedException("Unsupported geometry recipe.");
        }
    }
    public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken cancellationToken=default)=>Run(()=>
    {
        using var native=XdeDocument.ReadExchange(Path.GetFullPath(path));using var files=new KernelFiles();
        var contextPath=files.PathFor("import.xbf");native.Save(contextPath);
        var leases=new List<IAssetLease>();var results=new List<GeometryResult>();
        try
        {
            var context=assets.Stage(File.ReadAllBytes(contextPath));leases.Add(context);
            var doc=DocumentSnapshot.Create(Path.GetFileNameWithoutExtension(path));
            var definitions=doc.Definitions.ToBuilder();var bodies=doc.Bodies.ToBuilder();var features=doc.Features.ToBuilder();
            var mapped=new Dictionary<string,DefinitionId>(StringComparer.Ordinal);var active=new HashSet<string>();
            DefinitionId ReadDefinition(XdeLabel label,int depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(depth>128||definitions.Count>100000)throw new CadValidationException("Import graph exceeds limits.");
                if(active.Contains(label.Entry))throw new CadValidationException("Cyclic source assembly.");
                if(mapped.TryGetValue(label.Entry,out var known))return known;
                var id=DefinitionId.New();mapped.Add(label.Entry,id);active.Add(label.Entry);
                string name=string.IsNullOrWhiteSpace(label.Name)?"Unnamed":label.Name;
                if(label.IsAssembly)
                {
                    var children=ImmutableArray.CreateBuilder<ComponentSlot>();
                    foreach(var component in label.GetComponents())
                    {
                        using var location=component.Location;using var transform=location.ToTransform();
                        var child=ReadDefinition(component.ReferredShape,depth+1);
                        children.Add(new(ComponentSlotId.New(),child,string.IsNullOrWhiteSpace(component.Name)?definitions[child].Name:component.Name,
                            OcctGeometryBridge.FromNative(transform),true,component.Color is {} color?new CadAppearance(OcctGeometryBridge.ToArgb(color)):null));
                    }
                    definitions.Add(id,new AssemblyDefinition(id,name,children.ToImmutable()));
                }
                else
                {
                    using var original=label.Shape;using var identity=TopLocLocation.Identity;using var local=original.Located(identity);
                    var stored=OcctGeometryBridge.StoreShape(local,assets);results.Add(stored);
                    var geometry=stored.Geometry with {Source=new(context.Id,label.Entry,AssetFormatPolicy.CurrentXde)};
                    var bodyId=BodyId.New();var featureId=FeatureId.New();
                    var color=OverallColor(label);
                    var body=new CadBody(bodyId,id,name,geometry,featureId,doc.Layers.Keys.First(),new(color is {} c?OcctGeometryBridge.ToArgb(c):0xFF86ACC5,PreserveSourceStyles:true));
                    bodies.Add(bodyId,body);
                    features.Add(featureId,new(featureId,id,"Import",new ImportedRecipe(geometry),[],bodyId,geometry));
                    definitions.Add(id,new PartDefinition(id,name,[bodyId],[featureId]));
                }
                active.Remove(label.Entry);return id;
            }
            var roots=ImmutableArray.CreateBuilder<ComponentSlot>();
            foreach(var label in native.GetFreeShapes())
            {
                var id=ReadDefinition(label,0);using var location=label.Location;using var transform=location.ToTransform();
                roots.Add(new(ComponentSlotId.New(),id,definitions[id].Name,OcctGeometryBridge.FromNative(transform)));
            }
            if(roots.Count==0)throw new CadValidationException("The exchange file contains no transferable model.");
            definitions[doc.RootAssemblyId]=new AssemblyDefinition(doc.RootAssemblyId,doc.Name,roots.ToImmutable());
            doc=doc with {Definitions=definitions.ToImmutable(),Bodies=bodies.ToImmutable(),Features=features.ToImmutable()};
            doc.Validate();
            foreach(var id in doc.ReferencedAssets())leases.Add(assets.Acquire(id));
            var loaded=new LoadedDocument(doc,leases,[new("IMPORT.UNITS","OCCT exchange uses its transferred millimeter geometry; no second unit scale is applied."),
                new("IMPORT.METADATA","XDE source context is embedded for supported display styles. CAD business projection currently includes definitions, placements, names and overall colors.")]);
            leases.Clear();return loaded;
        }
        finally{foreach(var result in results)result.Dispose();foreach(var lease in leases)lease.Dispose();}
    },cancellationToken);
    private static XdeColor? OverallColor(XdeLabel label)
    {
        if((label.Color??label.VisualMaterial?.BaseColor) is {} direct)return direct;
        // IGES commonly attaches color to every face instead of the enclosing group.
        var styles=label.GetPresentationStyles();
        try
        {
            var colors=styles.Select(s=>s.EffectiveColor).Distinct().ToArray();
            return colors.Length==1?colors[0]:null;
        }
        finally{foreach(var style in styles)style.Dispose();}
    }
    public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken cancellationToken=default)=>ExportAsync(snapshot,assets,path,new CadExportOptions(),cancellationToken);
    public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken cancellationToken=default)=>Run(()=>
    {
        snapshot.Validate();options.Validate();string extension=Path.GetExtension(path).ToLowerInvariant();
        if(extension is not (".step" or ".stp" or ".iges" or ".igs" or ".stl"))throw new NotSupportedException("Export supports STEP, IGES and STL.");
        if(extension==".stl")return ExportStl(snapshot,assets,path,options,cancellationToken);
        using var native=XdeDocument.Create();var labels=new Dictionary<DefinitionId,XdeLabel>();
        bool IncludeBody(BodyId id)
        {
            var b=snapshot.Bodies[id];return b.Geometry.Kind!=BodyKind.Empty&&(!options.VisibleOnly||(b.IsVisible&&snapshot.Layers[b.LayerId].IsVisible));
        }
        var included=new Dictionary<DefinitionId,bool>();
        bool IncludeDefinition(DefinitionId id)
        {
            if(included.TryGetValue(id,out bool cached))return cached;
            bool include=snapshot.Definitions[id] switch
            {
                PartDefinition part=>part.Bodies.Any(IncludeBody),
                AssemblyDefinition assembly=>assembly.Children.Any(s=>(!options.VisibleOnly||s.IsVisible)&&IncludeDefinition(s.DefinitionId)),
                _=>false
            };
            included[id]=include;return include;
        }
        if(!IncludeDefinition(snapshot.RootAssemblyId))throw new CadValidationException("There is no geometry to export.");
        XdeLabel Define(DefinitionId id)
        {
            cancellationToken.ThrowIfCancellationRequested();if(labels.TryGetValue(id,out var existing))return existing;
            var definition=snapshot.Definitions[id];XdeLabel label;
            var bodyIds=definition is PartDefinition p?p.Bodies.Where(IncludeBody).ToArray():[];
            if(definition is PartDefinition && bodyIds.Length==1)
            {
                var body=snapshot.Bodies[bodyIds[0]];using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);
                label=native.AddShape(shape,definition.Name);label.Color=OcctGeometryBridge.ToXdeColor(body.Appearance.ByLayer?snapshot.Layers[body.LayerId].Argb:body.Appearance.Argb);
            }
            else
            {
                label=native.AddAssembly(definition.Name);labels.Add(id,label);
                if(definition is AssemblyDefinition a)
                    foreach(var slot in a.Children)
                    {
                        if(options.VisibleOnly&&!slot.IsVisible||!IncludeDefinition(slot.DefinitionId))continue;
                        using var t=OcctGeometryBridge.ToNative(slot.LocalTransform);using var location=TopLocLocation.FromTransform(t);
                        var occurrence=native.AddComponent(label,Define(slot.DefinitionId),location);occurrence.Name=slot.Name;
                        if(slot.AppearanceOverride is {} c)occurrence.Color=OcctGeometryBridge.ToXdeColor(c.Argb);
                    }
                else if(definition is PartDefinition part)
                    foreach(var bodyId in bodyIds)
                    {
                        var body=snapshot.Bodies[bodyId];using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);
                        var child=native.AddShape(shape,body.Name);child.Color=OcctGeometryBridge.ToXdeColor(body.Appearance.ByLayer?snapshot.Layers[body.LayerId].Argb:body.Appearance.Argb);
                        using var identity=TopLocLocation.Identity;native.AddComponent(label,child,identity);
                    }
                return label;
            }
            labels.Add(id,label);return label;
        }
        using(var transaction=native.BeginTransaction("Export Cadoryx assembly"))
        {
            Define(snapshot.RootAssemblyId);transaction.Commit();
        }
        string fullPath=Path.GetFullPath(path);string? directory=Path.GetDirectoryName(fullPath);Directory.CreateDirectory(directory!);
        string temp=Path.Combine(directory!,"."+Guid.NewGuid().ToString("N")+Path.GetExtension(path));
        try
        {
            if(Path.GetExtension(path).ToLowerInvariant() is ".igs" or ".iges")native.WriteIges(temp);else native.WriteStep(temp);
            cancellationToken.ThrowIfCancellationRequested();File.Move(temp,fullPath,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
        var diagnostics=ImmutableArray.CreateBuilder<CadDiagnostic>();
        diagnostics.Add(new("EXPORT.METADATA","Exports definitions, placements, names and overall colors. Cadoryx feature history and unsupported source PMI/subshape styles are not transferred."));
        bool iges=extension is ".igs" or ".iges";
        if(iges)diagnostics.Add(new("EXPORT.IGES_SURFACES","The current IGES writer transfers surfaces; a closed solid may reopen as faces without solid volume, and assembly sharing may be flattened. Use STEP or Cadoryx when those semantics must be retained."));
        return new CadExportReport(fullPath,iges?"IGES":"STEP",diagnostics.ToImmutable());
    },cancellationToken);
    private static CadExportReport ExportStl(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken token)
    {
        var shapes=new List<Shape>();string full=Path.GetFullPath(path);string directory=Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);string temp=Path.Combine(directory,"."+Guid.NewGuid().ToString("N")+".stl");
        try
        {
            foreach(var occurrence in snapshot.EnumerateOccurrences())
            {
                token.ThrowIfCancellationRequested();
                if(options.VisibleOnly&&!occurrence.IsVisible||snapshot.Definitions[occurrence.DefinitionId] is not PartDefinition part)continue;
                foreach(var bodyId in part.Bodies)
                {
                    var body=snapshot.Bodies[bodyId];
                    if(body.Geometry.Kind==BodyKind.Empty||options.VisibleOnly&&(!body.IsVisible||!snapshot.Layers[body.LayerId].IsVisible))continue;
                    using var local=OcctGeometryBridge.ReadShape(body.Geometry,assets);using var transform=OcctGeometryBridge.ToNative(occurrence.WorldTransform);
                    shapes.Add(local.Transformed(transform));
                }
            }
            if(shapes.Count==0)throw new CadValidationException("There is no geometry to export.");
            using var compound=ShapeFactory.CreateCompound(shapes);
            ShapeExchange.WriteStl(compound,temp,new StlWriteOptions(options.LinearDeflectionMm,options.AngularDeflectionRad,options.BinaryStl));
            token.ThrowIfCancellationRequested();File.Move(temp,full,true);
            return new(full,"STL",[new("EXPORT.STL_SEMANTICS","STL contains a triangulated surface in millimeter coordinates; it has no intrinsic units, assembly names, colors, constraints or feature history.")]);
        }
        finally{foreach(var shape in shapes)shape.Dispose();if(File.Exists(temp))File.Delete(temp);}
    }
}
