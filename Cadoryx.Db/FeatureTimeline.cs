using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>A session-only boundary in an explicitly ordered part history; Position counts chronological entries.</summary>
public sealed record FeatureCursor(DocumentStateId State,DefinitionId Part,int Position);

public static class FeatureTimeline
{
    public static void ValidateOrder(DocumentSnapshot doc,DefinitionId partId,ImmutableArray<FeatureId> order)
    {
        if(doc.Definitions.GetValueOrDefault(partId) is not PartDefinition part||order.IsDefault||
            order.Length!=part.Features.Length||order.Distinct().Count()!=order.Length||!order.ToHashSet().SetEquals(part.Features))
            throw FeatureTimelineFailure.Create("HistoryInvalidOrder","History order must contain every feature of the same part exactly once.");
        var prerequisites=DatumPlanes.FeatureDependencies(doc);var seen=new HashSet<FeatureId>();
        foreach(var id in order)
        {
            foreach(var dependency in prerequisites[id])
                if(doc.Features[dependency].PartId==partId&&!seen.Contains(dependency))
                    throw FeatureTimelineFailure.Create("HistoryDependencyFormat",$"Feature '{doc.Features[id].Name}' must follow '{doc.Features[dependency].Name}'.",doc.Features[id].Name,doc.Features[dependency].Name);
            seen.Add(id);
        }
    }

    public static DocumentSnapshot Preview(DocumentSnapshot doc,FeatureCursor cursor)
    {
        if(cursor.State!=doc.StateId||doc.Definitions.GetValueOrDefault(cursor.Part) is not PartDefinition part||
            cursor.Position<0||cursor.Position>part.Features.Length)
            throw FeatureTimelineFailure.Create("HistoryCursorStale","History cursor is stale, foreign or outside the part history.");
        ValidateOrder(doc,part.Id,part.Features);
        var available=new HashSet<FeatureId>();
        var dependencies=DatumPlanes.FeatureDependencies(doc);
        foreach(var id in part.Features.Take(cursor.Position))
            if(!doc.Features[id].IsStale&&!doc.Features[id].IsSuppressed&&
                dependencies[id].All(dep=>doc.Features[dep].PartId!=part.Id||available.Contains(dep)))available.Add(id);
        var consumed=available.SelectMany(id=>doc.Features[id].Inputs).ToHashSet();
        return ReplaceOutputs(doc,part,available.Except(consumed),temporary:true);
    }

    internal static DocumentSnapshot ReplaceOutputs(DocumentSnapshot doc,PartDefinition part,IEnumerable<FeatureId> outputs,bool temporary)
    {
        var bodies=doc.Bodies.RemoveRange(part.Bodies.Where(id=>doc.Bodies[id].Producer is not null));
        var ids=part.Bodies.Where(id=>doc.Bodies[id].Producer is null).ToImmutableArray();
        foreach(var id in outputs)
        {
            var f=doc.Features[id];if(f.Result.Kind==BodyKind.Empty)continue;
            var old=doc.Bodies.GetValueOrDefault(f.OutputBodyId);
            var metadata=old is not null?BodyOutputMetadata.FromBody(old):f.OutputMetadata??
                new(f.Name,doc.Layers.Keys.OrderBy(l=>l.Value).First(),new(),true,null);
            bodies=bodies.SetItem(f.OutputBodyId,new(f.OutputBodyId,part.Id,metadata.Name,f.Result,f.Id,metadata.Layer,metadata.Appearance,metadata.Visible,metadata.Material));
            ids=ids.Add(f.OutputBodyId);
        }
        return doc with{Bodies=bodies,Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=ids}),
            Dimensions=temporary?ImmutableDictionary<Guid,EngineeringDimension>.Empty:doc.Dimensions,
            IsTemporaryHistoryView=temporary};
    }

    /// <summary>Insert an explicitly authored new branch; existing downstream operands and topology identities stay exact.</summary>
    public static DocumentSnapshot CompleteInsertion(DocumentSnapshot original,DocumentSnapshot candidate,FeatureCursor cursor)
    {
        var view=Preview(original,cursor);var part=(PartDefinition)original.Definitions[cursor.Part];
        if(candidate.Id!=original.Id||!original.Features.Keys.All(candidate.Features.ContainsKey)||
            candidate.Definitions.GetValueOrDefault(part.Id) is not PartDefinition staged)
            throw FeatureTimelineFailure.Create("HistoryPreserveGraph","Cursor authoring must preserve the existing part and feature graph.");
        var added=candidate.Features.Keys.Except(original.Features.Keys).ToHashSet();
        if(added.Count==0||added.Any(id=>candidate.Features[id].PartId!=part.Id)||
            !staged.Features.Where(id=>!added.Contains(id)).SequenceEqual(part.Features))
            throw FeatureTimelineFailure.Create("HistoryNewFeaturesOnly","Cursor authoring must add new features to the selected part without changing the old order.");
        foreach(var f in original.Features.Values)
            if((candidate.Features[f.Id] with{OutputMetadata=f.OutputMetadata})!=f||
                candidate.Features[f.Id].OutputMetadata!=f.OutputMetadata&&
                (view.Bodies.GetValueOrDefault(f.OutputBodyId) is not {} visible||candidate.Features[f.Id].OutputMetadata!=BodyOutputMetadata.FromBody(visible)))
                throw FeatureTimelineFailure.Create("HistoryEditRequiresEnd","Cursor insertion cannot rewrite an existing feature. Return to the end to edit it.");
        if(original.Parameters!=candidate.Parameters||original.Datums!=candidate.Datums||original.AssemblyPatterns!=candidate.AssemblyPatterns||
            original.Sketches.Count!=candidate.Sketches.Count||original.Sketches.Any(p=>candidate.Sketches.GetValueOrDefault(p.Key)!=p.Value)||original.Settings!=candidate.Settings)
            throw FeatureTimelineFailure.Create("HistoryPreserveAuthorData","Cursor insertion cannot change existing author data.");
        var authorState=candidate with{StateId=original.StateId,Definitions=original.Definitions,Bodies=original.Bodies,
            Features=original.Features,Dimensions=original.Dimensions,DrawingSheets=original.DrawingSheets,IsTemporaryHistoryView=original.IsTemporaryHistoryView};
        if(authorState!=original||candidate.Dimensions!=original.Dimensions&&candidate.Dimensions.Count!=0||
            !DrawingAuthorsPreserved(original.DrawingSheets,candidate.DrawingSheets))
            throw FeatureTimelineFailure.Create("HistoryPreserveAuthorData","Cursor insertion cannot change document, review or drawing author data.");
        var outputIds=added.Select(id=>candidate.Features[id].OutputBodyId).ToHashSet();
        foreach(var body in candidate.Bodies.Values)
            if(!outputIds.Contains(body.Id)&&body!=original.Bodies.GetValueOrDefault(body.Id)&&body!=view.Bodies.GetValueOrDefault(body.Id))
                throw FeatureTimelineFailure.Create("HistoryPreserveAuthorData","Cursor insertion cannot rewrite existing body data.");
        foreach(var body in original.Bodies.Values.Where(b=>b.PartId!=part.Id||b.Producer is null))
            if(candidate.Bodies.GetValueOrDefault(body.Id)!=body)
                throw FeatureTimelineFailure.Create("HistoryPreserveAuthorData","Cursor insertion must preserve other and unauthored bodies.");
        foreach(var definition in original.Definitions.Values)
            if(definition.Id!=part.Id&&candidate.Definitions.GetValueOrDefault(definition.Id)!=definition)
                throw FeatureTimelineFailure.Create("HistoryPreserveDefinitions","Cursor insertion cannot change other definitions.");
        if(candidate.Definitions.Count!=original.Definitions.Count||part.Name!=staged.Name)
            throw FeatureTimelineFailure.Create("HistoryPreserveDefinitions","Cursor insertion cannot replace or rename definitions.");
        var inserted=staged.Features.Where(added.Contains).ToImmutableArray();
        var order=part.Features.Take(cursor.Position).Concat(inserted).Concat(part.Features.Skip(cursor.Position)).ToImmutableArray();
        candidate=candidate with{Definitions=candidate.Definitions.SetItem(part.Id,staged with{Features=order}),
            Features=candidate.Features.SetItems(original.Features),IsTemporaryHistoryView=false};
        ValidateOrder(candidate,part.Id,order);
        var current=order.Where(id=>!candidate.Features[id].IsSuppressed&&!candidate.Features[id].IsStale).ToHashSet();
        var consumed=current.SelectMany(id=>candidate.Features[id].Inputs).ToHashSet();
        // Preserve frozen terminal results already shown by the original document.
        var outputs=current.Except(consumed).Concat(original.Bodies.Values.Where(b=>b.PartId==part.Id&&b.Producer is {} id&&candidate.Features[id].IsStale).Select(b=>b.Producer!.Value)).Distinct();
        var retained=original.Bodies.Values.Where(b=>b.PartId==part.Id&&b.Producer is {} id&&part.Features.IndexOf(id)>=cursor.Position).ToArray();
        staged=(PartDefinition)candidate.Definitions[part.Id];
        candidate=candidate with{Bodies=candidate.Bodies.SetItems(retained.Select(b=>new KeyValuePair<BodyId,CadBody>(b.Id,b))),
            Definitions=candidate.Definitions.SetItem(part.Id,staged with{Bodies=staged.Bodies.AddRange(retained.Select(b=>b.Id).Except(staged.Bodies))})};
        return ReplaceOutputs(candidate,(PartDefinition)candidate.Definitions[part.Id],outputs,temporary:false) with{Dimensions=original.Dimensions};
    }

    private static bool DrawingAuthorsPreserved(ImmutableDictionary<Guid,TechnicalDrawingSheet> original,
        ImmutableDictionary<Guid,TechnicalDrawingSheet> candidate)
    {
        if(original.Count!=candidate.Count)return false;
        foreach(var sheet in original.Values)
        {
            if(!candidate.TryGetValue(sheet.Id,out var next)||next with{Views=sheet.Views,Dimensions=sheet.Dimensions,
                    DatumSymbols=sheet.DatumSymbols,ControlFrames=sheet.ControlFrames,Balloons=sheet.Balloons,HoleTables=sheet.HoleTables}!=sheet||
                next.Views.Length!=sheet.Views.Length||next.Dimensions.Length!=sheet.Dimensions.Length||
                next.DatumSymbols.Length!=sheet.DatumSymbols.Length||next.ControlFrames.Length!=sheet.ControlFrames.Length||
                next.Balloons.Length!=sheet.Balloons.Length||next.HoleTables.Length!=sheet.HoleTables.Length)return false;
            for(int i=0;i<sheet.Views.Length;i++)
                if(!next.Views[i].Sources.SequenceEqual(sheet.Views[i].Sources)||
                    next.Views[i] with{Sources=sheet.Views[i].Sources,Strokes=sheet.Views[i].Strokes,StaleReason=sheet.Views[i].StaleReason}!=sheet.Views[i])return false;
            for(int i=0;i<sheet.Dimensions.Length;i++)
                if(next.Dimensions[i] with{Value=sheet.Dimensions[i].Value,StaleReason=sheet.Dimensions[i].StaleReason}!=sheet.Dimensions[i])return false;
            for(int i=0;i<sheet.DatumSymbols.Length;i++)
            {
                var before=sheet.DatumSymbols[i];var after=next.DatumSymbols[i];
                if(before.Source.AuthorSelection()!=after.Source.AuthorSelection()||!SameSizeAuthor(before.SizeEvidence,after.SizeEvidence)||
                    after with{Source=before.Source,SizeEvidence=before.SizeEvidence,StaleReason=before.StaleReason}!=before)return false;
            }
            for(int i=0;i<sheet.ControlFrames.Length;i++)
            {
                var before=sheet.ControlFrames[i];var after=next.ControlFrames[i];
                if(before.Source.AuthorSelection()!=after.Source.AuthorSelection()||!before.Datums.SequenceEqual(after.Datums)||before.DatumBindings.Length!=after.DatumBindings.Length||
                    before.DatumBindings.Where((b,j)=>b.Id!=after.DatumBindings[j].Id||b.FeatureType!=after.DatumBindings[j].FeatureType||b.Source.AuthorSelection()!=after.DatumBindings[j].Source.AuthorSelection()).Any()||before.DatumSystem.Length!=after.DatumSystem.Length||
                    before.DatumSystem.Where((group,index)=>!group.SameReferences(after.DatumSystem[index])).Any()||
                    !SameSizeAuthor(before.SizeEvidence,after.SizeEvidence)||after with{Source=before.Source,Datums=before.Datums,DatumSystem=before.DatumSystem,DatumBindings=before.DatumBindings,
                        SizeEvidence=before.SizeEvidence,StaleReason=before.StaleReason}!=before)return false;
            }
            for(int i=0;i<sheet.Balloons.Length;i++)
            {
                var before=sheet.Balloons[i];
                if(next.Balloons[i].Source.AuthorSelection()!=before.Source.AuthorSelection()||next.Balloons[i] with{Source=before.Source,Quantity=before.Quantity,PartName=before.PartName,StaleReason=before.StaleReason}!=before)return false;
            }
            for(int i=0;i<sheet.HoleTables.Length;i++)
            {
                var before=sheet.HoleTables[i];var after=next.HoleTables[i];
                if(after.Rows.Length!=before.Rows.Length||after with{Rows=before.Rows,StaleReason=before.StaleReason,
                    CoordinateRevisions=before.CoordinateRevisions,LocalFrame=before.CoordinateBinding is null?after.LocalFrame:before.LocalFrame}!=before)return false;
                for(int j=0;j<before.Rows.Length;j++)
                {
                    var old=before.Rows[j];var current=after.Rows[j];
                    if(current.Evidence.Source.AuthorSelection()!=old.Evidence.Source.AuthorSelection()||
                        current with{Evidence=old.Evidence,CoordinatesMm=old.CoordinatesMm,StaleReason=old.StaleReason}!=old)return false;
                }
            }
        }
        return true;
        static bool SameSizeAuthor(DrawingCylinderSizeEvidence? a,DrawingCylinderSizeEvidence? b)=>a is null?b is null:b is not null&&a.Source.AuthorSelection()==b.Source.AuthorSelection();
    }
}
