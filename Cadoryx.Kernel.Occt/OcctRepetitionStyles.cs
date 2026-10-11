using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IRepetitionPresentationVerifier
{
    private static GeometryResult EvaluateStyledImport(ImportedRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        using var shape=OcctGeometryBridge.ReadShape(recipe.Source,assets);using var input=RepairSnapshot.Create(shape);
        using var stored=OcctGeometryBridge.StoreShape(shape,assets);using var after=OcctGeometryBridge.ReadShape(stored.Geometry,assets);using var output=RepairSnapshot.Create(after);
        var source=recipe.Source.Source!;using var context=OcctGeometryBridge.ReadContext(source.ContextAssetId,assets,source.Format);
        var definition=context.GetLabel(source.DefinitionEntry);
        if(definition.IsAssembly)throw new CadValidationException("STYLES.SOURCE: Imported source context must be a simple definition.");
        using var original=definition.Shape;using var identity=TopLocLocation.Identity;using var local=original.Located(identity);using var contextMap=RepairSnapshot.Create(local);
        if(!VerifyHistoryRoundtrip(input,contextMap,token)||!VerifyHistoryRoundtrip(input,output,token))
            throw new CadValidationException("STYLES.SOURCE: Imported source context and exact BRep have incompatible native maps.");
        IAssetLease? boundary=null,presentation=null;
        try
        {
            boundary=GeometryAssetLease.Acquire(stored.Geometry,assets);presentation=assets.Acquire(source.ContextAssetId);token.ThrowIfCancellationRequested();
            var result=new GeometryResult(stored.Geometry with{Source=source},new StyledGeometryLease(boundary,presentation));boundary=null;presentation=null;return result;
        }
        finally{boundary?.Dispose();presentation?.Dispose();}
    }

    private sealed record DirectPresentation(XdePresentationColors Colors,string[] Layers,XdeMaterial? Material,XdeVisualMaterial? Visual)
    {
        internal bool Assigned=>Colors.General.HasValue||Colors.Surface.HasValue||Colors.Curve.HasValue||!Colors.IsVisible||
            Layers.Length>0||Material is not null||Visual is not null;
        internal static DirectPresentation Read(XdeLabel label)=>new(label.GetPresentationColors(),label.Layers.Order(StringComparer.Ordinal).ToArray(),label.Material,label.VisualMaterial);
        internal DirectPresentation Effective(DirectPresentation root)=>new(
            new(Colors.General??root.Colors.General,Colors.Surface??Colors.General??root.Colors.Surface??root.Colors.General,
                Colors.Curve??Colors.General??root.Colors.Curve??root.Colors.General,Colors.IsVisible&&root.Colors.IsVisible),
            Layers.Concat(root.Layers).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),Material??root.Material,Visual??root.Visual);
        internal bool Same(DirectPresentation other)=>Colors==other.Colors&&Layers.SequenceEqual(other.Layers)&&Material==other.Material&&Visual==other.Visual;
    }

    private static GeometryResult AttachRepetitionStyles(GeometryResult geometry,RepetitionRecipe recipe,RepairSnapshot sourceMap,
        RepairSnapshot resultMap,IAssetStore assets,CancellationToken token)
    {
        if(recipe.Source.Source is not {} reference)return geometry;
        using var original=OcctGeometryBridge.ReadContext(reference.ContextAssetId,assets,reference.Format);
        var definition=original.GetLabel(reference.DefinitionEntry);
        if(definition.IsAssembly)throw new CadValidationException("STYLES.SOURCE: Repetition requires a simple XDE definition.");
        using var normalized=XdeDocument.Create();using var placement=definition.Location;using var placed=placement.ToTransform();using var inverse=placed.Inverted();
        XdeLabel local=definition;var sourceDocument=original;
        if(!placement.IsIdentity)
        {
            using var edit=normalized.BeginTransaction("Normalize exact repetition source");
            local=normalized.CopyTransformedDefinition(definition,inverse);edit.Commit();sourceDocument=normalized;
        }
        using var source=new RepairDocumentSession(sourceDocument,local);
        if(!VerifyHistoryRoundtrip(sourceMap,source.Source,token))
            throw new CadValidationException("STYLES.SOURCE: XDE definition and stored source BRep do not have the same complete native map.");
        using var sourceEdit=sourceDocument.BeginTransaction("Read native source membership labels");
        var root=DirectPresentation.Read(local);
        var labels=new Dictionary<int,XdeLabel>();var presentations=new Dictionary<int,DirectPresentation>();
        if(source.Source.Topology[0].Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex)
        {labels.Add(0,local);presentations.Add(0,new(new(null,null,null,true),[],null,null));}
        foreach(var slot in source.Source.Topology.Skip(1))
        {
            token.ThrowIfCancellationRequested();
            var label=source.GetOrCreateSubshapeLabel(slot.Selection);var metadata=DirectPresentation.Read(label);
            labels.Add(slot.Selection.Index,label);presentations.Add(slot.Selection.Index,metadata);
        }
        int styled=presentations.Count(p=>p.Value.Assigned);
        var rows=geometry.TopologyHistory?.Entries??[];
        var incoming=rows.Where(e=>e.ResultIndex is not null&&e.SourceKind==e.ResultKind&&
            e.Evolution is TopologyEvolution.Unchanged or TopologyEvolution.Modified or TopologyEvolution.Generated)
            .GroupBy(e=>e.ResultIndex!.Value).ToDictionary(g=>g.Key,g=>g.Select(e=>e.SourceIndex).Distinct().ToArray());
        var chosen=new Dictionary<int,int>();var conflicts=new List<int>();int shared=0;
        foreach(var slot in resultMap.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex))
        {
            token.ThrowIfCancellationRequested();
            if(!incoming.TryGetValue(slot.Selection.Index,out var contributors))continue;
            if(contributors.Any(i=>!presentations.ContainsKey(i)))throw new CadValidationException("STYLES.HISTORY: Invalid native presentation contributor.");
            var first=presentations[contributors[0]].Effective(root);
            if(contributors.Length>1||rows.Count(e=>e.ResultIndex==slot.Selection.Index)>1)shared++;
            if(contributors.Skip(1).Any(i=>!first.Same(presentations[i].Effective(root))))conflicts.Add(slot.Selection.Index);
            else chosen.Add(slot.Selection.Index,contributors[0]);
        }
        bool complete=geometry.TopologyHistory is not null;
        if(recipe.StylePolicy==RepetitionStylePolicy.VerifiedInheritance)
        {
            if(!complete)throw new CadValidationException("STYLES.HISTORY: Complete native history is unavailable. Choose whole-body presentation explicitly.");
            if(conflicts.Count>0)throw new CadValidationException("STYLES.CONFLICT: Different native contributors have incompatible presentation at result slots "+string.Join(",",conflicts)+". Choose whole-body presentation explicitly.");
            if(rows.Any(e=>e.Evolution==TopologyEvolution.Unmapped&&presentations.GetValueOrDefault(e.SourceIndex)?.Assigned==true))
                throw new CadValidationException("STYLES.HISTORY: An assigned source subshape has no native presentation correspondence.");
        }
        using var destination=XdeDocument.Create();using var resultShape=resultMap.CopyShape();XdeLabel target;
        var copied=new Dictionary<int,DirectPresentation>();
        using(var edit=destination.BeginTransaction("Native multicopy presentation"))
        {
            target=destination.AddShape(resultShape,"Repeated result");destination.CopyPresentationMetadata(local,target);
            using var result=new RepairDocumentSession(destination,target);
            if(!VerifyHistoryRoundtrip(resultMap,result.Source,token))throw new CadValidationException("STYLES.RESULT: Result XDE membership differs from the stored native result.");
            if(recipe.StylePolicy==RepetitionStylePolicy.VerifiedInheritance)
            {
                AddProvedContainerPresentation(recipe,source,labels,presentations,result,geometry.TopologyHistory!,chosen,token);
                foreach(var (index,origin) in chosen)
                {
                    token.ThrowIfCancellationRequested();if(index==0||!presentations[origin].Assigned)continue;
                    var label=result.GetOrCreateSubshapeLabel(result.Source.Select(index));
                    destination.CopyPresentationMetadata(labels[origin],label);copied.Add(index,DirectPresentation.Read(label));
                }
            }
            edit.Commit();
        }
        using var files=new KernelFiles();var path=files.PathFor("repetition.xbf");destination.Save(path);
        using(var reopened=XdeDocument.Open(path))
        {
            var persisted=reopened.GetLabel(target.Entry);using var verify=new RepairDocumentSession(reopened,persisted);
            if(!VerifyHistoryRoundtrip(resultMap,verify.Source,token)||!root.Same(DirectPresentation.Read(persisted)))
                throw new CadValidationException("STYLES.ROUNDTRIP: Result geometry or root presentation changed during native persistence.");
            using var edit=reopened.BeginTransaction("Verify persisted membership presentation");
            foreach(var (index,expected) in copied)
            {
                token.ThrowIfCancellationRequested();var actual=verify.GetOrCreateSubshapeLabel(verify.Source.Select(index));
                if(!expected.Same(DirectPresentation.Read(actual)))throw new CadValidationException("STYLES.ROUNDTRIP: A proved result presentation assignment did not survive native persistence.");
            }
        }
        IAssetLease? context=null,boundary=null;
        try
        {
            token.ThrowIfCancellationRequested();context=assets.Stage(File.ReadAllBytes(path));boundary=GeometryAssetLease.Acquire(geometry.Geometry,assets);token.ThrowIfCancellationRequested();
            var report=new RepetitionPresentationEvidence(sourceMap.Fingerprint,resultMap.Fingerprint,sourceMap.Topology.Count,resultMap.Topology.Count,
                styled,copied.Count,shared,complete,conflicts.ToImmutableArray());
            var diagnostics=recipe.StylePolicy==RepetitionStylePolicy.WholeBody?
                geometry.Diagnostics.Add(new("STYLES.WHOLE_BODY","The author explicitly selected root colors/layers/materials/visibility; subshape presentation was omitted.")):geometry.Diagnostics;
            var result=new GeometryResult(geometry.Geometry with{Source=new(context.Id,target.Entry,AssetFormatPolicy.CurrentXde)},
                new StyledGeometryLease(boundary,context),diagnostics){TopologyHistory=geometry.TopologyHistory,RepetitionPresentation=report};
            boundary=null;context=null;geometry.Dispose();return result;
        }
        finally{boundary?.Dispose();context?.Dispose();}
    }

    public Task ConfirmRepetitionPresentationAsync(FeatureDefinition feature,IAssetStore assets,CancellationToken cancellationToken=default)=>Run(()=>
    {
        var evidence=feature.RepetitionPresentation??throw new CadValidationException("Missing repetition presentation evidence.");evidence.ValidateFor(feature);
        var recipe=(RepetitionRecipe)feature.Recipe;
        using var expected=EvaluateRepetition(recipe,assets,cancellationToken);
        if(feature.TopologyHistory is {} history?!SameNativeHistoryReplay(expected.TopologyHistory,history):expected.TopologyHistory is not null)
            throw new CadValidationException("Stored repetition history differs from complete native replay.");
        var actual=expected.RepetitionPresentation;
        if(actual is null||actual with{ConflictingResultIndices=evidence.ConflictingResultIndices}!=evidence||
            !actual.ConflictingResultIndices.SequenceEqual(evidence.ConflictingResultIndices))
            throw new CadValidationException("Stored repetition presentation report differs from complete native replay.");
        using var storedShape=OcctGeometryBridge.ReadShape(feature.Result,assets);using var stored=RepairSnapshot.Create(storedShape);
        using var expectedShape=OcctGeometryBridge.ReadShape(expected.Geometry,assets);using var expectedMap=RepairSnapshot.Create(expectedShape);
        if(!VerifyHistoryRoundtrip(expectedMap,stored,cancellationToken))throw new CadValidationException("Stored repetition result differs from complete native replay.");
        CompareRepetitionContexts(expected.Geometry,feature.Result,stored,assets,cancellationToken);
        return true;
    },cancellationToken);

    private static void CompareRepetitionContexts(GeometryAssetRef expected,GeometryAssetRef stored,RepairSnapshot map,IAssetStore assets,CancellationToken token)
    {
        var a=expected.Source??throw new CadValidationException("Missing replay presentation context.");
        var b=stored.Source??throw new CadValidationException("Missing stored presentation context.");
        using var first=OcctGeometryBridge.ReadContext(a.ContextAssetId,assets,a.Format);using var second=OcctGeometryBridge.ReadContext(b.ContextAssetId,assets,b.Format);
        var left=first.GetLabel(a.DefinitionEntry);var right=second.GetLabel(b.DefinitionEntry);
        using var leftMap=new RepairDocumentSession(first,left);using var rightMap=new RepairDocumentSession(second,right);
        if(!VerifyHistoryRoundtrip(map,leftMap.Source,token)||!VerifyHistoryRoundtrip(map,rightMap.Source,token)||
            !DirectPresentation.Read(left).Same(DirectPresentation.Read(right)))throw new CadValidationException("Stored XDE root geometry/presentation differs from native replay.");
        using var leftEdit=first.BeginTransaction("Read replay membership");using var rightEdit=second.BeginTransaction("Read stored membership");
        foreach(var slot in map.Topology.Skip(1))
        {
            token.ThrowIfCancellationRequested();
            var leftLabel=leftMap.GetOrCreateSubshapeLabel(leftMap.Source.Select(slot.Selection.Index));
            var rightLabel=rightMap.GetOrCreateSubshapeLabel(rightMap.Source.Select(slot.Selection.Index));
            if(!DirectPresentation.Read(leftLabel).Same(DirectPresentation.Read(rightLabel))||rightLabel.ValidationProperties.HasAny)
                throw new CadValidationException("Stored repetition subshape presentation differs from native replay or retains invalid source validation attributes.");
        }
        if(right.ValidationProperties.HasAny)throw new CadValidationException("Repeated geometry retains invalid source validation properties.");
    }
}
