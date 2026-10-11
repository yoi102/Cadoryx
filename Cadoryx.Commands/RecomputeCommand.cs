using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using System.Collections.Immutable;
namespace Cadoryx.Commands;

/// <summary>Reevaluates the changed feature and its dependent closure into an isolated candidate.</summary>
public sealed class RecomputeCommand(FeatureId featureId,GeometryRecipe recipe,SketchProfileReference? replacementSketchSource=null) : ICadDocumentCommand
{
    public string Name=>Strings.EditFeatureParameters;
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        var doc=context.Snapshot;var original=doc.Features[featureId];
        if(FeatureSuppression.Blocked(doc).Contains(featureId))
            throw new CadValidationException("Restore the suppressed feature chain before editing its parameters.");
        if(original.Recipe.GetType()!=recipe.GetType())throw new CadValidationException("Changing feature kind requires a new identity.");
        if(recipe is SolidOffsetRecipe offset&&(original.Inputs.Length!=1||offset.Source!=doc.Features[original.Inputs[0]].Result))
            throw new CadValidationException("Solid offset editing retains its exact whole source identity. Inspect the current source again.");
        if(recipe is ContourLocalRecipe contour)
        {
            contour.Validate();
            if(original.Inputs.Length!=1||contour.Programs[0].Seed.FeatureId!=original.Inputs[0]||contour.Source!=doc.Features[original.Inputs[0]].Result)
                throw new CadValidationException("Contour editing retains its exact whole source identity. Reselect a changed source explicitly.");
        }
        if(recipe is RevolveRecipe {Axis:{Datum:not null} axis})
        {
            var expected=axis.Refresh(doc,original.PartId);
            if(expected.DatumRevision!=axis.DatumRevision||!axis.SameLineValues(expected))
                throw new CadValidationException("Edit or detach the driven revolution axis first.");
        }
        if(original.Recipe is ExtrudeRecipe previousExtrusion&&recipe is ExtrudeRecipe changedExtrusion)
        {
            if((previousExtrusion.Options?.Operation??ExtrusionOperation.NewBody)!=(changedExtrusion.Options?.Operation??ExtrusionOperation.NewBody))
                throw new CadValidationException("Changing extrusion operation requires a new feature identity.");
            if(previousExtrusion.Options is {Datum:not null} oldOptions&&changedExtrusion.Options is {} changedOptions&&oldOptions.Datum==changedOptions.Datum&&
                oldOptions.LimitPlane is {} a&&changedOptions.LimitPlane is {} b&&!DatumPlanes.SameFrame(a,b))
                throw new CadValidationException("Edit or detach the driven extrusion limit datum first.");
        }
        if(original.Recipe is HoleRecipe oldHole&&recipe is HoleRecipe hole&&oldHole.Datum is not null&&
            oldHole.Datum==hole.Datum&&!DatumPlanes.SameFrame(oldHole.Frame,hole.Frame))
            throw new CadValidationException("Hole frame is driven by a datum; edit or explicitly detach its datum first.");
        if(original.Recipe is RepetitionRecipe previous&&recipe is RepetitionRecipe repetition)
        {
            repetition.Validate();
            if(previous.Kind!=repetition.Kind||previous.IncludeSource!=repetition.IncludeSource||
                !previous.CopyIds.Take(Math.Min(previous.Count,repetition.Count)).SequenceEqual(repetition.CopyIds.Take(Math.Min(previous.Count,repetition.Count))))
                throw new CadValidationException("Changing repetition kind or surviving copy identity requires a new feature.");
            if(previous.Datum is not null&&previous.Datum==repetition.Datum&&!DatumPlanes.SameFrame(previous.Frame,repetition.Frame))
                throw new CadValidationException("Repetition frame is driven by a datum; edit or explicitly detach its datum first.");
        }
        if(original.Recipe is HolePatternRecipe oldPattern&&recipe is HolePatternRecipe pattern)
        {
            pattern.Validate();var oldLayout=oldPattern.Layout;var current=pattern.Layout;
            if(oldPattern.Seed!=pattern.Seed||oldPattern.Template!=pattern.Template||oldLayout.Kind!=current.Kind||oldLayout.IncludeSource!=current.IncludeSource||
                !oldLayout.CopyIds.Take(Math.Min(oldLayout.Count,current.Count)).SequenceEqual(current.CopyIds.Take(Math.Min(oldLayout.Count,current.Count))))
                throw new CadValidationException("Edit the seed hole; changing replay source, kind or surviving identity requires a new feature.");
            if(oldLayout.Datum is not null&&oldLayout.Datum==current.Datum&&!DatumPlanes.SameFrame(oldLayout.Frame,current.Frame))
                throw new CadValidationException("Replay layout is datum-driven; edit or explicitly detach the datum first.");
        }
        if(original.Recipe is ExtrusionPatternRecipe oldExtrusionPattern&&recipe is ExtrusionPatternRecipe extrusionPattern)
        {
            extrusionPattern.Validate();var before=oldExtrusionPattern.Layout;var after=extrusionPattern.Layout;
            if(oldExtrusionPattern.Seed!=extrusionPattern.Seed||!oldExtrusionPattern.SameTemplate(extrusionPattern.Template)||before.Kind!=after.Kind||before.IncludeSource!=after.IncludeSource||
                !before.CopyIds.Take(Math.Min(before.Count,after.Count)).SequenceEqual(after.CopyIds.Take(Math.Min(before.Count,after.Count))))
                throw new CadValidationException("Edit the extrusion seed; changing replay source, kind or surviving identity requires a new feature.");
            if(before.Datum is not null&&before.Datum==after.Datum&&!DatumPlanes.SameFrame(before.Frame,after.Frame))
                throw new CadValidationException("Replay layout is datum-driven; edit or explicitly detach the datum first.");
        }
        if(original.Recipe is DraftRecipe oldDraft&&recipe is DraftRecipe changedDraft)
            foreach(var p in changedDraft.Programs)
                if(oldDraft.Programs.FirstOrDefault(o=>o.Id==p.Id) is {Datum:not null} earlier&&earlier.Datum==p.Datum&&!DatumPlanes.SameFrame(earlier.NeutralPlane,p.NeutralPlane))
                    throw new CadValidationException("Draft neutral plane is driven by a datum; edit or explicitly detach its datum first.");
        if(replacementSketchSource is not null)
        {
            if(original.SketchSource is null||recipe is not (ExtrudeRecipe or RevolveRecipe))
                throw new CadValidationException("Only an existing linked extrusion or revolution can change sketch source.");
            replacementSketchSource.Resolve(doc,original.PartId,recipe);
        }
        return FeatureRecompute.PrepareAsync(context,[featureId],new Dictionary<FeatureId,GeometryRecipe>{{featureId,recipe}},cancellationToken,
            replacementSketchSource is null?null:new Dictionary<FeatureId,SketchProfileReference>{{featureId,replacementSketchSource}});
    }
}

/// <summary>Suppresses a feature or restores it and recomputes its dependent chain.</summary>
public sealed class SetFeatureSuppressionCommand(FeatureId featureId,bool suppressed) : ICadDocumentCommand
{
    public string Name=>Strings.ResourceManager.GetString(suppressed?"SuppressFeature":"RestoreFeature",Strings.Culture)!;
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        if(!context.Snapshot.Features.TryGetValue(featureId,out var feature))
            throw new CadValidationException("Feature no longer exists.");
        if(feature.IsSuppressed==suppressed)
            return Task.FromResult(new PreparedDocumentEdit(context.Snapshot,[]));
        var staged=context.Snapshot with
        {
            Features=context.Snapshot.Features.SetItem(featureId,feature with{IsSuppressed=suppressed})
        };
        return FeatureRecompute.PrepareAsync(context with{Snapshot=staged},[featureId],null,cancellationToken,
            reconcileOutputs:true);
    }
}

internal static class FeatureRecompute
{
    internal static async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,IEnumerable<FeatureId> roots,
        IReadOnlyDictionary<FeatureId,GeometryRecipe>? replacements,CancellationToken cancellationToken,
        IReadOnlyDictionary<FeatureId,SketchProfileReference>? sketchSourceReplacements=null,bool reconcileOutputs=false,
        DocumentSnapshot? geometryBaseline=null)
    {
        var doc=context.Snapshot;geometryBaseline??=doc;var pending=roots.ToHashSet();var authorRoots=pending.ToHashSet();
        var unchangedGeometry=new HashSet<FeatureId>();
        if(replacements is not null)foreach(var (id,recipe) in replacements)doc=doc with{Features=doc.Features.SetItem(id,doc.Features[id] with{Recipe=recipe})};
        var dependencies=DatumPlanes.FeatureDependencies(doc);
        await DatumRecompute.ConfirmAsync(context,doc,cancellationToken).ConfigureAwait(false);
        bool added;
        do {added=false;foreach(var f in doc.Features.Values)if(dependencies[f.Id].Any(pending.Contains))added|=pending.Add(f.Id);}while(added);
        if(pending.Any(id=>doc.Features[id].IsSuppressed&&doc.AssociatedSections.ContainsKey(id)))
            throw new CadValidationException("Detach the associated section before suppressing its feature.");
        if(pending.Any(id=>doc.Bodies.TryGetValue(doc.Features[id].OutputBodyId,out var body)?doc.Layers[body.LayerId].IsLocked:
            doc.Features[id].OutputMetadata is {} metadata&&doc.Layers[metadata.Layer].IsLocked))
            throw new CadValidationException(Strings.LayerLocked);
        var done=new HashSet<FeatureId>();var resources=new List<IDisposable>();
        var confirmedSketches=new HashSet<SketchId>();
        try
        {
            while(done.Count<pending.Count)
            {
                var f=doc.Features.Values.FirstOrDefault(f=>pending.Contains(f.Id)&&!done.Contains(f.Id)&&dependencies[f.Id].All(x=>!pending.Contains(x)||done.Contains(x)))
                    ??throw new CadValidationException("Cyclic recompute graph.");
                cancellationToken.ThrowIfCancellationRequested();
                void Freeze()
                {
                    doc=doc with{Features=doc.Features.SetItem(f.Id,f with{IsStale=true,TopologyHistory=null,RepetitionPresentation=null})};
                    done.Add(f.Id);
                }
                if(f.IsSuppressed||f.Inputs.Any(input=>doc.Features[input].IsStale)){Freeze();continue;}
                if(!doc.Datums.IsEmpty)doc=DatumRecompute.RefreshPlanes(doc);
                if(DatumPlanes.HasStaleInput(doc,f.Id)){Freeze();continue;}
                GeometryRecipe current=replacements?.GetValueOrDefault(f.Id)??f.Recipe;
                var sketchSource=sketchSourceReplacements?.GetValueOrDefault(f.Id)??f.SketchSource;
                if(sketchSource?.HasUnavailableEntities(doc)==true||GuidedAuthoringSources.HasUnavailableEntities(doc,current))
                {Freeze();continue;}
                foreach(var id in GuidedAuthoringSources.Sketches(current).Concat(sketchSource is {} selected?[selected.SketchId]:[]))
                    if(confirmedSketches.Add(id))await SketchNativeProjectionCommands.ConfirmAsync(context with{Snapshot=doc},doc.Sketches[id],cancellationToken).ConfigureAwait(false);
                current=GuidedAuthoringSources.Refresh(doc,f.PartId,current);
                if(current is RevolveRecipe revolve)current=revolve.Refresh(doc,f.PartId);
                if(sketchSource is not null)
                {
                    current=sketchSource.Resolve(doc,f.PartId,current,refresh:true);
                    sketchSource=sketchSource with{Revision=doc.Sketches[sketchSource.SketchId].Revision};
                }
                if(current is BooleanRecipe boolean && f.Inputs.Length==boolean.Inputs.Length)
                    current=boolean with {Inputs=f.Inputs.Select(id=>doc.Features[id].Result).ToImmutableArray()};
                if(current is TransformRecipe transform && f.Inputs.Length==1)
                    current=transform with {Source=doc.Features[f.Inputs[0]].Result};
                if(current is SolidOffsetRecipe offset&&f.Inputs.Length==1)
                    current=offset with{Source=doc.Features[f.Inputs[0]].Result};
                if(current is RepetitionRecipe repetition&&f.Inputs.Length==1)
                    current=(repetition with{Source=doc.Features[f.Inputs[0]].Result}).Refresh(doc,f.PartId);
                if(current is HoleRecipe hole&&f.Inputs.Length==1)
                    current=(hole with{Source=doc.Features[f.Inputs[0]].Result}).Refresh(doc,f.PartId);
                if(current is HolePatternRecipe pattern)current=pattern.Refresh(doc,f.PartId);
                if(current is ExtrusionPatternRecipe extrusionPattern)current=extrusionPattern.Refresh(doc,f.PartId);
                if(current is ContourLocalRecipe contourRecipe&&!contourRecipe.HasCurrentSource(doc)){Freeze();continue;}
                if(current is ShellRecipe or DraftRecipe or PlanarPushPullRecipe or DefeatureRecipe)
                {
                    if(!FaceFeatureRecipes.HasCurrentSource(doc,current)){Freeze();continue;}
                    if(current is DraftRecipe draft)current=draft.Refresh(doc,f.PartId);
                    await FaceFeatureCommandGuard.ConfirmAsync(context with{Snapshot=doc},current,cancellationToken);
                }
                if(current is ExtrudeRecipe {Options:{} extrusionOptions} extrusion)
                {
                    if(!extrusionOptions.HasCurrentFace(doc)){Freeze();continue;}
                    if(f.Inputs.Length==1)extrusionOptions=extrusionOptions with{Source=doc.Features[f.Inputs[0]].Result};
                    current=extrusion with{Options=extrusionOptions.Refresh(doc,f.PartId)};
                    if(extrusionOptions.LimitFace is {} face)await AddExtrusionCommand.ConfirmLimitAsync(context with{Snapshot=doc},face,cancellationToken);
                    if(extrusionOptions.SupportFace is {} support)await AddExtrusionCommand.ConfirmLimitAsync(context with{Snapshot=doc},support,cancellationToken);
                }
                if(current is LocalFeatureRecipe local&&f.Inputs.Length==1)
                    current=local with{Source=doc.Features[f.Inputs[0]].Result,Box=doc.Features[f.Inputs[0]].Recipe as BoxRecipe??throw new CadValidationException("Local feature requires a box source.")};
                if(current is HistoryFilletRecipe or HistoryChamferRecipe)
                {
                    var binding=f.TopologyBinding??throw new CadValidationException("Bound local feature has no selection.");
                    if(f.Inputs.Length!=1||f.Inputs[0]!=binding.TargetFeatureId)throw new CadValidationException("Bound local feature dependency changed.");
                    var upstream=doc.Features[binding.TargetFeatureId];
                    if(upstream.Result.Revision!=binding.TargetRevision||upstream.Result.AssetId!=binding.TargetAsset)
                    {Freeze();continue;}
                    if(binding.Origin is {} origin)
                        await FeatureBindingGuard.ConfirmAsync(context with{Snapshot=doc},origin,
                            binding.TargetFeatureId,new(binding.TargetRevision,binding.TargetAsset,binding.FullTopologyIndex,HistoryShapeKind.Edge,binding.AdapterVersion),cancellationToken);
                    else await FeatureBindingGuard.ConfirmExactAsync(context with{Snapshot=doc},binding.ExactEdge!,binding.SupportFace,cancellationToken);
                    if(binding.SupportFace is {} face&&binding.Origin is not null)
                        await FeatureBindingGuard.WithFaceAsync(context with{Snapshot=doc},binding,face,cancellationToken);
                    current=current switch
                    {
                        HistoryFilletRecipe fillet=>fillet with{Source=upstream.Result,FullTopologyIndex=binding.FullTopologyIndex},
                        HistoryChamferRecipe chamfer=>chamfer with{Source=upstream.Result,FullTopologyIndex=binding.FullTopologyIndex,
                            SupportFaceIndex=binding.SupportFace!.FullTopologyIndex},
                        _=>current
                    };
                }
                var before=geometryBaseline.Features.GetValueOrDefault(f.Id);
                bool symbolicChange=before is not null&&SameSymbolicHoleGeometry(before.Recipe,current);
                bool unchangedDependent=!authorRoots.Contains(f.Id)&&dependencies[f.Id].Where(pending.Contains).Any()&&
                    dependencies[f.Id].Where(pending.Contains).All(unchangedGeometry.Contains)&&before?.Recipe==current;
                if(before is {IsStale:false,IsSuppressed:false}&&f.Result==before.Result&&sketchSource==before.SketchSource&&
                    (symbolicChange||unchangedDependent))
                {
                    // Symbolic intent adds no BRep geometry. Keep the exact original shape/history;
                    // rebuilding an equivalent shape would invalidate its original attachments.
                    current.Validate();doc=doc with{Features=doc.Features.SetItem(f.Id,f with{Recipe=current})};
                    unchangedGeometry.Add(f.Id);done.Add(f.Id);continue;
                }
                var result=await context.Kernel.EvaluateForDocumentAsync(current,context.Assets,cancellationToken).ConfigureAwait(false);
                resources.Add(result);
                doc=doc with {Features=doc.Features.SetItem(f.Id,f with {Recipe=current,Result=result.Geometry,SketchSource=sketchSource,TopologyHistory=result.TopologyHistory,RepetitionPresentation=result.RepetitionPresentation,GuidedModelingReport=result.GuidedModelingReport,IsStale=false})};
                var part=(PartDefinition)doc.Definitions[f.PartId];
                bool terminal=!doc.Features.Values.Any(other=>other.Inputs.Contains(f.Id));
                if(doc.Bodies.TryGetValue(f.OutputBodyId,out var body))
                {
                    doc=doc with{Features=doc.Features.SetItem(f.Id,doc.Features[f.Id] with{OutputMetadata=BodyOutputMetadata.FromBody(body)})};
                    if(result.Geometry.Kind==BodyKind.Empty)
                        doc=doc with {Bodies=doc.Bodies.Remove(body.Id),Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Remove(body.Id)})};
                    else doc=doc with {Bodies=doc.Bodies.SetItem(body.Id,body with {Geometry=result.Geometry})};
                }
                else if(terminal&&result.Geometry.Kind!=BodyKind.Empty)
                {
                    var metadata=f.OutputMetadata??new(f.Name,doc.Layers.Keys.OrderBy(x=>x.Value).First(),new(),true,null);
                    var restored=new CadBody(f.OutputBodyId,f.PartId,metadata.Name,result.Geometry,f.Id,metadata.Layer,metadata.Appearance,metadata.Visible,metadata.Material);
                    doc=doc with {Bodies=doc.Bodies.Add(restored.Id,restored),Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Add(restored.Id)})};
                }
                done.Add(f.Id);
            }
            if(reconcileOutputs||pending.Any(id=>doc.Features[id].IsSuppressed))
            {
                var touched=pending.ToHashSet();
                do
                {
                    added=false;
                    foreach(var id in touched.ToArray())
                        foreach(var input in doc.Features[id].Inputs)added|=touched.Add(input);
                }while(added);
                doc=ReconcileFeatureBodies(doc,touched);
            }
            if(pending.Count>0&&!doc.Datums.IsEmpty)doc=DatumRecompute.Refresh(doc,new HashSet<SketchId>());
            return new(doc.WithNewState(),resources);
        }
        catch{foreach(var resource in resources)resource.Dispose();throw;}
    }

    private static bool SameSymbolicHoleGeometry(GeometryRecipe before,GeometryRecipe after)=> (before,after) switch
    {
        (HoleRecipe a,HoleRecipe b)=>a.Thread?.Representation!=ThreadHoleRepresentation.Modeled&&b.Thread?.Representation!=ThreadHoleRepresentation.Modeled&&
            a.Thread!=b.Thread&&(a with{Thread=null})==(b with{Thread=null}),
        (HolePatternRecipe a,HolePatternRecipe b)=>a.Template.Thread?.Representation!=ThreadHoleRepresentation.Modeled&&b.Template.Thread?.Representation!=ThreadHoleRepresentation.Modeled&&
            a.Template.Thread!=b.Template.Thread&&
            (a with{Template=a.Template with{Thread=null}})==(b with{Template=b.Template with{Thread=null}}),
        _=>false
    };

    private static DocumentSnapshot ReconcileFeatureBodies(DocumentSnapshot document,HashSet<FeatureId> touched)
    {
        foreach(var partId in touched.Select(id=>document.Features[id].PartId).Distinct())
        {
            var part=(PartDefinition)document.Definitions[partId];
            foreach(var featureId in part.Features.Where(touched.Contains))
            {
                var feature=document.Features[featureId];
                bool active=!feature.IsSuppressed&&!feature.IsStale&&feature.Result.Kind!=BodyKind.Empty;
                bool hasActiveConsumer=document.Features.Values.Any(other=>other.PartId==partId&&
                    !other.IsSuppressed&&!other.IsStale&&other.Inputs.Contains(featureId));
                bool show=active&&!hasActiveConsumer;
                if(document.Bodies.TryGetValue(feature.OutputBodyId,out var body))
                {
                    document=document with{Features=document.Features.SetItem(featureId,feature with
                        {OutputMetadata=BodyOutputMetadata.FromBody(body)})};
                    if(!show)
                    {
                        document=document with{Bodies=document.Bodies.Remove(body.Id),
                            Definitions=document.Definitions.SetItem(partId,part with{Bodies=part.Bodies.Remove(body.Id)})};
                        part=(PartDefinition)document.Definitions[partId];
                    }
                }
                else if(show)
                {
                    var metadata=feature.OutputMetadata??new BodyOutputMetadata(feature.Name,
                        document.Layers.Keys.OrderBy(x=>x.Value).First(),new(),true,null);
                    var restored=new CadBody(feature.OutputBodyId,partId,metadata.Name,feature.Result,
                        feature.Id,metadata.Layer,metadata.Appearance,metadata.Visible,metadata.Material);
                    document=document with{Bodies=document.Bodies.Add(restored.Id,restored),
                        Definitions=document.Definitions.SetItem(partId,part with{Bodies=part.Bodies.Add(restored.Id)})};
                    part=(PartDefinition)document.Definitions[partId];
                }
            }
        }
        return document;
    }
}
