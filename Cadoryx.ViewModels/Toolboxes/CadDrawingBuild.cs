using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.ViewModels.Toolboxes;

internal static partial class CadDrawingAuthoring
{
    private static void GuardOwnerType(object old,object author,string action)
    {
        var prior=Author(old);
        if(prior.GetType()!=author.GetType())throw new ArgumentException("An edit must preserve the drawing object type.");
        Guid? Owner(object value)=>value switch{DrawingViewAuthor v=>v.ParentView,DrawingDimensionAuthor d=>d.ViewId,
            DrawingDatumAuthor d=>d.ViewId,DrawingControlAuthor f=>f.ViewId,DrawingBalloonAuthor b=>b.ViewId,DrawingHoleTableAuthor t=>t.ViewId,_=>null};
        if(Owner(prior)!=Owner(author))throw new ArgumentException("An edit must preserve the drawing owner or parent view.");
        if(prior is DrawingViewAuthor p&&author is DrawingViewAuthor v&&(p.Kind!=v.Kind||action=="edit"&&v.SourcePath is not null))
            throw new ArgumentException("View edits preserve kind and selected sources. Use reselect explicitly.");
        if(prior is DrawingDimensionAuthor pd&&author is DrawingDimensionAuthor nd&&pd.Kind!=nd.Kind||
            prior is DrawingDatumAuthor pa&&author is DrawingDatumAuthor na&&pa.FeatureType!=na.FeatureType||
            prior is DrawingControlAuthor pf&&author is DrawingControlAuthor nf&&pf.Target!=nf.Target)
            throw new ArgumentException("An edit must preserve the dimension or manufacturing feature type.");
        if(action=="reselect"&&prior is not (DrawingViewAuthor or DrawingDimensionAuthor or DrawingDatumAuthor or DrawingControlAuthor or DrawingBalloonAuthor or DrawingHoleTableAuthor))
            throw new ArgumentException("This object has no attachment to reselect.");
        if(action!="edit")return;
        bool Same(DrawingSelectionAuthor? a,DrawingSelectionAuthor? b)=>a==b;
        if(prior is DrawingDimensionAuthor d&&author is DrawingDimensionAuthor e&&(!Same(d.First,e.First)||!Same(d.Second,e.Second))||
            prior is DrawingDatumAuthor a&&author is DrawingDatumAuthor b&&(!Same(a.Source,b.Source)||!Same(a.Opposite,b.Opposite)||a.InternalWidth!=b.InternalWidth)||
            prior is DrawingControlAuthor f&&author is DrawingControlAuthor g&&(!Same(f.Source,g.Source)||!Same(f.Opposite,g.Opposite)||f.InternalWidth!=g.InternalWidth)||
            prior is DrawingBalloonAuthor balloon&&author is DrawingBalloonAuthor other&&!Same(balloon.Source,other.Source))
            throw new ArgumentException("Attachment changes require explicit reselect.");
        if(prior is DrawingHoleTableAuthor table&&author is DrawingHoleTableAuthor next)
        {
            if(table.CoordinatePath!=next.CoordinatePath||table.Coordinates?.Binding!=next.Coordinates?.Binding)
                throw new ArgumentException("Coordinate association changes require explicit reselect or detach_coordinates.");
            foreach(var row in next.Rows.Where(r=>r.Id is not null))
                if(table.Rows.SingleOrDefault(r=>r.Id==row.Id)?.Source!=row.Source)
                    throw new ArgumentException("An existing hole row source requires explicit reselect.");
        }
    }
    private static async Task<ICadDocumentCommand> BuildAsync(DocumentCommandContext context,TechnicalDrawingSheet? sheet,
        Guid id,object author,object? old,string action,CancellationToken token)
    {
        var doc=context.Snapshot;bool replace=old is not null;
        async Task<DrawingTopologyReference> Source(DrawingSelectionAuthor input,DrawingTopologyReference? previous=null)
        {
            var path=ValidateSelection(doc,input);
            // Keep the immutable author origin only for an exact emitted selection. The production
            // command subsequently reproves the full original history chain and current anchor.
            if(previous is not null&&Selection(previous)==input)return previous;
            var resolver=context.Kernel as IDrawingTopologyResolver??throw new NotSupportedException("Drawing attachment resolver unavailable.");
            return await resolver.ResolveDrawingTopologyAsync(doc,path,new(input.BodyId),input.Index,input.Fingerprint,context.Assets,token);
        }
        async Task<DrawingPlanarSizeEvidence?> Planar(DrawingTopologyReference source,DrawingSelectionAuthor? opposite,bool internalWidth,DrawingTopologyReference? previous)
        {
            if(opposite is null)throw new ArgumentException("A median-plane feature requires both exact face selections.");
            var second=await Source(opposite,previous);
            var resolver=context.Kernel as IDrawingPlanarSizeResolver??throw new NotSupportedException("Planar size resolver unavailable.");
            return await resolver.ResolveDrawingPlanarSizeAsync(doc,source,second,internalWidth,context.Assets,token);
        }
        switch(author)
        {
            case DrawingSheetAuthor a:
            {
                var template=a.Template;
                if(!replace&&template is not null)template=template with{CustomFields=[..template.CustomFields.Select(f=>f with{Id=Guid.NewGuid()})],Notes=[..template.Notes.Select(n=>n with{Id=Guid.NewGuid()})]};
                if(old is TechnicalDrawingSheet prior&&template is not null&&prior.Template is {} existing&&
                    (!template.CustomFields.Select(f=>f.Id).Order().SequenceEqual(existing.CustomFields.Select(f=>f.Id).Order())||
                        !template.Notes.Select(n=>n.Id).Order().SequenceEqual(existing.Notes.Select(n=>n.Id).Order())))
                    throw new ArgumentException("Use field/note create/delete actions to change sheet content identities.");
                return replace?TechnicalDrawingCommands.EditSheet(id,a.Name,a.WidthMm,a.HeightMm,a.Unit,a.Title,a.Author,a.Standard,template,template is null):
                    TechnicalDrawingCommands.AddSheet(new(id,a.Name,a.WidthMm,a.HeightMm,a.Unit,[],[],a.Title,a.Author){Standard=a.Standard,Template=template});
            }
            case DrawingViewAuthor a:
            {
                if(a.Kind==DrawingViewKind.Detail&&a.SourcePath is not null)throw new ArgumentException("Detail sources are selected by their exact parent.");
                if(!replace)return TechnicalDrawingCommands.AddView(sheet!.Id,id,a.Name,a.Kind,a.CenterMm,a.Scale,
                    a.SourcePath is null?null:Path(doc,a.SourcePath),a.ParentView,a.SectionNormal,a.SectionOffsetMm,a.DetailCenter,a.DetailRadius,a.Hatching);
                var edit=TechnicalDrawingCommands.EditViewAuthor(sheet!.Id,id,a.Name,a.CenterMm,a.Scale,a.SectionNormal,a.SectionOffsetMm,a.DetailCenter,a.DetailRadius,a.Hatching);
                return action=="reselect"?new DrawingSequenceCommand(edit,TechnicalDrawingCommands.ReselectView(sheet.Id,id,a.SourcePath is null?null:Path(doc,a.SourcePath))):edit;
            }
            case DrawingDimensionAuthor a:
            {
                if(a.Kind is DrawingMeasureKind.Radius or DrawingMeasureKind.Diameter&&a.Second is not null)
                    throw new ArgumentException("A radius or diameter has one selected datum.");
                var resolver=context.Kernel as IAssemblyDatumResolver??throw new NotSupportedException("Analytic drawing dimension resolver unavailable.");
                async Task<AssemblyDatumReference> Datum(DrawingSelectionAuthor s)=>await resolver.ResolveAssemblyDatumAsync(doc,
                    ValidateSelection(doc,s),new(s.BodyId),s.Index,s.Fingerprint,context.Assets,token);
                var value=new TechnicalDrawingDimension(id,a.ViewId,a.Kind,await Datum(a.First),a.Second is {} second?await Datum(second):null,a.TextPositionMm,0,a.Argb)
                    {UpperTolerance=a.UpperTolerance,LowerTolerance=a.LowerTolerance};
                return replace?TechnicalDrawingCommands.ReselectDimension(sheet!.Id,value):TechnicalDrawingCommands.AddDimension(sheet!.Id,value);
            }
            case DrawingDatumAuthor a:
            {
                if(a.FeatureType!=DrawingDatumFeatureType.PlanarSizeMedianPlane&&(a.Opposite is not null||a.InternalWidth))throw new ArgumentException("Only a planar size datum accepts a second face.");
                var prior=old as DrawingDatumSymbol;var source=await Source(a.Source,prior?.Source);
                var value=new DrawingDatumSymbol(id,a.ViewId,a.Name,source,a.PositionMm){FeatureType=a.FeatureType,
                    PlanarSizeEvidence=a.FeatureType==DrawingDatumFeatureType.PlanarSizeMedianPlane?await Planar(source,a.Opposite,a.InternalWidth,prior?.PlanarSizeEvidence?.OppositeSource):null};
                return DrawingAnnotationCommands.PutDatum(sheet!.Id,value,replace);
            }
            case DrawingControlAuthor a:
            {
                if(a.Target!=DrawingToleranceTarget.MedianPlane&&(a.Opposite is not null||a.InternalWidth))throw new ArgumentException("Only a median-plane target accepts a second face.");
                var prior=old as DrawingFeatureControlFrame;var source=await Source(a.Source,prior?.Source);
                var value=new DrawingFeatureControlFrame(id,a.ViewId,source,a.PositionMm,a.Characteristic,a.ToleranceMm,a.Target,a.DiameterZone,a.MaterialCondition)
                {DatumSystem=a.DatumSystem.IsDefault?[]:a.DatumSystem,
                    DatumBindings=action=="edit"&&prior is not null?prior.DatumBindings:[],
                    PlanarSizeEvidence=a.Target==DrawingToleranceTarget.MedianPlane?await Planar(source,a.Opposite,a.InternalWidth,prior?.PlanarSizeEvidence?.OppositeSource):null};
                // A new datum system is author data. Existing feature bindings remain guarded until explicit reselect.
                if(action=="edit"&&prior is not null&&!value.ReferencedDatumIds.SequenceEqual(prior.ReferencedDatumIds))value=value with{DatumBindings=[]};
                return DrawingAnnotationCommands.PutControlFrame(sheet!.Id,value,replace);
            }
            case DrawingBalloonAuthor a:return DrawingAnnotationCommands.PutBalloon(sheet!.Id,
                new(id,a.ViewId,await Source(a.Source,(old as DrawingAssemblyBalloon)?.Source),a.PositionMm,a.ItemNumber),replace);
            case DrawingHoleTableAuthor a:
            {
                var prior=old as DrawingHoleTable;var path=Path(doc,a.CoordinatePath);var occurrence=doc.EnumerateOccurrences().Single(o=>o.Path.Equals(path));
                RigidTransform3d frame;ImmutableArray<Guid> revisions=[];
                if(a.Coordinates is {} coordinates)
                {
                    if(a.Frame is not null)throw new ArgumentException("Associated coordinates accept identities and revisions, never a cached frame.");
                    (frame,revisions)=coordinates.Binding.Resolve(doc,occurrence.DefinitionId);
                    if(coordinates.Revisions.IsDefault||!revisions.SequenceEqual(coordinates.Revisions))throw new ArgumentException("Coordinate datums changed. Inspect their exact revisions.");
                }
                else
                {
                    if(a.Frame is null)throw new ArgumentException("Independent hole coordinates require an explicit frame.");
                    frame=Frame(a.Frame,prior?.LocalFrame);
                    if(prior?.CoordinateBinding is not null&&action=="edit")throw new ArgumentException("Use detach_coordinates before authoring an independent frame.");
                }
                if(a.Rows.IsDefault||a.Rows.Length>32)throw new ArgumentException("A hole table supports up to 32 explicit rows.");
                var resolver=context.Kernel as IDrawingHoleResolver??throw new NotSupportedException("Current hole resolver unavailable.");
                var rows=ImmutableArray.CreateBuilder<DrawingHoleRow>();
                foreach(var row in a.Rows)
                {
                    DrawingHoleRow? previous=null;
                    if(row.Id is {} rowId)
                    {previous=prior?.Rows.SingleOrDefault(r=>r.Id==rowId)??throw new ArgumentException("Row identities must come from this exact existing table.");}
                    var evidence=await resolver.ResolveDrawingHoleAsync(doc,await Source(row.Source,previous?.Evidence.Source),context.Assets,token);
                    rows.Add(new(row.Id??Guid.NewGuid(),row.Number,evidence,new(0,0)));
                }
                return DrawingHoleTableCommands.Put(sheet!.Id,new(id,a.ViewId,a.Name,path,occurrence.DefinitionId,frame,a.PositionMm)
                    {Rows=rows.ToImmutable(),CoordinateBinding=a.Coordinates?.Binding,CoordinateRevisions=revisions},replace);
            }
            case DrawingFieldAuthor a:return DrawingTemplateCommands.PutField(sheet!.Id,new(id,a.Caption,a.Value,a.PositionMm,a.WidthMm,a.HeightMm),replace);
            case DrawingNoteAuthor a:return DrawingTemplateCommands.PutNote(sheet!.Id,new(id,a.Text,a.PositionMm,a.WidthMm),replace);
            default:throw new ArgumentException("Unsupported drawing author.");
        }
    }
    private static RigidTransform3d Frame(DrawingFrameAuthor author,RigidTransform3d? previous=null)
    {
        if(previous is {} p&&author==Frame(p))return p;
        return DatumConstructions.Frame(author.Origin,author.Normal,author.U);
    }
    private static DrawingFrameAuthor Frame(RigidTransform3d frame)=>new(frame.Translation,frame.Rotation.Rotate(Vector3d.UnitZ),frame.Rotation.Rotate(new(1,0,0)));
    private static OccurrencePath Path(DocumentSnapshot doc,string text)
    {
        var tokens=text.Split('/');
        if(tokens.Length is <2 or >129||!Guid.TryParseExact(tokens[0],"D",out var document)||document!=doc.Id.Value)
            throw new ArgumentException("A full path in this exact document is required.");
        var slots=tokens.Skip(1).Select(t=>Guid.TryParseExact(t,"D",out var id)&&id!=Guid.Empty?new ComponentSlotId(id):throw new ArgumentException("Invalid exact occurrence slot."));
        var path=new OccurrencePath(doc.Id,slots);
        if(!doc.EnumerateOccurrences().Any(o=>o.Path.Equals(path)))throw new ArgumentException("Exact occurrence path is missing.");return path;
    }
    private static OccurrencePath ValidateSelection(DocumentSnapshot doc,DrawingSelectionAuthor author)
    {
        var path=Path(doc,author.Path);var occurrence=doc.EnumerateOccurrences().Single(o=>o.Path.Equals(path));
        if(author.Index is <0 or >100000||author.Fingerprint is not {Length:64}||!author.Fingerprint.All(Uri.IsHexDigit)||
            doc.Definitions[occurrence.DefinitionId] is not PartDefinition part||!part.Bodies.Contains(new(author.BodyId))||
            !doc.Bodies.TryGetValue(new(author.BodyId),out var body)||body.Geometry.Revision.Value!=author.Revision||body.Geometry.AssetId.ToString()!=author.Asset||
            body.Producer is {} producer&&(doc.Features[producer].IsStale||doc.Features[producer].IsSuppressed))
            throw new ArgumentException("Exact attachment body/revision/asset/index is foreign, blocked or changed.");return path;
    }
    private sealed class DrawingSequenceCommand(params ICadDocumentCommand[] commands):ICadDocumentCommand
    {
        public string Name=>"Reselect complete drawing view";
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            var prepared=new List<PreparedDocumentEdit>();var next=context.Snapshot;
            try
            {
                foreach(var command in commands){var edit=await command.PrepareAsync(context with{Snapshot=next},token);prepared.Add(edit);next=edit.Snapshot;}
                return new(next,prepared);
            }
            catch{foreach(var edit in prepared)edit.Dispose();throw;}
        }
    }
}
