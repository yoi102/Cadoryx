using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Commands;

/// <summary>Remaps authoring identities only. Immutable BRep assets and their topology evidence are unchanged.</summary>
internal sealed class PartIdentityClone(DocumentSnapshot document,DefinitionId source,DefinitionId target,
    IReadOnlyDictionary<FeatureId,FeatureId> features,IReadOnlyDictionary<BodyId,BodyId> bodies,
    IReadOnlyDictionary<SketchId,SketchId> sketches)
{
    private readonly Dictionary<Guid,Guid> datums=document.Datums.Planes.Values.Where(p=>p.PartId==source).Select(p=>p.Id)
        .Concat(document.Datums.Axes.Values.Where(p=>p.PartId==source).Select(p=>p.Id))
        .Concat(document.Datums.Points.Values.Where(p=>p.PartId==source).Select(p=>p.Id)).ToDictionary(id=>id,_=>Guid.NewGuid());
    private readonly Dictionary<TopologyReferenceId,TopologyReference> references=[];

    public Guid Datum(Guid id)=>datums.TryGetValue(id,out var mapped)?mapped:
        throw new CadValidationException("Part datum refers outside its own authoring graph.");
    private Guid? Datum(Guid? id)=>id is {} value?Datum(value):null;
    public FeatureId Feature(FeatureId id)=>features.TryGetValue(id,out var mapped)?mapped:
        throw new CadValidationException("Part feature refers outside its own authoring graph.");
    public SketchId Sketch(SketchId id)=>sketches.TryGetValue(id,out var mapped)?mapped:
        throw new CadValidationException("Part sketch refers outside its own authoring graph.");
    public BodyId Body(BodyId id)=>bodies.TryGetValue(id,out var mapped)?mapped:
        throw new CadValidationException("Part body refers outside its own authoring graph.");
    public FeatureId ExistingFeature(FeatureId id)=>features.GetValueOrDefault(id,id);
    public BodyId ExistingBody(BodyId id)=>bodies.GetValueOrDefault(id,id);
    public DefinitionId ExistingPart(DefinitionId id)=>id==source?target:id;
    public TopologyReference Reference(TopologyReference reference)
    {
        if(!references.TryGetValue(reference.Id,out var copied))
            references.Add(reference.Id,copied=reference with{Id=TopologyReferenceId.New(),
                FeatureId=Feature(reference.FeatureId),OutputBodyId=Body(reference.OutputBodyId)});
        return copied;
    }
    public ExactTopologySelection Exact(ExactTopologySelection selection)=>selection with{FeatureId=Feature(selection.FeatureId)};
    private ExactDatumGeometry? Exact(ExactDatumGeometry? evidence)=>evidence is null?null:evidence with{Selection=Exact(evidence.Selection)};
    public SketchProfileReference? Profile(SketchProfileReference? profile)=>profile is null?null:profile with{SketchId=Sketch(profile.SketchId)};
    private DatumPlaneSource PlaneSource(DatumPlaneSource s)=>s with
        {Datum=Datum(s.Datum),Point=Datum(s.Point),Feature=s.Feature is {} id?Feature(id):null,Exact=Exact(s.Exact)};

    public GeometryRecipe Recipe(GeometryRecipe recipe)=>recipe switch
    {
        RepetitionRecipe r=>r with{Datum=Datum(r.Datum),CopyIds=r.CopyIds.Select(_=>Guid.NewGuid()).ToImmutableArray()},
        HoleRecipe r=>r with{Datum=Datum(r.Datum)},
        HolePatternRecipe r=>r with{Seed=Feature(r.Seed),Template=(HoleRecipe)Recipe(r.Template),Layout=(RepetitionRecipe)Recipe(r.Layout)},
        ExtrusionPatternRecipe r=>r with{Seed=Feature(r.Seed),Template=(ExtrudeRecipe)Recipe(r.Template),Layout=(RepetitionRecipe)Recipe(r.Layout)},
        RevolveRecipe r=>r with{Axis=r.Axis is {} axis?axis with{Datum=Datum(axis.Datum)}:null},
        ExtrudeRecipe r=>r with{Options=r.Options is {} options?options with
            {Datum=Datum(options.Datum),LimitFace=options.LimitFace is {} face?Exact(face):null,
                SupportFace=options.SupportFace is {} support?Exact(support):null}:null},
        ShellRecipe r=>r with{ClosingFaces=r.ClosingFaces.Select(Exact).ToImmutableArray()},
        DraftRecipe r=>r with{Programs=r.Programs.Select(p=>p with{Face=Exact(p.Face),Datum=Datum(p.Datum)}).ToImmutableArray()},
        SweepRecipe r=>r with{Section=r.Section with{Source=Profile(r.Section.Source)},
            Path=r.Path with{Source=r.Path.Source is {} path?path with{Sketch=Sketch(path.Sketch)}:null}},
        LoftRecipe r=>r with{Channels=r.Channels.Select(c=>c with{Sources=c.Sources.Select(Profile).ToImmutableArray()}).ToImmutableArray()},
        _=>recipe
    };

    public DatumBook Datums()
    {
        var book=document.Datums;
        foreach(var p in document.Datums.Planes.Values.Where(p=>p.PartId==source))
            book=book with{Planes=book.Planes.Add(Datum(p.Id),p with{Id=Datum(p.Id),PartId=target,
                Primary=PlaneSource(p.Primary),Secondary=p.Secondary is {} secondary?PlaneSource(secondary):null,HingeAxis=Datum(p.HingeAxis)})};
        foreach(var a in document.Datums.Axes.Values.Where(p=>p.PartId==source))
            book=book with{Axes=book.Axes.Add(Datum(a.Id),a with{Id=Datum(a.Id),PartId=target,
                First=Datum(a.First),Second=Datum(a.Second),Exact=Exact(a.Exact)})};
        foreach(var p in document.Datums.Points.Values.Where(p=>p.PartId==source))
            book=book with{Points=book.Points.Add(Datum(p.Id),p with{Id=Datum(p.Id),PartId=target,
                First=Datum(p.First),Second=Datum(p.Second),Third=Datum(p.Third),Exact=Exact(p.Exact)})};
        foreach(var b in document.Datums.SketchBindings.Values.Where(b=>sketches.ContainsKey(b.Sketch)))
            book=book with{SketchBindings=book.SketchBindings.Add(Sketch(b.Sketch),b with{Sketch=Sketch(b.Sketch),Datum=Datum(b.Datum)})};
        return book;
    }

    // Named parameters are document-global. An independent part keeps those explicit drivers;
    // a reference parameter continues to read its originally authored target.
    public ParameterBook Parameters()=>document.Parameters with{Bindings=document.Parameters.Bindings.AddRange(
        document.Parameters.Bindings.Select(binding=>
        {
            Guid? owner=binding.Target.Kind switch
            {
                ParameterTargetKind.Feature=>features.TryGetValue(new(binding.Target.Owner),out var feature)?feature.Value:null,
                ParameterTargetKind.SketchConstraint or ParameterTargetKind.ExactSketchCurve or ParameterTargetKind.SketchRegion=>sketches.TryGetValue(new(binding.Target.Owner),out var sketch)?sketch.Value:null,
                ParameterTargetKind.Datum=>datums.TryGetValue(binding.Target.Owner,out var datum)?datum:null,
                _=>null
            };
            return owner is {} id?binding with{Id=Guid.NewGuid(),Target=binding.Target with{Owner=id}}:null;
        }).OfType<ParameterBinding>())};
}

/// <summary>Path changes come from explicit assembly clones, never from geometry or a search for a similar instance.</summary>
internal sealed class OccurrenceIdentityRemap(DocumentSnapshot document,OccurrencePath selected,PartIdentityClone? clone=null)
{
    private readonly HashSet<OccurrencePath> selectedParts=SelectedParts(document,selected,clone);
    private static HashSet<OccurrencePath> SelectedParts(DocumentSnapshot document,OccurrencePath selected,PartIdentityClone? clone)
    {
        if(clone is null)return [];
        return LinkedPaths(document,selected);
    }
    private static HashSet<OccurrencePath> LinkedPaths(DocumentSnapshot document,OccurrencePath selected)
    {
        var paths=new HashSet<OccurrencePath>{selected};var queue=new Queue<OccurrencePath>();queue.Enqueue(selected);
        while(queue.TryDequeue(out var path))
            for(int depth=1;depth<=path.Slots.Length;depth++)
            {
                var prefix=new OccurrencePath(path.DocumentId,path.Slots.Take(depth));var placement=OccurrencePlacement.Resolve(document,prefix);
                foreach(var pattern in document.AssemblyPatterns.Values.Where(p=>p.Kind!=RepetitionKind.Mirror&&p.Owner==placement.OwnerId&&p.Source==placement.Slot.Id))
                    foreach(var id in pattern.CopySlots)
                    {
                        var alias=new OccurrencePath(path.DocumentId,path.Slots.SetItem(depth-1,id));
                        if(paths.Add(alias)){if(paths.Count>100000)throw new CadValidationException("Linked instance paths exceed the document occurrence limit.");queue.Enqueue(alias);}
                    }
            }
        return paths;
    }
    private readonly List<(ImmutableArray<ComponentSlotId> Prefix,IReadOnlyDictionary<ComponentSlotId,ComponentSlotId> Children,
        DefinitionId Before,DefinitionId After)> branches=[];
    private readonly Dictionary<(OccurrencePath Path,DefinitionId Id),DefinitionId> mirrorDefinitions=[];
    private readonly Dictionary<(OccurrencePath Path,BodyId Id),BodyId> mirrorBodies=[];
    public void Add(int depth,IReadOnlyDictionary<ComponentSlotId,ComponentSlotId> children,DefinitionId before,DefinitionId after)
    {
        foreach(var prefix in LinkedPaths(document,new(selected.DocumentId,selected.Slots.Take(depth))))
            branches.Add((prefix.Slots,children,before,after));
    }
    public OccurrencePath Path(OccurrencePath path)
    {
        if(path.DocumentId!=document.Id)return path;
        var ids=path.Slots.ToArray();
        foreach(var (prefix,children,_,_) in branches)
            if(ids.Length>prefix.Length&&path.Slots.Take(prefix.Length).SequenceEqual(prefix)&&children.TryGetValue(path.Slots[prefix.Length],out var id))
                ids[prefix.Length]=id;
        return ids.SequenceEqual(path.Slots)?path:new(path.DocumentId,ids);
    }
    private bool Selected(OccurrencePath path)=>selectedParts.Contains(path);
    private DefinitionId Definition(OccurrencePath path,DefinitionId id)
    {
        if(mirrorDefinitions.TryGetValue((path,id),out var mirrored))return mirrored;
        if(Selected(path))return clone!.ExistingPart(id);
        foreach(var (prefix,_,before,after) in branches)
            if(path.Slots.SequenceEqual(prefix)&&id==before)return after;
        return id;
    }
    private BodyId Body(OccurrencePath path,BodyId id)=>mirrorBodies.TryGetValue((path,id),out var mirrored)?mirrored:
        Selected(path)?clone!.ExistingBody(id):id;
    private TopologyReference? Topology(TopologyReference? reference,OccurrencePath? path)=>
        reference is not null&&path is not null&&Selected(path)&&clone!.ExistingFeature(reference.FeatureId)!=reference.FeatureId?clone.Reference(reference):reference;
    private AssemblyDatumReference? Datum(AssemblyDatumReference? datum)=>datum is null?null:datum with
        {Path=Path(datum.Path),DefinitionId=Definition(datum.Path,datum.DefinitionId),
            BodyId=Body(datum.Path,datum.BodyId),
            FeatureId=Selected(datum.Path)&&datum.FeatureId is {} f?clone!.ExistingFeature(f):datum.FeatureId};
    private ReviewInstance Instance(ReviewInstance instance)=>instance with{Path=Path(instance.Path),
        BodyId=Body(instance.Path,instance.BodyId)};
    private SectionSource Section(SectionSource source)=>source with{Path=Path(source.Path),
        BodyId=Body(source.Path,source.BodyId),
        FeatureId=Selected(source.Path)&&source.FeatureId is {} f?clone!.ExistingFeature(f):source.FeatureId};
    private DrawingSource Drawing(DrawingSource source)=>source with{Path=Path(source.Path),
        Body=Body(source.Path,source.Body),
        Feature=Selected(source.Path)&&source.Feature is {} f?clone!.ExistingFeature(f):source.Feature};

    public DocumentSnapshot Apply(DocumentSnapshot candidate)
    {
        var patterns=candidate.AssemblyPatterns;var parameters=candidate.Parameters;
        var mirrorSourcePaths=new Dictionary<Guid,OccurrencePath>();
        var mirrorOrigins=new Dictionary<Guid,Guid>();
        foreach(var (prefix,children,before,after) in branches.DistinctBy(b=>b.After).ToArray())
            foreach(var pattern in candidate.AssemblyPatterns.Values.Where(p=>p.Owner==before))
            {
                var copy=pattern with{Id=Guid.NewGuid(),Owner=after,Source=children[pattern.Source],
                    CopySlots=pattern.CopySlots.Select(id=>children[id]).ToImmutableArray(),Mirrors=[]};
                if(pattern.Kind==RepetitionKind.Mirror)
                {
                    mirrorOrigins.Add(copy.Id,pattern.Id);
                    mirrorSourcePaths.Add(copy.Id,new(document.Id,prefix.Add(pattern.Source)));
                    var copied=CopyMirrorGraph(candidate,pattern,prefix);
                    candidate=copied.Document;copy=copy with{Mirrors=copied.Links};
                    var owner=(AssemblyDefinition)candidate.Definitions[after];
                    var output=copy.Mirrors.Single(m=>m.Source==pattern.Resolve(document).Seed.DefinitionId).Target;
                    candidate=candidate with{Definitions=candidate.Definitions.SetItem(after,owner with
                        {Children=owner.Children.Select(s=>copy.CopySlots.Contains(s.Id)?s with{DefinitionId=output}:s).ToImmutableArray()})};
                }
                patterns=patterns.Add(copy.Id,copy);
                parameters=parameters with{Bindings=parameters.Bindings.AddRange(candidate.Parameters.Bindings
                    .Where(b=>b.Target.Kind==ParameterTargetKind.AssemblyPattern&&b.Target.Owner==pattern.Id)
                    .Select(b=>b with{Id=Guid.NewGuid(),Target=b.Target with{Owner=copy.Id}}))};
            }
        if(clone is not null)
            foreach(var pattern in patterns.Values.ToArray())
            {
                var seedPaths=candidate.EnumerateOccurrences().Where(o=>o.Path.Slots.Last()==pattern.Source).Select(o=>o.Path);
                if(seedPaths.Any(p=>p.Equals(Path(selected))))
                    patterns=patterns.SetItem(pattern.Id,pattern with{Datum=pattern.Datum is {} datum?clone.Datum(datum):null});
            }
        var mirrorOrder=AssemblyMirrors.OrderedPatterns(document).Select((id,index)=>(id,index)).ToDictionary(p=>p.id,p=>p.index);
        foreach(var pattern in patterns.Values.Where(p=>p.Kind==RepetitionKind.Mirror)
            .OrderBy(p=>mirrorOrder[mirrorOrigins.GetValueOrDefault(p.Id,p.Id)]).ToArray())
        {
            var sourcePath=mirrorSourcePaths.GetValueOrDefault(pattern.Id)??document.EnumerateOccurrences()
                .Where(o=>o.Path.Slots.Last()==pattern.Source).Select(o=>o.Path).FirstOrDefault(path=>
                {
                    var placement=OccurrencePlacement.Resolve(candidate,Path(path));
                    return placement.OwnerId==pattern.Owner&&placement.Slot.Id==pattern.Source;
                });
            // The old owner can remain as an unused definition. It keeps its own source identities.
            if(sourcePath is not null)
            {
                var remapped=RemapMirrorSources(candidate,pattern,sourcePath,document.AssemblyPatterns[mirrorOrigins.GetValueOrDefault(pattern.Id,pattern.Id)]);
                candidate=remapped.Document;patterns=patterns.SetItem(pattern.Id,remapped.Pattern);
            }
        }
        var book=candidate.Datums;
        if(book.WorkPlane is {} workPlane)book=book with{WorkPlane=workPlane with{Occurrence=Path(workPlane.Occurrence),
            Datum=Selected(workPlane.Occurrence)?clone!.Datum(workPlane.Datum):workPlane.Datum}};
        return candidate with{Datums=book,AssemblyPatterns=patterns,Parameters=parameters,
            AssemblyConstraints=candidate.AssemblyConstraints.ToImmutableDictionary(p=>p.Key,p=>
            {
                var c=p.Value;return c with{PrimaryPath=Path(c.PrimaryPath),SecondaryPath=c.SecondaryPath is {} s?Path(s):null,
                    PrimaryDefinitionId=Definition(c.PrimaryPath,c.PrimaryDefinitionId),
                    SecondaryDefinitionId=c.SecondaryPath is {} secondary&&c.SecondaryDefinitionId is {} secondaryId?Definition(secondary,secondaryId):c.SecondaryDefinitionId,
                    PrimaryTopology=Topology(c.PrimaryTopology,c.PrimaryPath),SecondaryTopology=Topology(c.SecondaryTopology,c.SecondaryPath),
                    PrimaryDatum=Datum(c.PrimaryDatum),SecondaryDatum=Datum(c.SecondaryDatum)};
            }),
            ReviewBookmarks=candidate.ReviewBookmarks.ToImmutableDictionary(p=>p.Key,p=>p.Value with
                {Hidden=p.Value.Hidden.Select(Instance).ToImmutableArray(),Isolated=p.Value.Isolated?.Select(Instance).ToImmutableArray()}),
            AssociatedSections=candidate.AssociatedSections.ToImmutableDictionary(p=>p.Key,p=>p.Value with
                {Sources=p.Value.Sources.Select(Section).ToImmutableArray()}),
            DrawingSheets=candidate.DrawingSheets.ToImmutableDictionary(p=>p.Key,p=>p.Value with
                {Views=p.Value.Views.Select(v=>v with{Sources=v.Sources.Select(Drawing).ToImmutableArray()}).ToImmutableArray(),
                    Dimensions=p.Value.Dimensions.Select(d=>d with{First=Datum(d.First)!,Second=Datum(d.Second)}).ToImmutableArray()})};
    }

    private (DocumentSnapshot Document,AssemblyRepetition Pattern) RemapMirrorSources(
        DocumentSnapshot candidate,AssemblyRepetition pattern,OccurrencePath sourcePath,AssemblyRepetition original)
    {
        var paths=new List<(OccurrencePath Source,OccurrencePath Output,DefinitionId Id)>();
        var oldLinks=original.Mirrors.ToDictionary(m=>m.Source);
        var currentLinks=pattern.Mirrors.ToDictionary(m=>m.Source);
        void Visit(OccurrencePath path,OccurrencePath output,DefinitionId id,int depth)
        {
            if(depth>128||paths.Count>=100000)throw new CadValidationException("Mirror identity clone exceeds its path or depth limit.");
            paths.Add((path,output,id));
            if(document.Definitions[id] is AssemblyDefinition assembly)
                foreach(var slot in assembly.Children)
                    Visit(path.Append(slot.Id),output.Append(oldLinks[id].Slots.Single(s=>s.Source==slot.Id).Target),slot.DefinitionId,depth+1);
        }
        var outputRoot=new OccurrencePath(document.Id,sourcePath.Slots.RemoveAt(sourcePath.Slots.Length-1)).Append(original.CopySlots.Single());
        Visit(sourcePath,outputRoot,OccurrencePlacement.Resolve(document,sourcePath).Slot.DefinitionId,0);
        var links=new Dictionary<(DefinitionId Original,DefinitionId Source),AssemblyMirrorDefinition>();
        foreach(var group in paths.GroupBy(p=>p.Id))
        {
            var variants=group.GroupBy(p=>Definition(p.Source,p.Id)).OrderBy(g=>g.Key==group.Key?0:1).ThenBy(g=>g.Key.Value).ToArray();
            var baseline=currentLinks[group.Key];
            for(int i=0;i<variants.Length;i++)
            {
                var variant=variants[i];var path=variant.First().Source;
                var target=i==0?baseline.Target:DefinitionId.New();
                var bodyIds=baseline.Bodies.ToDictionary(b=>b.Target,b=>i==0?b.Target:BodyId.New());
                var slotIds=baseline.Slots.ToDictionary(s=>s.Target,s=>i==0?s.Target:ComponentSlotId.New());
                if(i>0)
                {
                    // This is an explicit identity clone of unchanged immutable reflected geometry.
                    // The mirror postprocessor supplies its final child graph before publication.
                    var definition=candidate.Definitions[baseline.Target];
                    CadDefinition copied=definition switch
                    {
                        PartDefinition p=>p with{Id=target,Bodies=p.Bodies.Select(b=>bodyIds[b]).ToImmutableArray()},
                        AssemblyDefinition a=>a with{Id=target,Children=a.Children.Select(s=>s with{Id=slotIds[s.Id]}).ToImmutableArray()},
                        _=>throw new CadValidationException("Unsupported reflected identity clone.")
                    };
                    candidate=candidate with{Definitions=candidate.Definitions.Add(target,copied)};
                    foreach(var body in baseline.Bodies)
                        candidate=candidate with{Bodies=candidate.Bodies.Add(bodyIds[body.Target],candidate.Bodies[body.Target] with{Id=bodyIds[body.Target],PartId=target})};
                }
                var link=baseline with{Source=variant.Key,Target=target,
                    Bodies=baseline.Bodies.Select(b=>b with{Source=Body(path,b.Source),Target=bodyIds[b.Target]}).ToImmutableArray(),
                    Slots=baseline.Slots.Select(s=>s with{Source=Path(path.Append(s.Source)).Slots.Last(),Target=slotIds[s.Target]}).ToImmutableArray()};
                links.Add((group.Key,variant.Key),link);
                var old=oldLinks[group.Key];
                foreach(var occurrence in variant)
                {
                    mirrorDefinitions[(occurrence.Output,old.Target)]=target;
                    foreach(var body in old.Bodies)
                        mirrorBodies[(occurrence.Output,body.Target)]=link.Bodies.Single(b=>b.Source==Body(occurrence.Source,body.Source)).Target;
                    if(!old.Slots.IsEmpty)
                        branches.Add((occurrence.Output.Slots,old.Slots.ToDictionary(s=>s.Target,
                            s=>link.Slots.Single(mapped=>mapped.Source==Path(occurrence.Source.Append(s.Source)).Slots.Last()).Target),old.Target,target));
                }
            }
        }
        return(candidate,pattern with{Mirrors=links.Values.OrderBy(m=>m.Source.Value).ToImmutableArray()});
    }

    private (DocumentSnapshot Document,ImmutableArray<AssemblyMirrorDefinition> Links) CopyMirrorGraph(
        DocumentSnapshot candidate,AssemblyRepetition pattern,ImmutableArray<ComponentSlotId> prefix)
    {
        var definitions=pattern.Mirrors.ToDictionary(m=>m.Target,_=>DefinitionId.New());
        var bodies=pattern.Mirrors.SelectMany(m=>m.Bodies).ToDictionary(b=>b.Target,_=>BodyId.New());
        var slots=pattern.Mirrors.SelectMany(m=>m.Slots).ToDictionary(s=>s.Target,_=>ComponentSlotId.New());
        foreach(var link in pattern.Mirrors)
        {
            var id=definitions[link.Target];
            var source=candidate.Definitions[link.Target];
            CadDefinition copied=source switch
            {
                PartDefinition p=>p with{Id=id,Bodies=p.Bodies.Select(b=>bodies[b]).ToImmutableArray()},
                AssemblyDefinition a=>a with{Id=id,Children=a.Children.Select(s=>s with
                    {Id=slots[s.Id],DefinitionId=definitions[s.DefinitionId]}).ToImmutableArray()},
                _=>throw new CadValidationException("Unsupported mirror definition identity clone.")
            };
            candidate=candidate with{Definitions=candidate.Definitions.Add(id,copied)};
            foreach(var body in link.Bodies)
                candidate=candidate with{Bodies=candidate.Bodies.Add(bodies[body.Target],candidate.Bodies[body.Target] with
                    {Id=bodies[body.Target],PartId=id})};
        }
        var byTarget=pattern.Mirrors.ToDictionary(m=>m.Target);int paths=0;
        void Record(OccurrencePath path,DefinitionId id)
        {
            if(++paths>100000)throw new CadValidationException("Mirror identity clone exceeds the document occurrence limit.");
            var link=byTarget[id];mirrorDefinitions.Add((path,id),definitions[id]);
            foreach(var body in link.Bodies)mirrorBodies.Add((path,body.Target),bodies[body.Target]);
            if(document.Definitions[id] is AssemblyDefinition assembly)
            {
                branches.Add((path.Slots,assembly.Children.ToDictionary(s=>s.Id,s=>slots[s.Id]),id,definitions[id]));
                foreach(var child in assembly.Children)Record(path.Append(child.Id),child.DefinitionId);
            }
        }
        // Every explicit linear/circular alias of this cloned parent uses the same copied graph.
        var copiedOwner=branches.Single(b=>b.Before==pattern.Owner&&b.Prefix.SequenceEqual(prefix)).After;
        var root=pattern.Resolve(document).Seed.DefinitionId;var target=pattern.Mirrors.Single(m=>m.Source==root).Target;
        foreach(var branch in branches.Where(b=>b.Before==pattern.Owner&&b.After==copiedOwner).ToArray())
            foreach(var slot in pattern.CopySlots)Record(new(document.Id,branch.Prefix.Add(slot)),target);
        return(candidate,pattern.Mirrors.Select(m=>m with{Target=definitions[m.Target],
            Bodies=m.Bodies.Select(b=>b with{Target=bodies[b.Target]}).ToImmutableArray(),
            Slots=m.Slots.Select(s=>s with{Target=slots[s.Target]}).ToImmutableArray()}).ToImmutableArray());
    }
}
