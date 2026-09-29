using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

/// <summary>Atomic postprocessing inside the initiating edit's history entry. Undo restores exact cached results.</summary>
public static class UpdateAssociatedSections
{
    public static async Task<PreparedDocumentEdit> PrepareCommandAsync(ICadDocumentCommand command,DocumentCommandContext context,CancellationToken token)
    {
        var original=await command.PrepareAsync(context,token).ConfigureAwait(false);
        PreparedDocumentEdit? reviewed=null;
        try
        {
            reviewed=await PrepareAsync(context,original.Snapshot,token).ConfigureAwait(false);
            var drawing=await UpdateTechnicalDrawings.PrepareAsync(context,reviewed.Snapshot,token).ConfigureAwait(false);
            return new(drawing.Snapshot,[original,reviewed,drawing]);
        }
        catch{reviewed?.Dispose();original.Dispose();throw;}
    }
    public static async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,DocumentSnapshot next,CancellationToken token)
    {
        if(next.AssociatedSections.Count==0||next.StateId==context.Snapshot.StateId)return new(next);
        var resources=new List<IDisposable>();
        try
        {
            next=next with{AssociatedSections=next.AssociatedSections.RemoveRange(next.AssociatedSections.Keys.Where(id=>!next.Features.ContainsKey(id)))};
            EngineeringReviewValidation.Validate(next);
            var occurrences=next.EnumerateOccurrences().ToDictionary(o=>o.Path);
            foreach(var section in next.AssociatedSections.Values)
            {
                token.ThrowIfCancellationRequested();
                var feature=next.Features[section.FeatureId];
                if(!next.Bodies.TryGetValue(feature.OutputBodyId,out var output))
                    throw new CadValidationException("Detach the associated section before consuming its body.");
                if(context.Snapshot.AssociatedSections.TryGetValue(feature.Id,out var oldSection)&&context.Snapshot.Features.TryGetValue(feature.Id,out var old)&&
                    old.Result!=feature.Result&&oldSection==section)throw new CadValidationException("Detach the associated section before changing its geometry.");
                var inputs=new List<GeometryInstance>();string? reason=null;
                foreach(var source in section.Sources)
                {
                    if(!occurrences.TryGetValue(source.Path,out var occurrence)) {reason="Source occurrence is missing. Reselect explicitly or detach.";break;}
                    GeometryAssetRef geometry;
                    if(source.FeatureId is {} producer)
                    {
                        if(!next.Features.TryGetValue(producer,out var f)||f.OutputBodyId!=source.BodyId||f.PartId!=occurrence.DefinitionId||f.IsStale)
                        {reason="Source feature is missing or stale. Reselect explicitly or detach.";break;}
                        geometry=f.Result;
                    }
                    else
                    {
                        if(!next.Bodies.TryGetValue(source.BodyId,out var b)||b.PartId!=occurrence.DefinitionId||b.Producer is not null)
                        {reason="Source body identity is missing. Reselect explicitly or detach.";break;}
                        geometry=b.Geometry;
                    }
                    if(geometry.Kind==BodyKind.Empty){reason="Source feature has an empty result. Previous section retained.";break;}
                    inputs.Add(new(source.Path,source.BodyId,geometry,occurrence.WorldTransform));
                }
                bool changed=reason is not null||section.StaleReason is not null||inputs.Where((i,index)=>
                    i.Geometry.AssetId!=section.Sources[index].Asset||i.Geometry.Revision!=section.Sources[index].Revision||
                    i.WorldTransform!=section.Sources[index].Transform).Any();
                if(!changed)continue;
                if(reason is null&&next.Layers[output.LayerId].IsLocked)reason="Section layer is locked. Unlock to refresh.";
                if(reason is null)
                {
                    var review=context.Kernel as IGeometryReviewKernel??throw new NotSupportedException("Section kernel unavailable.");
                    var plane=new CuttingPlane(section.Normal,section.OffsetMm);
                    var result=await (section.Output==SectionOutput.Faces?review.SectionFacesAsync(inputs,plane,context.Assets,token):
                        review.SectionAsync(inputs,plane,context.Assets,token)).ConfigureAwait(false);
                    resources.Add(result);token.ThrowIfCancellationRequested();
                    if(result.Geometry.Kind==BodyKind.Empty)reason="The current plane no longer intersects the source. Previous section retained.";
                    else
                    {
                        feature=feature with{Recipe=new ImportedRecipe(result.Geometry),Result=result.Geometry,IsStale=false,TopologyHistory=null};
                        next=next with{Features=next.Features.SetItem(feature.Id,feature),Bodies=next.Bodies.SetItem(output.Id,output with{Geometry=result.Geometry}),
                            AssociatedSections=next.AssociatedSections.SetItem(feature.Id,section with{StaleReason=null,
                                Sources=[..inputs.Select((i,index)=>section.Sources[index] with{Revision=i.Geometry.Revision,Asset=i.Geometry.AssetId,Transform=i.WorldTransform})]})};
                    }
                }
                if(reason is not null)next=next with{Features=next.Features.SetItem(feature.Id,feature with{IsStale=true,TopologyHistory=null}),
                    AssociatedSections=next.AssociatedSections.SetItem(feature.Id,section with{StaleReason=reason})};
            }
            return new(next,resources);
        }
        catch{foreach(var r in resources)r.Dispose();throw;}
    }
}

public sealed class EditAssociatedSectionCommand(FeatureId id,Vector3d normal,double offset,SectionOutput output,bool detach=false) : ICadDocumentCommand
{
    public string Name=>"Edit section association";
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();var doc=context.Snapshot;var section=doc.AssociatedSections[id];
        var f=doc.Features[id];
        if(doc.Bodies.TryGetValue(f.OutputBodyId,out var body)&&doc.Layers[body.LayerId].IsLocked)throw new CadValidationException("Section layer is locked.");
        new CuttingPlane(normal,offset).Validate();
        return Task.FromResult(new PreparedDocumentEdit((doc with{
            AssociatedSections=detach?doc.AssociatedSections.Remove(id):doc.AssociatedSections.SetItem(id,section with{Normal=normal,OffsetMm=offset,Output=output,StaleReason="Refresh requested"}),
            Features=detach?doc.Features.SetItem(id,f with{IsStale=false}):doc.Features}).WithNewState()));
    }
}

public sealed class EditDimensionCommand(EngineeringDimension dimension,bool remove=false) : ICadDocumentCommand
{
    public string Name=>remove?"Delete engineering dimension":"Edit engineering dimension";
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();dimension.Validate();
        return Task.FromResult(new PreparedDocumentEdit((context.Snapshot with{Dimensions=remove?
            context.Snapshot.Dimensions.Remove(dimension.Id):context.Snapshot.Dimensions.SetItem(dimension.Id,dimension)}).WithNewState()));
    }
}
