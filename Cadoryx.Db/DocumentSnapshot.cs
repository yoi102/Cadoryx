using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Immutable business state. All mutations are validated before a session publishes them.</summary>
public sealed record DocumentSnapshot(DocumentId Id,DocumentStateId StateId,string Name,DefinitionId RootAssemblyId,
    DocumentSettings Settings,ImmutableDictionary<DefinitionId,CadDefinition> Definitions,
    ImmutableDictionary<BodyId,CadBody> Bodies,ImmutableDictionary<FeatureId,FeatureDefinition> Features,
    ImmutableDictionary<LayerId,CadLayer> Layers,ImmutableDictionary<MaterialId,CadMaterial> Materials,
    ImmutableArray<PreservedSection> Extensions=default,ImmutableArray<AssetId> RetainedAssets=default,
    ImmutableDictionary<AssetId,AssetFormat>? RetainedAssetFormats=null)
{
    /// <summary>Never persisted; prevents a view of cached intermediate outputs from becoming a saved/exported model.</summary>
    public bool IsTemporaryHistoryView {get;init;}
    public ParameterBook Parameters {get;init;}=ParameterBook.Empty;
    public DatumBook Datums {get;init;}=DatumBook.Empty;
    public ImmutableDictionary<Guid,AssemblyRepetition> AssemblyPatterns {get;init;}=ImmutableDictionary<Guid,AssemblyRepetition>.Empty;
    public ImmutableDictionary<FeatureId,AssociatedSection> AssociatedSections {get;init;}=ImmutableDictionary<FeatureId,AssociatedSection>.Empty;
    public ImmutableDictionary<Guid,EngineeringDimension> Dimensions {get;init;}=ImmutableDictionary<Guid,EngineeringDimension>.Empty;
    public ImmutableDictionary<Guid,ReviewBookmark> ReviewBookmarks {get;init;}=ImmutableDictionary<Guid,ReviewBookmark>.Empty;
    public ImmutableDictionary<SketchId,CadSketch> Sketches {get;init;}=ImmutableDictionary<SketchId,CadSketch>.Empty;
    public ImmutableDictionary<TopologyReferenceId,TopologyReference> TopologyReferences {get;init;}=ImmutableDictionary<TopologyReferenceId,TopologyReference>.Empty;
    public ImmutableDictionary<HistoryQueryId,HistoryQuery> HistoryQueries {get;init;}=ImmutableDictionary<HistoryQueryId,HistoryQuery>.Empty;
    public ImmutableDictionary<AssemblyConstraintId,AssemblyConstraint> AssemblyConstraints {get;init;}=
        ImmutableDictionary<AssemblyConstraintId,AssemblyConstraint>.Empty;
    public ImmutableDictionary<DefinitionId,ExternalPartLink> ExternalParts {get;init;}=
        ImmutableDictionary<DefinitionId,ExternalPartLink>.Empty;
    public ImmutableDictionary<Guid,TechnicalDrawingSheet> DrawingSheets {get;init;}=
        ImmutableDictionary<Guid,TechnicalDrawingSheet>.Empty;
    public ImmutableDictionary<Guid,DrawingTemplateDefinition> DrawingTemplates {get;init;}=
        ImmutableDictionary<Guid,DrawingTemplateDefinition>.Empty;
    public static DocumentSnapshot Create(string name)
    {
        CadGuard.Name(name);var root=DefinitionId.New();var layer=LayerId.New();
        return new(DocumentId.New(),DocumentStateId.New(),name,root,new(),
            ImmutableDictionary<DefinitionId,CadDefinition>.Empty.Add(root,new AssemblyDefinition(root,name,[])),
            ImmutableDictionary<BodyId,CadBody>.Empty,ImmutableDictionary<FeatureId,FeatureDefinition>.Empty,
            ImmutableDictionary<LayerId,CadLayer>.Empty.Add(layer,new(layer,"Default")),ImmutableDictionary<MaterialId,CadMaterial>.Empty);
    }
    public IEnumerable<GeometryAssetRef> ReferencedGeometry()=>Bodies.Values.Select(x=>x.Geometry).Concat(Features.Values.SelectMany(x=>x.Recipe.AssetInputs.Append(x.Result))).Concat(DatumConstructions.ExactSources(Datums).Select(e=>e.Source))
        .Concat(Sketches.Values.SelectMany(s=>s.NativeProjections.Select(p=>p.Source)));
    public IEnumerable<AssetId> ReferencedAssets() => ReferencedGeometry()
        .SelectMany(g=>g.Source is {} s?g.BoundaryAssets().Append(s.ContextAssetId):g.BoundaryAssets())
        .Concat(MaterialTextures().Select(t=>t.AssetId)).Concat(DrawingHistoryAssets()).Concat(Extensions.IsDefault?[]:Extensions.Select(x=>x.PayloadAssetId)).Concat(RetainedAssets.IsDefault?[]:RetainedAssets).Distinct();
    public IEnumerable<CadTextureRef> MaterialTextures()=>Materials.Values.Select(m=>m.Appearance?.Texture).OfType<CadTextureRef>();
    public IEnumerable<AssetId> DrawingHistoryAssets()=>DrawingSheets.Values.SelectMany(s=>s.DatumSymbols.Select(d=>d.Source)
        .Concat(s.ControlFrames.Select(f=>f.Source)).Concat(s.ControlFrames.SelectMany(f=>f.DatumBindings.Select(b=>b.Source)))
        .Concat(s.Balloons.Select(b=>b.Source)).Concat(s.HoleTables.SelectMany(t=>t.Rows.Select(r=>r.Evidence.Source))))
        .Where(s=>s.HistoryOrigin is not null).SelectMany(s=>new[]{s.Asset,s.HistoryOrigin!.Asset})
        .Concat(DrawingPlanarReferences().SelectMany(s=>s.HistoryOrigin is {} origin?new[]{s.Asset,origin.Asset}:new[]{s.Asset})).Distinct();
    private IEnumerable<DrawingTopologyReference> DrawingPlanarReferences()=>DrawingSheets.Values.SelectMany(s=>
        s.DatumSymbols.Select(d=>d.PlanarSizeEvidence).Concat(s.ControlFrames.Select(f=>f.PlanarSizeEvidence))
            .OfType<DrawingPlanarSizeEvidence>().SelectMany(p=>new[]{p.Source,p.OppositeSource})
            .Concat(s.ControlFrames.SelectMany(f=>f.DatumBindings.Select(b=>b.OppositeSource)).OfType<DrawingTopologyReference>()));

    public DocumentSnapshot WithNewState() => this with { StateId=DocumentStateId.New() };
    public void Validate()=>Validate(true);
    public void Validate(bool validateReviewCache)
    {
        if(IsTemporaryHistoryView)throw new CadValidationException("A temporary history view cannot be saved, exported or committed.");
        ParameterGraph.ValidateApplied(this);
        DatumPlanes.ValidateApplied(this);
        AssemblyRepetitions.ValidateApplied(this);
        EngineeringReviewValidation.Validate(this);
        if(DrawingSheets.Count>64)throw new CadValidationException("Too many drawing sheets.");
        if(DrawingTemplates is null||DrawingTemplates.Count>64)throw new CadValidationException("Too many drawing templates.");
        foreach(var (key,entry) in DrawingTemplates)
        {
            if(entry is null||key!=entry.Id)throw new CadValidationException("Invalid template library identity.");
            entry.Validate();
        }
        if(DrawingTemplates.Values.Select(t=>t.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=DrawingTemplates.Count)
            throw new CadValidationException("Template library names must be unique, ignoring case and outer spaces.");
        foreach(var (id,sheet) in DrawingSheets)
        {
            if(id!=sheet.Id)throw new CadValidationException("Drawing sheet key mismatch.");sheet.Validate(Id);
            if(sheet.Views.Any(v=>v.Hatching is {} hatch&&hatch.Materials.Any(r=>!Materials.ContainsKey(r.Material))))
                throw new CadValidationException("Drawing hatch rule references a missing material.");
        }
        CadGuard.Id(Id);CadGuard.Id(StateId);CadGuard.Name(Name);Settings.Validate();
        foreach(var (id,reference) in TopologyReferences)
        {
            reference.Validate();
            if(id!=reference.Id||reference.DocumentId!=Id)throw new CadValidationException("Invalid topology reference ownership.");
            // Deleted producers remain diagnosable and can be restored by exact undo.
            if(Features.TryGetValue(reference.FeatureId,out var producer)&&producer.OutputBodyId!=reference.OutputBodyId)
                throw new CadValidationException("Topology output identity mismatch.");
        }
        foreach(var (id,query) in HistoryQueries)
        {
            query.Validate(Id);
            if(id!=query.Id)throw new CadValidationException("History query key mismatch.");
        }
        if(!Extensions.IsDefault)foreach(var extension in Extensions){CadGuard.Name(extension.Kind);extension.PayloadAssetId.Validate();if(extension.SchemaVersion<1)throw new CadValidationException("Invalid extension version.");}
        if(!RetainedAssets.IsDefault)foreach(var asset in RetainedAssets)asset.Validate();
        if(RetainedAssetFormats is not null)foreach(var (id,format) in RetainedAssetFormats){id.Validate();format.Validate();}
        if(!Definitions.TryGetValue(RootAssemblyId,out var root)||root is not AssemblyDefinition) throw new CadValidationException("Missing root assembly.");
        if(Layers.Count==0) throw new CadValidationException("A document requires a layer.");
        foreach(var (id,sketch) in Sketches)
        {
            sketch.Validate();
            SketchFiniteParametricContact.RequireSolvedRelations(sketch);
            if(id!=sketch.Id||!Definitions.TryGetValue(sketch.PartId,out var owner)||owner is not PartDefinition)
                throw new CadValidationException("Invalid sketch ownership.");
            foreach(var projection in sketch.NativeProjections)
                if(projection.Selection.DocumentId!=Id||Features.TryGetValue(projection.Selection.FeatureId,out var producer)&&producer.PartId!=sketch.PartId)
                    throw new CadValidationException("Native sketch projection belongs to a different document or part.");
        }
        SketchProjectionGeometry.ValidateGraph(Sketches);
        if(Sketches.Values.Any(s=>!s.NativeProjections.IsEmpty))DatumPlanes.Order(this);
        var slots=new HashSet<ComponentSlotId>();var ownedBodies=new HashSet<BodyId>();var ownedFeatures=new HashSet<FeatureId>();
        foreach(var (id,definition) in Definitions)
        {
            CadGuard.Id(id);CadGuard.Name(definition.Name);if(id!=definition.Id)throw new CadValidationException("Definition key mismatch.");
            if(definition is AssemblyDefinition a)
            {
                if(a.Children.IsDefault)throw new CadValidationException("Uninitialized children.");
                foreach(var child in a.Children)
                {
                    CadGuard.Id(child.Id);CadGuard.Name(child.Name);child.LocalTransform.Validate();
                    if(!slots.Add(child.Id)||!Definitions.ContainsKey(child.DefinitionId)||child.DefinitionId==RootAssemblyId)throw new CadValidationException("Invalid or duplicated assembly slot.");
                }
            }
            else if(definition is PartDefinition p)
            {
                if(p.Bodies.IsDefault||p.Features.IsDefault)throw new CadValidationException("Uninitialized part.");
                foreach(var body in p.Bodies)
                    if(!ownedBodies.Add(body)||!Bodies.TryGetValue(body,out var b)||b.PartId!=id)throw new CadValidationException("Invalid body ownership.");
                foreach(var feature in p.Features)
                    if(!ownedFeatures.Add(feature)||!Features.TryGetValue(feature,out var f)||f.PartId!=id)throw new CadValidationException("Invalid feature ownership.");
            }
            else throw new CadValidationException("Unknown definition type.");
        }
        if(ownedBodies.Count!=Bodies.Count || ownedFeatures.Count!=Features.Count)throw new CadValidationException("Orphan body or feature.");
        foreach(var (id,b) in Bodies)
        {
            CadGuard.Id(id);CadGuard.Name(b.Name);b.Geometry.Validate();
            if(b.Id!=id||!Layers.ContainsKey(b.LayerId)||(b.MaterialId is {} m&&!Materials.ContainsKey(m))||
                (b.Producer is {} f&&(!Features.TryGetValue(f,out var feature)||feature.OutputBodyId!=id||feature.Result!=b.Geometry)))
                throw new CadValidationException("Invalid body references.");
        }
        foreach(var (id,f) in Features)
        {
            CadGuard.Id(id);CadGuard.Id(f.OutputBodyId);CadGuard.Name(f.Name);f.Recipe.Validate();f.Result.Validate();
            if(f.Recipe is RepetitionRecipe repetition)repetition.ValidateApplied(this,f);
            if(f.Recipe is RevolveRecipe {Axis:{} revolveAxis})revolveAxis.ValidateApplied(this,f);
            if(f.Recipe is HoleRecipe hole)hole.ValidateApplied(this,f);
            if(f.Recipe is HolePatternRecipe holePattern)holePattern.ValidateApplied(this,f);
            if(f.Recipe is ExtrusionPatternRecipe extrusionPattern)extrusionPattern.ValidateApplied(this,f);
            FaceFeatureRecipes.ValidateApplied(this,f);
            if(f.Recipe is SolidOffsetRecipe offset)offset.ValidateApplied(this,f);
            if(f.Recipe is ContourLocalRecipe contour)contour.ValidateApplied(this,f);
            GuidedAuthoringSources.ValidateApplied(this,f);
            if(f.Recipe is ExtrudeRecipe {Options: {} extrusion})extrusion.ValidateApplied(this,f);
            if(!f.IsStale)f.SketchSource?.ValidateCache(this,f);
            f.TopologyHistory?.ValidateFor(f);
            f.RepetitionPresentation?.ValidateFor(f);
            f.GuidedModelingReport?.ValidateFor(f.Result);
            if(f.IsStale&&f.TopologyHistory is not null)throw new CadValidationException("A stale feature cannot retain topology history.");
            if(f.IsSuppressed&&(!f.IsStale||Bodies.ContainsKey(f.OutputBodyId)))
                throw new CadValidationException("A suppressed feature must have no active body.");
            if(f.Recipe is HistoryFilletRecipe or HistoryChamferRecipe)
            {
                var binding=f.TopologyBinding??throw new CadValidationException("Bound local feature has no confirmed binding.");
                binding.Validate(Id);
                var source=f.Recipe is HistoryFilletRecipe fillet?fillet.Source:((HistoryChamferRecipe)f.Recipe).Source;
                var edgeIndex=f.Recipe is HistoryFilletRecipe f1?f1.FullTopologyIndex:((HistoryChamferRecipe)f.Recipe).FullTopologyIndex;
                if(f.Inputs.Length!=1||f.Inputs[0]!=binding.TargetFeatureId||edgeIndex!=binding.FullTopologyIndex||
                    source.Revision!=binding.TargetRevision||source.AssetId!=binding.TargetAsset||
                    !Features.TryGetValue(binding.TargetFeatureId,out var target)||target.PartId!=f.PartId||
                    !f.IsStale&&(target.Result.Revision!=binding.TargetRevision||target.Result.AssetId!=binding.TargetAsset||target.IsStale))
                    throw new CadValidationException("Bound local feature differs from its target.");
                if(f.Recipe is HistoryChamferRecipe chamfer&&
                    (binding.SupportFace is null||chamfer.SupportFaceIndex!=binding.SupportFace.FullTopologyIndex)||
                    f.Recipe is HistoryFilletRecipe&&binding.SupportFace is not null)
                    throw new CadValidationException("Bound local feature support face differs from its recipe.");
            }
            else if(f.TopologyBinding is not null)throw new CadValidationException("Unexpected feature topology binding.");
            if(id!=f.Id||f.SchemaVersion!=1||f.Inputs.IsDefault||f.Inputs.Distinct().Count()!=f.Inputs.Length)throw new CadValidationException("Invalid feature.");
            if(f.OutputMetadata is {} metadata)
            {
                CadGuard.Name(metadata.Name);
                if(!Layers.ContainsKey(metadata.Layer)||metadata.Material is {} material&&!Materials.ContainsKey(material))throw new CadValidationException("Invalid retained output metadata.");
            }
            foreach(var input in f.Inputs)
                if(!Features.TryGetValue(input,out var upstream)||upstream.PartId!=f.PartId)throw new CadValidationException("Invalid feature dependency.");
            if(f.TopologyHistory is {} history)
                for(int argument=0;argument<f.Inputs.Length;argument++)
                {
                    var geometry=Features[f.Inputs[argument]].Result;var source=history.GetSource(argument);
                    if(geometry.Revision!=source.Revision||geometry.AssetId!=source.Asset)
                        throw new CadValidationException("History operand order differs from its upstream features.");
                }
            if(!f.IsStale&&f.Recipe is LocalFeatureRecipe local&&
                (f.Inputs.Length!=1||Features[f.Inputs[0]].Recipe is not BoxRecipe box||local.Box!=box||local.Source!=Features[f.Inputs[0]].Result))
                throw new CadValidationException("Local feature must reference its current upstream box.");
        }
        foreach(var (id,l) in Layers){CadGuard.Id(id);CadGuard.Name(l.Name);if(id!=l.Id)throw new CadValidationException("Layer key mismatch.");}
        foreach(var (id,m) in Materials){CadGuard.Id(id);CadGuard.Name(m.Name);CadGuard.Positive(m.DensityKgPerMm3);m.Appearance?.Validate();if(id!=m.Id)throw new CadValidationException("Material key mismatch.");}
        ValidateDag(Definitions.Keys,id=>Definitions[id] is AssemblyDefinition a?a.Children.Select(x=>x.DefinitionId):[]);
        ValidateDag(Features.Keys,id=>Features[id].Inputs);
        if(validateReviewCache)EngineeringReviewValidation.ValidateBindings(this);
        if(ExternalParts.Count>4096)throw new CadValidationException("Too many external part links.");
        foreach(var (id,link) in ExternalParts)
        {
            link.Validate();
            if(id!=link.TargetPartId||!Definitions.TryGetValue(id,out var definition)||
               definition is not PartDefinition part||!part.Features.IsEmpty||
               part.Bodies.Any(body=>Bodies[body].Producer is not null))
                throw new CadValidationException("External part must be a frozen part snapshot.");
        }
        if(AssemblyConstraints.Count>4096)throw new CadValidationException("Too many assembly constraints.");
        var constraintOccurrences=AssemblyConstraints.Count>0?EnumerateOccurrences().ToDictionary(o=>o.Path):null;
        foreach(var (id,constraint) in AssemblyConstraints)
        {
            if(id!=constraint.Id)throw new CadValidationException("Assembly constraint key mismatch.");
            var evaluation=constraint.Evaluate(this,constraintOccurrences!);
            if(constraint.Kind==AssemblyConstraintKind.Fixed&&evaluation.Status==AssemblyConstraintStatus.Unsatisfied)
                throw new CadValidationException("A fixed assembly instance cannot move until its constraint is disabled or removed.");
        }
        if(DrawingSheets.Count>0)
        {
            var occurrences=EnumerateOccurrences().ToDictionary(o=>o.Path);
            foreach(var sheet in DrawingSheets.Values)
            {
                foreach(var view in sheet.Views.Where(v=>v.StaleReason is null))
                    foreach(var source in view.Sources)
                    {
                        if(!occurrences.TryGetValue(source.Path,out var occurrence)||
                           Definitions[occurrence.DefinitionId] is not PartDefinition part||
                           !part.Bodies.Contains(source.Body)||!Bodies.TryGetValue(source.Body,out var body)||
                           body.Producer!=source.Feature||body.Geometry.Revision!=source.Revision||
                           body.Geometry.AssetId!=source.Asset||occurrence.WorldTransform!=source.WorldTransform)
                            throw new CadValidationException("Current drawing view has a mismatched source.");
                    }
                foreach(var dimension in sheet.Dimensions.Where(d=>d.StaleReason is null))
                {
                    var view=sheet.Views.Single(v=>v.Id==dimension.ViewId);
                    if(view.StaleReason is not null||
                       !view.Sources.Any(s=>s.Path.Equals(dimension.First.Path)&&s.Body==dimension.First.BodyId)||
                       dimension.Second is {} paired&&!view.Sources.Any(s=>s.Path.Equals(paired.Path)&&s.Body==paired.BodyId)||
                       !dimension.First.IsCurrent(this,dimension.First.Path,dimension.First.DefinitionId)||
                       dimension.Second is {} other&&!other.IsCurrent(this,other.Path,other.DefinitionId))
                        throw new CadValidationException("Current drawing dimension has a stale datum.");
                }
                void Attachment(Guid viewId,DrawingTopologyReference reference)
                {
                    var view=sheet.Views.Single(v=>v.Id==viewId);
                    if(view.StaleReason is not null||!reference.IsCurrent(this)||
                       !view.Sources.Any(s=>s.Path.Equals(reference.Path)&&s.Body==reference.BodyId))
                        throw new CadValidationException("Current manufacturing annotation has a stale or foreign attachment.");
                    if(reference.HistoryOrigin is {} origin&&(!Features.TryGetValue(origin.FeatureId!.Value,out var original)||
                        original.IsStale||original.PartId!=origin.DefinitionId||original.OutputBodyId!=origin.BodyId||
                        original.Result.Revision!=origin.Revision||original.Result.AssetId!=origin.Asset))
                        throw new CadValidationException("Current manufacturing annotation lost its immutable authored origin.");
                }
                foreach(var datum in sheet.DatumSymbols.Where(d=>d.StaleReason is null))
                {
                    Attachment(datum.ViewId,datum.Source);
                    if(datum.PlanarSizeEvidence is {} planar)Attachment(datum.ViewId,planar.OppositeSource);
                }
                foreach(var table in sheet.HoleTables.Where(t=>t.StaleReason is null))
                {
                    if(!occurrences.TryGetValue(table.CoordinatePath,out var basis)||basis.DefinitionId!=table.CoordinateDefinition||
                        !sheet.Views.Single(v=>v.Id==table.ViewId).Sources.Any(s=>s.Path.Equals(table.CoordinatePath)))
                        throw new CadValidationException("Current hole table has a stale or foreign coordinate occurrence.");
                    table.ConfirmCoordinateCache(this);
                    foreach(var row in table.Rows.Where(r=>r.StaleReason is null))
                    {
                        Attachment(table.ViewId,row.Evidence.Source);
                        if(table.Coordinates(this,row.Evidence)!=row.CoordinatesMm)throw new CadValidationException("Hole table coordinate cache differs from current instance placements.");
                    }
                }
                foreach(var frame in sheet.ControlFrames.Where(f=>f.StaleReason is null))
                {
                    Attachment(frame.ViewId,frame.Source);
                    if(frame.PlanarSizeEvidence is {} planar)Attachment(frame.ViewId,planar.OppositeSource);
                    if(frame.ReferencedDatumIds.Any(id=>sheet.DatumSymbols.Single(d=>d.Id==id).StaleReason is not null))
                        throw new CadValidationException("Current control frame consumes a stale datum.");
                }
                foreach(var balloon in sheet.Balloons.Where(b=>b.StaleReason is null))
                {
                    Attachment(balloon.ViewId,balloon.Source);
                    if(balloon.Quantity!=DrawingAssemblyParts.Quantity(this,sheet,balloon.Source.DefinitionId)||
                       balloon.PartName!=Definitions[balloon.Source.DefinitionId].Name)
                        throw new CadValidationException("Current parts table cache differs from its explicit definition or occurrences.");
                }
            }
        }
    }
    private static void ValidateDag<T>(IEnumerable<T> keys,Func<T,IEnumerable<T>> children) where T:notnull
    {
        var done=new HashSet<T>();var active=new HashSet<T>();
        void Visit(T id,int depth)
        {
            if(depth>128)throw new CadValidationException("Graph depth exceeds 128.");
            if(done.Contains(id))return;
            if(!active.Add(id))throw new CadValidationException("Cyclic dependency.");
            foreach(var c in children(id))Visit(c,depth+1);
            active.Remove(id);done.Add(id);
        }
        foreach(var id in keys)Visit(id,0);
    }
    public IEnumerable<CadOccurrence> EnumerateOccurrences(int limit=100000)
    {
        int count=0;
        IEnumerable<CadOccurrence> Expand(DefinitionId id,OccurrencePath path,RigidTransform3d world,bool visible,CadAppearance? appearance,int depth)
        {
            if(depth>128)throw new CadValidationException("Assembly depth exceeds 128.");
            if(Definitions[id] is not AssemblyDefinition assembly)yield break;
            foreach(var slot in assembly.Children)
            {
                if(++count>limit)throw new CadValidationException("Expanded occurrence limit exceeded.");
                var p=path.Append(slot.Id);var t=world*slot.LocalTransform;var v=visible&&slot.IsVisible;var a=slot.AppearanceOverride??appearance;
                yield return new(p,slot.DefinitionId,slot.Name,t,v,a);
                foreach(var child in Expand(slot.DefinitionId,p,t,v,a,depth+1))yield return child;
            }
        }
        return Expand(RootAssemblyId,new(Id,[]),RigidTransform3d.Identity,true,null,0);
    }
}
