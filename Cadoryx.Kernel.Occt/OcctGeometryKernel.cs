using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot = Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

/// <summary>Serializes native modeling/exchange; no Viewer or UI object enters this queue.</summary>
public sealed partial class OcctGeometryKernel : IGeometryKernel, ITopologyResolver
{
    private static readonly SemaphoreSlim Queue=new(1,1);
    private readonly IMetricThreadExecutor? threadExecutor;
    private readonly INativeDrawingExecutor? drawingExecutor;
    public OcctGeometryKernel():this(null,null){}
    public OcctGeometryKernel(IMetricThreadExecutor? threadExecutor):this(threadExecutor,null){}
    public OcctGeometryKernel(IMetricThreadExecutor? threadExecutor,INativeDrawingExecutor? drawingExecutor)
        :this(threadExecutor,drawingExecutor,GuidedDocumentEvidencePolicy.LegacyMembers){}
    public OcctGeometryKernel(IMetricThreadExecutor? threadExecutor,INativeDrawingExecutor? drawingExecutor,
        GuidedDocumentEvidencePolicy guidedDocumentEvidence)
    {
        if(!Enum.IsDefined(guidedDocumentEvidence))throw new ArgumentOutOfRangeException(nameof(guidedDocumentEvidence));
        this.threadExecutor=threadExecutor;this.drawingExecutor=drawingExecutor;GuidedDocumentEvidence=guidedDocumentEvidence;
    }
    public GuidedDocumentEvidencePolicy GuidedDocumentEvidence {get;}
    public string Version=>"OcctSharp "+System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(OcctGeometryKernel).Assembly)
        .Single(a=>a.Key=="OcctSharpPackageVersion").Value;
    public bool Supports(GeometryRecipe recipe)=>recipe is BoxRecipe or CylinderRecipe or ImportedRecipe or BooleanRecipe or TransformRecipe or RepetitionRecipe or HolePatternRecipe or ExtrusionPatternRecipe or HoleRecipe or ShellRecipe or DraftRecipe or SolidOffsetRecipe or ContourLocalRecipe or PlanarPushPullRecipe or DefeatureRecipe or ExtrudeRecipe or SweepRecipe or LoftRecipe or RevolveRecipe or LocalFeatureRecipe or HistoryFilletRecipe or HistoryChamferRecipe;
    private static async Task<T> Run<T>(Func<T> action,CancellationToken token)
    {
        await Queue.WaitAsync(token).ConfigureAwait(false);
        try{return await Task.Run(()=>{token.ThrowIfCancellationRequested();_ = OcctRuntime.Info;return action();},token).ConfigureAwait(false);}
        finally{Queue.Release();}
    }
    public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken cancellationToken=default)
        =>EvaluateCore(recipe,assets,false,cancellationToken);
    public Task<GeometryResult> EvaluateForDocumentAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken cancellationToken=default)
        =>EvaluateCore(recipe,assets,true,cancellationToken);
    private Task<GeometryResult> EvaluateCore(GeometryRecipe recipe,IAssetStore assets,bool retainReferences,CancellationToken cancellationToken)
    {
        recipe.Validate();
        return Run(()=>
        {
            if(recipe is SweepRecipe or LoftRecipe)
                return retainReferences&&GuidedDocumentEvidence==GuidedDocumentEvidencePolicy.PreferCurrentOccurrences
                    ?EvaluateGuidedWithReport(recipe,assets,cancellationToken,false,preferOccurrences:true)
                    :EvaluateGuidedWithReport(recipe,assets,cancellationToken,retainReferences);
            if(recipe is RepetitionRecipe repetition)return EvaluateRepetition(repetition,assets,cancellationToken);
            if(recipe is ImportedRecipe {Source.Source:not null} imported)return EvaluateStyledImport(imported,assets,cancellationToken);
            if(recipe is HoleRecipe hole)return EvaluateHole(hole,assets,cancellationToken);
            if(recipe is HolePatternRecipe holePattern)return EvaluateHolePattern(holePattern,assets,cancellationToken);
            if(recipe is ExtrusionPatternRecipe extrusionPattern)return EvaluateExtrusionPattern(extrusionPattern,assets,cancellationToken);
            if(recipe is ShellRecipe shell)return EvaluateShell(shell,assets,cancellationToken);
            if(recipe is DraftRecipe draft)return EvaluateDraft(draft,assets,cancellationToken);
            if(recipe is SolidOffsetRecipe offset)return EvaluateSolidOffset(offset,assets,cancellationToken);
            if(recipe is ContourLocalRecipe contour)return EvaluateContourLocal(contour,assets,cancellationToken);
            if(recipe is PlanarPushPullRecipe pushPull)return EvaluatePlanarPushPull(pushPull,assets,cancellationToken);
            if(recipe is DefeatureRecipe defeature)return EvaluateDefeature(defeature,assets,cancellationToken);
            if(recipe is ExtrudeRecipe {Options:not null} extrusion)return EvaluateExtrusion(extrusion,assets,cancellationToken);
            if(recipe is LocalFeatureRecipe local)
            {
                return EvaluateLocal(local,assets,cancellationToken);
            }
            if(recipe is HistoryFilletRecipe bound)
            {
                return EvaluateHistoryFillet(bound,assets,cancellationToken);
            }
            if(recipe is HistoryChamferRecipe chamfer)
            {
                return EvaluateHistoryChamfer(chamfer,assets,cancellationToken);
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
            using var result=Create(recipe,assets,cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if(recipe is RevolveRecipe&&(!result.GetValidationReport(geometryChecks:true,exact:true).IsValid||
                !result.GetSelfInterferenceReport().IsClear))
                throw new CadValidationException("Revolution produced invalid or self-interfering exact geometry.");
            bool adaptiveVolume=recipe switch
            {
                ExtrudeRecipe prism=>prism.Profile.RequiresExactRegionInspection,
                RevolveRecipe revolution=>revolution.Profile.RequiresExactRegionInspection,
                _=>false
            };
            var stored=OcctGeometryBridge.StoreShape(result,assets,adaptiveVolume:adaptiveVolume);
            if(recipe is RevolveRecipe&&stored.Geometry.Kind is not (BodyKind.Solid or BodyKind.Compound))
            {
                stored.Dispose();throw new CadValidationException("Revolution must produce valid solid material regions.");
            }
            return stored;
        },cancellationToken);
    }
    private static Shape Create(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default,bool canonicalExpressions=false)
    {
        static Shape Place(Shape source,RigidTransform3d placement)
        {
            using(source)using(var transform=OcctGeometryBridge.ToNative(placement))return source.Transformed(transform);
        }
        switch(recipe)
        {
            case BoxRecipe b:return Place(ShapeFactory.CreateBox(b.X,b.Y,b.Z),b.Placement);
            case CylinderRecipe c:return Place(ShapeFactory.CreateCylinder(c.Radius,c.Height),c.Placement);
            case ImportedRecipe i:return OcctGeometryBridge.ReadShape(i.Source,assets);
            case TransformRecipe t:return Place(OcctGeometryBridge.ReadShape(t.Source,assets),t.Transform);
            case SweepRecipe sweep:return BuildSweep(sweep,token);
            case LoftRecipe loft:return BuildLoft(loft,token);
            case ExtrudeRecipe e:
                if(e.Profile.RequiresExactRegionInspection)
                    ValidatePlanarRegion(OuterBoundary(e.Profile),HoleBoundaries(e.Profile).ToArray(),
                        e.Profile.Islands.Select(i=>OuterBoundary(i.Profile)).ToArray(),token);
                using(var face=OcctSketchProfileGeometry.CreateFace(e.Profile,false,canonicalExpressions))using(var direction=GpVec.Create(0,0,e.Distance))
                {
                    Shape current=face.Extrude(direction);
                    bool currentOwnedByPieces=false;
                    try
                    {
                        foreach(var hole in e.Profile.Holes.IsDefault?ImmutableArray<CircularSketchRegion>.Empty:e.Profile.Holes)
                        {
                            double margin=Math.Max(1e-5,Math.Min(1,e.Distance*.1));
                            using var edge=ShapeFactory.CreateCircleEdge(new(hole.Center.X,hole.Center.Y,-margin),new(0,0,1),hole.Radius);
                            using var wire=ShapeFactory.CreateWire([edge]);
                            using var holeFace=ShapeFactory.CreatePlanarFace(wire);
                            using var holeDirection=GpVec.Create(0,0,e.Distance+2*margin);
                            using var cutter=holeFace.Extrude(holeDirection);
                            var next=current.Cut(cutter);current.Dispose();current=next;
                        }
                        foreach(var hole in e.Profile.PolygonHoles)
                        {
                            double margin=Math.Max(1e-5,Math.Min(1,e.Distance*.1));
                            using var wire=ShapeFactory.CreatePolygonWire(hole.Select(p=>new GpPoint(p.X,p.Y,-margin)).ToArray(),true);
                            using var holeFace=ShapeFactory.CreatePlanarFace(wire);
                            using var holeDirection=GpVec.Create(0,0,e.Distance+2*margin);
                            using var cutter=holeFace.Extrude(holeDirection);
                            var next=current.Cut(cutter);current.Dispose();current=next;
                        }
                        foreach(var hole in e.Profile.MixedHoles)
                        {
                            double margin=Math.Max(1e-5,Math.Min(1,e.Distance*.1));
                            using var holeFace=Place(OcctSketchProfileGeometry.CreateFace(new SketchProfile([]){BoundaryCurves=hole},false,canonicalExpressions),
                                RigidTransform3d.Translate(0,0,-margin));
                            using var holeDirection=GpVec.Create(0,0,e.Distance+2*margin);
                            using var cutter=holeFace.Extrude(holeDirection);
                            var next=current.Cut(cutter);current.Dispose();current=next;
                        }
                        foreach(var hole in e.Profile.SplineHoles)
                        {
                            double margin=Math.Max(1e-5,Math.Min(1,e.Distance*.1));
                            using var holeFace=Place(OcctSketchProfileGeometry.CreateFace(new SketchProfile([]){Spline=hole},false),
                                RigidTransform3d.Translate(0,0,-margin));
                            using var holeDirection=GpVec.Create(0,0,e.Distance+2*margin);
                            using var cutter=holeFace.Extrude(holeDirection);
                            var next=current.Cut(cutter);current.Dispose();current=next;
                        }
                        if(e.Profile.Islands.IsEmpty){currentOwnedByPieces=true;return Place(current,e.Placement);}
                        var pieces=new List<Shape>{current};
                        currentOwnedByPieces=true;
                        try
                        {
                            foreach(var island in e.Profile.Islands)
                            {
                                pieces.Add(Create(new ExtrudeRecipe(island.Profile,e.Distance,RigidTransform3d.Identity),assets,token,canonicalExpressions));
                            }
                            using var compound=ShapeFactory.CreateCompound(pieces);
                            return Place(compound,e.Placement);
                        }
                        finally{foreach(var piece in pieces)piece.Dispose();}
                    }
                    catch {if(!currentOwnedByPieces)current.Dispose();throw;}
                }
            case RevolveRecipe r:
                return BuildRevolve(r,token,canonicalExpressions);
            default:throw new NotSupportedException("Unsupported geometry recipe.");
        }
    }
    public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken cancellationToken=default)=>Run(()=>
    {
        if(Path.GetExtension(path).Equals(".stl",StringComparison.OrdinalIgnoreCase))return ImportStl(path,assets,cancellationToken);
        var clock=Stopwatch.StartNew();
        using var native=XdeDocument.ReadExchange(Path.GetFullPath(path));
        double nativeReadMs=clock.Elapsed.TotalMilliseconds;clock.Restart();
        using var files=new KernelFiles();
        var contextPath=files.PathFor("import.xbf");native.Save(contextPath);
        double contextSaveMs=clock.Elapsed.TotalMilliseconds;clock.Restart();
        var leases=new List<IAssetLease>();var results=new List<GeometryResult>();
        try
        {
            var context=assets.Stage(File.ReadAllBytes(contextPath));leases.Add(context);
            double contextStageMs=clock.Elapsed.TotalMilliseconds;clock.Restart();
            double shapeStoreMs=0,colorMs=0;int partCount=0;
            double topologyMs=0,validityMs=0,boundsMs=0,volumeMs=0,writeMs=0,stageMs=0;
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
                    long shapeStart=Stopwatch.GetTimestamp();
                    GeometryResult stored;
                    try
                    {
                        stored=OcctGeometryBridge.StoreShape(local,assets,t=>
                        {topologyMs+=t.TopologyMs;validityMs+=t.ValidityMs;boundsMs+=t.BoundsMs;
                         volumeMs+=t.VolumeMs;writeMs+=t.WriteMs;stageMs+=t.StageMs;});
                    }
                    catch(CadValidationException error)
                    {
                        throw new CadValidationException($"Exchange part '{name}' ({label.Entry}) failed exact BRep validation: {error.Message}");
                    }
                    results.Add(stored);
                    shapeStoreMs+=Stopwatch.GetElapsedTime(shapeStart).TotalMilliseconds;partCount++;
                    var geometry=stored.Geometry with {Source=new(context.Id,label.Entry,AssetFormatPolicy.CurrentXde)};
                    var bodyId=BodyId.New();var featureId=FeatureId.New();
                    long colorStart=Stopwatch.GetTimestamp();var color=OverallColor(label);
                    colorMs+=Stopwatch.GetElapsedTime(colorStart).TotalMilliseconds;
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
            double transferMs=clock.Elapsed.TotalMilliseconds;clock.Restart();
            doc.Validate();
            foreach(var id in doc.ReferencedAssets())leases.Add(assets.Acquire(id));
            double validateAndLeaseMs=clock.Elapsed.TotalMilliseconds;
            if(Environment.GetEnvironmentVariable("CADORYX_IMPORT_PROFILE") is {Length:>0} profilePath)
                try{File.AppendAllText(profilePath,JsonSerializer.Serialize(new{path=Path.GetFullPath(path),nativeReadMs,
                    contextSaveMs,contextStageMs,transferMs,shapeStoreMs,colorMs,partCount,
                    topologyMs,validityMs,boundsMs,volumeMs,writeMs,stageMs,
                    validateAndLeaseMs,utc=DateTimeOffset.UtcNow})+Environment.NewLine);}
                catch(IOException){}catch(UnauthorizedAccessException){}
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
        var suppressionBlocked=FeatureSuppression.Blocked(snapshot);
        if(snapshot.Features.Values.Any(f=>f.IsStale&&!suppressionBlocked.Contains(f.Id)))
            throw new CadValidationException("Reselect stale feature bindings before export.");
        if(extension is not (".step" or ".stp" or ".iges" or ".igs" or ".stl"))throw new NotSupportedException("Export supports STEP, IGES and STL.");
        if(extension==".stl")return ExportStl(snapshot,assets,path,options,cancellationToken);
        using var native=XdeDocument.Create();using var sourceContexts=new ExportStyleContexts(assets);
        using var unstyled=XdeDocument.Create();using var unstyledTransaction=unstyled.BeginTransaction("Inspect body root locations");
        var labels=new Dictionary<DefinitionId,XdeLabel>();var expressionConversions=ImmutableArray.CreateBuilder<CadDiagnostic>();
        XdeLabel BodyLabel(CadBody body,string name)
        {
            if(body.Appearance.PreserveSourceStyles&&!body.Appearance.ByLayer&&body.Geometry.Source is {} source)
            {
                var original=sourceContexts.Get(source).GetLabel(source.DefinitionEntry);
                using var location=original.Location;using var transform=location.ToTransform();using var inverse=transform.Inverted();
                return native.CopyTransformedDefinition(original,inverse,name);
            }
            using var shape=ExchangeExpressionShape(snapshot,body,assets,expressionConversions,cancellationToken);
            // XCAF represents a located shape as a reference plus an unlocated
            // definition. Passing that reference as a reusable component can
            // export both the original placement and an unwanted origin copy.
            // Keep those temporary labels in a separate document, then bake the
            // exact root location through the existing transformation history.
            var temporary=unstyled.AddShape(shape,name);using var rootLocation=temporary.Location;XdeLabel label;
            if(!rootLocation.IsIdentity)
            {
                using var placement=rootLocation.ToTransform();
                label=native.CopyTransformedDefinition(temporary.ReferredShape,placement,name);
            }
            else label=native.AddShape(shape,name);
            label.Color=OcctGeometryBridge.ToXdeColor(body.Appearance.ByLayer?snapshot.Layers[body.LayerId].Argb:body.Appearance.Argb);
            return label;
        }
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
                label=BodyLabel(snapshot.Bodies[bodyIds[0]],definition.Name);
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
                        var body=snapshot.Bodies[bodyId];var child=BodyLabel(body,body.Name);
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
        unstyledTransaction.Commit();
        string fullPath=Path.GetFullPath(path);string? directory=Path.GetDirectoryName(fullPath);Directory.CreateDirectory(directory!);
        string temp=Path.Combine(directory!,"."+Guid.NewGuid().ToString("N")+Path.GetExtension(path));
        try
        {
            if(Path.GetExtension(path).ToLowerInvariant() is ".igs" or ".iges")native.WriteIges(temp);else native.WriteStep(temp);
            cancellationToken.ThrowIfCancellationRequested();File.Move(temp,fullPath,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
        var diagnostics=ImmutableArray.CreateBuilder<CadDiagnostic>();
        diagnostics.AddRange(expressionConversions);
        diagnostics.Add(new("EXPORT.METADATA","Exports definitions, placements, names, overall colors and preserved source subshape colors through exact transformation history. Cadoryx feature history and unsupported source PMI are not transferred."));
        diagnostics.Add(new("EXPORT.PRESENTATION_LIMITS","Source layers, per-face visibility and physical/PBR materials require separate receiver verification. Preserved surface colors do not prove those attributes survived exchange."));
        if(HasThreadIntent(snapshot))diagnostics.Add(ThreadExchangeLoss());
        if(HasExpressionIntent(snapshot))diagnostics.Add(ExpressionExchangeLoss());
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
            var diagnostics=ImmutableArray.CreateBuilder<CadDiagnostic>();
            diagnostics.Add(new("EXPORT.STL_SEMANTICS","STL contains a triangulated surface in millimeter coordinates; it has no intrinsic units, assembly names, colors, constraints or feature history."));
            if(HasThreadIntent(snapshot))diagnostics.Add(ThreadExchangeLoss());
            if(HasExpressionIntent(snapshot))diagnostics.Add(ExpressionExchangeLoss());
            return new(full,"STL",diagnostics.ToImmutable());
        }
        finally{foreach(var shape in shapes)shape.Dispose();if(File.Exists(temp))File.Delete(temp);}
    }
    private static bool HasThreadIntent(DocumentSnapshot document)=>document.Features.Values.Any(f=>
        f.Recipe is HoleRecipe {Thread:not null} or HolePatternRecipe {Template.Thread:not null});
    private static CadDiagnostic ThreadExchangeLoss()=>new("EXPORT.THREAD_INTENT",
        "Thread designation, pitch, hand, profile identity and effective depth are not written as author semantics in this exchange format. Symbolic authoring contains only pilot geometry; modeled authoring contains tooth geometry, triangulated for STL. Save Cadoryx to retain thread intent.");
}
