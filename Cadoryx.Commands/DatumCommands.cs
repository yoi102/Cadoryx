using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

/// <summary>Edits the entire association table as one candidate. Detach retains the last applied frame.</summary>
public sealed class SetDatumsCommand:IParameterEditCommand
{
    private readonly Func<DocumentCommandContext,CancellationToken,Task<DatumBook>> resolve;
    public SetDatumsCommand(DatumBook book):this((_,_)=>Task.FromResult(book)) { }
    /// <summary>Resolve exact native sources against the guarded command capture, before atomic recompute.</summary>
    public SetDatumsCommand(Func<DocumentCommandContext,CancellationToken,Task<DatumBook>> resolve)=>this.resolve=resolve;
    public string Name=>Cadoryx.Lang.Strings.Strings.DatumPlanes;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var book=await resolve(context,token).ConfigureAwait(false);
        return await ParameterRecompute.PrepareAsync(context,context.Snapshot with{Datums=book},token).ConfigureAwait(false);
    }
}

internal static class DatumRecompute
{
    public static void Guard(DocumentSnapshot before,DocumentSnapshot after)
    {
        foreach(var old in before.Datums.Planes.Values)
            if(after.Datums.Planes.TryGetValue(old.Id,out var plane)&&plane.PartId!=old.PartId)
                throw new CadValidationException("Datum ownership cannot change. Create a new datum explicitly.");
        foreach(var old in before.Datums.Axes.Values)
            if(after.Datums.Axes.TryGetValue(old.Id,out var axis)&&axis.PartId!=old.PartId)throw new CadValidationException("Datum axis ownership cannot change.");
        foreach(var old in before.Datums.Points.Values)
            if(after.Datums.Points.TryGetValue(old.Id,out var point)&&point.PartId!=old.PartId)throw new CadValidationException("Datum point ownership cannot change.");
        if(before.Datums.Planes.Keys.Any(id=>after.Datums.Axes.ContainsKey(id)||after.Datums.Points.ContainsKey(id))||
            before.Datums.Axes.Keys.Any(id=>after.Datums.Planes.ContainsKey(id)||after.Datums.Points.ContainsKey(id))||
            before.Datums.Points.Keys.Any(id=>after.Datums.Planes.ContainsKey(id)||after.Datums.Axes.ContainsKey(id)))
            throw new CadValidationException("Changing datum type requires a new identity.");
        foreach(var old in before.Datums.SketchBindings.Values)
            if(after.Datums.SketchBindings.TryGetValue(old.Sketch,out var binding)&&binding==old&&
                after.Sketches.TryGetValue(old.Sketch,out var sketch)&&!DatumPlanes.SameFrame(sketch.Plane,before.Sketches[old.Sketch].Plane))
                throw new CadValidationException("Sketch plane is driven by a datum. Edit the datum or explicitly detach first.");
        if(before.Datums.WorkPlane is {} wp&&after.Datums.WorkPlane==wp&&after.Settings.WorkPlane!=before.Settings.WorkPlane)
            throw new CadValidationException("Workplane is driven by a datum. Edit the datum or explicitly detach first.");
    }
    public static DocumentSnapshot Refresh(DocumentSnapshot doc,HashSet<SketchId> changed)
    {
        doc=RefreshPlanes(doc);
        foreach(var binding in doc.Datums.SketchBindings.Values)
        {
            var plane=doc.Datums.Planes[binding.Datum];var sketch=doc.Sketches[binding.Sketch];
            if(!plane.IsStale&&(binding.Revision!=plane.Revision||!DatumPlanes.SameFrame(sketch.Plane,plane.Frame)))
            {
                doc=doc with{Sketches=doc.Sketches.SetItem(sketch.Id,sketch with{Plane=plane.Frame}),
                    Datums=doc.Datums with{SketchBindings=doc.Datums.SketchBindings.SetItem(sketch.Id,binding with{Revision=plane.Revision})}};
                changed.Add(sketch.Id);
            }
        }
        if(doc.Datums.WorkPlane is {} w&&!doc.Datums.Planes[w.Datum].IsStale)
        {
            var occurrence=DatumPlanes.Occurrence(doc,w);var plane=doc.Datums.Planes[w.Datum];var frame=occurrence.WorldTransform*plane.Frame;
            var settings=new DocumentWorkPlaneSettings(DocumentWorkPlaneKind.Custom){CustomOrigin=frame.Translation,CustomRotation=frame.Rotation};settings.Validate();
            if(w.Revision!=plane.Revision||w.OccurrenceTransform!=occurrence.WorldTransform||doc.Settings.WorkPlane!=settings)
                doc=doc with{Settings=doc.Settings with{WorkPlane=settings},Datums=doc.Datums with{WorkPlane=w with{Revision=plane.Revision,OccurrenceTransform=occurrence.WorldTransform}}};
        }
        return doc;
    }
    public static DocumentSnapshot RefreshPlanes(DocumentSnapshot doc)
    {
        foreach(var id in DatumPlanes.Order(doc))
        {
            try
            {
                if(doc.Datums.Planes.TryGetValue(id,out var plane))
                {
                    var(frame,revisions)=DatumPlanes.Resolve(doc,plane);
                    if(plane.IsStale||!DatumPlanes.SameFrame(frame,plane.Frame)||!revisions.SequenceEqual(plane.SourceRevisions))
                        doc=doc with{Datums=doc.Datums with{Planes=doc.Datums.Planes.SetItem(id,plane with{Frame=frame,SourceRevisions=revisions,Revision=Guid.NewGuid(),IsStale=false})}};
                }
                else if(doc.Datums.Axes.TryGetValue(id,out var axis))
                {
                    var(frame,revisions)=DatumConstructions.Resolve(doc,axis);
                    if(axis.IsStale||!DatumPlanes.SameFrame(frame,axis.Frame)||!revisions.SequenceEqual(axis.SourceRevisions))
                        doc=doc with{Datums=doc.Datums with{Axes=doc.Datums.Axes.SetItem(id,axis with{Frame=frame,SourceRevisions=revisions,Revision=Guid.NewGuid(),IsStale=false})}};
                }
                else
                {
                    var point=doc.Datums.Points[id];var(position,revisions)=DatumConstructions.Resolve(doc,point);
                    if(point.IsStale||(position-point.Position).Length>1e-9||!revisions.SequenceEqual(point.SourceRevisions))
                        doc=doc with{Datums=doc.Datums with{Points=doc.Datums.Points.SetItem(id,point with{Position=position,SourceRevisions=revisions,Revision=Guid.NewGuid(),IsStale=false})}};
                }
            }
            catch(DatumSourceUnavailableException)
            {
                if(doc.Datums.Planes.TryGetValue(id,out var p))doc=doc with{Datums=doc.Datums with{Planes=doc.Datums.Planes.SetItem(id,p with{IsStale=true})}};
                else if(doc.Datums.Axes.TryGetValue(id,out var a))doc=doc with{Datums=doc.Datums with{Axes=doc.Datums.Axes.SetItem(id,a with{IsStale=true})}};
                else doc=doc with{Datums=doc.Datums with{Points=doc.Datums.Points.SetItem(id,doc.Datums.Points[id] with{IsStale=true})}};
            }
        }
        return doc;
    }
    public static async Task ConfirmAsync(DocumentCommandContext context,DocumentSnapshot doc,CancellationToken token)
    {
        var evidence=DatumConstructions.ExactSources(doc.Datums).ToArray();if(evidence.Length==0)return;
        if(context.Kernel is not IDatumGeometryResolver resolver)throw new NotSupportedException("Kernel has no exact datum geometry resolver.");
        foreach(var source in evidence)await resolver.ConfirmDatumGeometryAsync(source,context.Assets,token).ConfigureAwait(false);
    }
    public static DocumentSnapshot RefreshAuthoredRevisions(DocumentSnapshot before,DocumentSnapshot next)
    {
        foreach(var axis in next.Datums.Axes.Values)
            if(before.Datums.Axes.TryGetValue(axis.Id,out var old)&&(axis with{Frame=old.Frame,Revision=old.Revision,SourceRevisions=old.SourceRevisions,IsStale=old.IsStale})!=old)
                next=next with{Datums=next.Datums with{Axes=next.Datums.Axes.SetItem(axis.Id,axis with{Revision=Guid.NewGuid()})}};
        foreach(var point in next.Datums.Points.Values)
            if(before.Datums.Points.TryGetValue(point.Id,out var old)&&(point with{Position=old.Position,Revision=old.Revision,SourceRevisions=old.SourceRevisions,IsStale=old.IsStale})!=old)
                next=next with{Datums=next.Datums with{Points=next.Datums.Points.SetItem(point.Id,point with{Revision=Guid.NewGuid()})}};
        return next;
    }
}
