using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

public static class TechnicalDrawingCommands
{
    public static ICadDocumentCommand AddSheet(TechnicalDrawingSheet sheet)=>
        new EditDocumentCommand("Create drawing sheet",document=>
        {
            sheet.Validate(document.Id);
            if(document.DrawingSheets.ContainsKey(sheet.Id))throw new CadValidationException("Drawing sheet ID already exists.");
            return document with{DrawingSheets=document.DrawingSheets.Add(sheet.Id,sheet)};
        });

    public static ICadDocumentCommand EditSheet(Guid id,string name,double widthMm,double heightMm,
        LengthUnit unit,string title,string? author,DrawingStandard? standard=null)=>new EditDocumentCommand("Edit drawing sheet",document=>
    {
        var current=document.DrawingSheets[id];var updated=current with
            {Name=name,WidthMm=widthMm,HeightMm=heightMm,Unit=unit,Title=title,Author=author,
                Standard=standard??current.Standard};
        updated.Validate(document.Id);
        return document with{DrawingSheets=document.DrawingSheets.SetItem(id,updated)};
    });

    public static ICadDocumentCommand DeleteSheet(Guid id)=>new EditDocumentCommand("Delete drawing sheet",document=>
        document with{DrawingSheets=document.DrawingSheets.Remove(id)});

    public static ICadDocumentCommand AddView(Guid sheetId,Guid viewId,string name,DrawingViewKind kind,
        Point2d centerMm,double scale,OccurrencePath? sourcePath=null,Guid? parentView=null,
        Vector3d? sectionNormal=null,double sectionOffsetMm=0,Point2d? detailCenter=null,double detailRadius=0)=>
        new AddDrawingViewCommand(sheetId,viewId,name,kind,centerMm,scale,sourcePath,parentView,sectionNormal,sectionOffsetMm,
            detailCenter,detailRadius);

    public static ICadDocumentCommand EditView(Guid sheetId,Guid viewId,Point2d centerMm,double scale)=>
        new EditDocumentCommand("Edit drawing view",document=>
        {
            var sheet=document.DrawingSheets[sheetId];int index=sheet.Views.FindIndex(v=>v.Id==viewId);
            if(index<0)throw new CadValidationException("Drawing view is missing.");
            var old=sheet.Views[index];
            if(!double.IsFinite(scale)||scale is <.0001 or >10000||
               !double.IsFinite(centerMm.X)||!double.IsFinite(centerMm.Y))
                throw new CadValidationException("Invalid drawing view placement.");
            var views=sheet.Views.ToBuilder();
            views[index]=old with{CenterMm=centerMm,Scale=scale};
            void MoveChildren(Guid parent,Point2d previous,Point2d next,double ratio)
            {
                for(int i=0;i<views.Count;i++)
                {
                    var child=views[i];if(child.ParentView!=parent)continue;
                    var translated=new Point2d(next.X+(child.CenterMm.X-previous.X)*ratio,
                        next.Y+(child.CenterMm.Y-previous.Y)*ratio);
                    var resized=child.Scale*ratio;
                    views[i]=child with{CenterMm=translated,Scale=resized};
                    MoveChildren(child.Id,child.CenterMm,translated,ratio);
                }
            }
            MoveChildren(viewId,old.CenterMm,centerMm,scale/old.Scale);
            return document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,
                sheet with{Views=views.ToImmutable()})};
        });

    public static ICadDocumentCommand ReselectView(Guid sheetId,Guid viewId,OccurrencePath? sourcePath)=>
        new ReselectDrawingViewCommand(sheetId,viewId,sourcePath);

    public static ICadDocumentCommand DeleteView(Guid sheetId,Guid viewId)=>
        new EditDocumentCommand("Delete drawing view",document=>
        {
            var sheet=document.DrawingSheets[sheetId];
            if(sheet.Views.Any(v=>v.ParentView==viewId))throw new CadValidationException("Delete dependent views first.");
            return document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,sheet with
            {Views=[..sheet.Views.Where(v=>v.Id!=viewId)],Dimensions=[..sheet.Dimensions.Where(d=>d.ViewId!=viewId)]})};
        });

    public static ICadDocumentCommand AddDimension(Guid sheetId,TechnicalDrawingDimension dimension)=>
        new AddDrawingDimensionCommand(sheetId,dimension);

    public static ICadDocumentCommand ReselectDimension(Guid sheetId,TechnicalDrawingDimension dimension)=>
        new AddDrawingDimensionCommand(sheetId,dimension,true);

    public static ICadDocumentCommand DeleteDimension(Guid sheetId,Guid dimensionId)=>
        new EditDocumentCommand("Delete drawing dimension",document=>
        {
            var sheet=document.DrawingSheets[sheetId];
            return document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,sheet with
                {Dimensions=[..sheet.Dimensions.Where(d=>d.Id!=dimensionId)]})};
        });

    public static ICadDocumentCommand MoveDimension(Guid sheetId,Guid dimensionId,Point2d textPosition,uint? argb=null)=>
        new EditDocumentCommand("Move drawing dimension",document=>
        {
            var sheet=document.DrawingSheets[sheetId];
            var index=sheet.Dimensions.FindIndex(d=>d.Id==dimensionId);
            if(index<0)throw new CadValidationException("Drawing dimension is missing.");
            return document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,sheet with
                {Dimensions=sheet.Dimensions.SetItem(index,sheet.Dimensions[index] with
                    {TextPositionMm=textPosition,Argb=argb??sheet.Dimensions[index].Argb})})};
        });

    public static ICadDocumentCommand SetDimensionTolerance(Guid sheetId,Guid dimensionId,double upper,double lower)=>
        new EditDocumentCommand("Edit drawing tolerance",document=>
        {
            var sheet=document.DrawingSheets[sheetId];
            var index=sheet.Dimensions.FindIndex(d=>d.Id==dimensionId);
            if(index<0)throw new CadValidationException("Drawing dimension is missing.");
            var dimension=sheet.Dimensions[index] with{UpperTolerance=upper,LowerTolerance=lower};
            dimension.Validate(document.Id);
            return document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,sheet with
                {Dimensions=sheet.Dimensions.SetItem(index,dimension)})};
        });

    public static ICadDocumentCommand Refresh()=>new EditDocumentCommand("Refresh drawing views",document=>
        document with{DrawingSheets=document.DrawingSheets.ToImmutableDictionary(pair=>pair.Key,pair=>pair.Value with
            {Views=[..pair.Value.Views.Select(v=>v with{StaleReason="Refresh requested"})]})});

    private sealed class ReselectDrawingViewCommand(Guid sheetId,Guid viewId,OccurrencePath? path):ICadDocumentCommand
    {
        public string Name=>"Reselect drawing view source";
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            var doc=context.Snapshot;var sheet=doc.DrawingSheets[sheetId];
            var index=sheet.Views.FindIndex(v=>v.Id==viewId);
            if(index<0)throw new CadValidationException("Drawing view is missing.");
            var existing=sheet.Views[index];var inputs=DrawingInputs.Capture(doc,path);
            if(existing.Kind==DrawingViewKind.Detail)
                throw new CadValidationException("Reselect the parent projection of a detail view.");
            var kernel=context.Kernel as ITechnicalDrawingKernel??throw new NotSupportedException("Drawing kernel unavailable.");
            var projection=await kernel.ProjectAsync(inputs,existing.Kind,existing.SectionNormal,
                existing.SectionOffsetMm,context.Assets,token).ConfigureAwait(false);
            var view=existing with{Sources=[..inputs.Select(i=>DrawingInputs.Source(doc,i))],
                Strokes=projection.Strokes,StaleReason=null};
            var dimensions=sheet.Dimensions.Select(d=>d.ViewId==viewId?d with
                {StaleReason="View source changed; reselect dimension datums."}:d).ToImmutableArray();
            return new((doc with{DrawingSheets=doc.DrawingSheets.SetItem(sheetId,sheet with
                {Views=sheet.Views.SetItem(index,view),Dimensions=dimensions})}).WithNewState());
        }
    }

    private sealed class AddDrawingViewCommand(Guid sheetId,Guid viewId,string name,DrawingViewKind kind,
        Point2d center,double scale,OccurrencePath? sourcePath,Guid? parentView,
        Vector3d? normal,double offset,Point2d? detailCenter,double detailRadius):ICadDocumentCommand
    {
        public string Name=>"Create drawing view";
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            var document=context.Snapshot;var sheet=document.DrawingSheets[sheetId];
            if(sheet.Views.Any(v=>v.Id==viewId))throw new CadValidationException("Drawing view ID already exists.");
            TechnicalDrawingView view;
            if(kind==DrawingViewKind.Detail)
            {
                var parent=sheet.Views.SingleOrDefault(v=>v.Id==parentView)??
                    throw new CadValidationException("Select a parent projection for the detail view.");
                if(parent.Kind==DrawingViewKind.Detail||parent.StaleReason is not null)
                    throw new CadValidationException("Detail views require a current base projection.");
                view=new(viewId,name,kind,parentView,center,scale,parent.Sources,parent.Strokes,null)
                    {DetailCenter=detailCenter,DetailRadius=detailRadius};
            }
            else
            {
                var inputs=DrawingInputs.Capture(document,sourcePath);
                var kernel=context.Kernel as ITechnicalDrawingKernel??throw new NotSupportedException("Drawing kernel unavailable.");
                var projection=await kernel.ProjectAsync(inputs,kind,normal,offset,context.Assets,token).ConfigureAwait(false);
                view=new(viewId,name,kind,parentView,center,scale,
                    [..inputs.Select(i=>DrawingInputs.Source(document,i))],projection.Strokes,null,normal,offset);
            }
            var updated=sheet with{Views=sheet.Views.Add(view)};updated.Validate(document.Id);
            return new((document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,updated)}).WithNewState());
        }
    }

    private sealed class AddDrawingDimensionCommand(Guid sheetId,TechnicalDrawingDimension dimension,bool replace=false):ICadDocumentCommand
    {
        public string Name=>"Add drawing dimension";
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            var document=context.Snapshot;var sheet=document.DrawingSheets[sheetId];
            dimension.Validate(document.Id);
            var view=sheet.Views.Single(v=>v.Id==dimension.ViewId);
            var index=sheet.Dimensions.FindIndex(d=>d.Id==dimension.Id);
            if(replace?index<0:index>=0)throw new CadValidationException("Drawing dimension identity does not match the edit.");
            await DrawingInputs.VerifyDatumAsync(context,dimension.First,view,token).ConfigureAwait(false);
            if(dimension.Second is {} second)
                await DrawingInputs.VerifyDatumAsync(context,second,view,token).ConfigureAwait(false);
            var value=DrawingInputs.Measure(document,dimension);
            var verified=dimension with{Value=value,StaleReason=null};
            var updated=sheet with{Dimensions=replace?sheet.Dimensions.SetItem(index,verified):sheet.Dimensions.Add(verified)};
            updated.Validate(document.Id);
            return new((document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,updated)}).WithNewState());
        }
    }
}

internal static class DrawingInputs
{
    public static ImmutableArray<GeometryInstance> Capture(DocumentSnapshot document,OccurrencePath? path)
    {
        if(path is not null&&path.DocumentId!=document.Id)throw new CadValidationException("Drawing source belongs to another document.");
        var inputs=ImmutableArray.CreateBuilder<GeometryInstance>();
        foreach(var occurrence in document.EnumerateOccurrences())
        {
            if(path is not null&&!occurrence.Path.Equals(path)&&!occurrence.Path.Slots.Take(path.Slots.Length).SequenceEqual(path.Slots))continue;
            if(document.Definitions[occurrence.DefinitionId] is not PartDefinition part)continue;
            foreach(var id in part.Bodies)
            {
                var body=document.Bodies[id];if(body.Geometry.Kind==BodyKind.Empty||body.Producer is {} producer&&document.Features[producer].IsStale)continue;
                inputs.Add(new(occurrence.Path,id,body.Geometry,occurrence.WorldTransform));
                if(inputs.Count>256)throw new CadValidationException("A drawing view supports at most 256 body instances.");
            }
        }
        if(inputs.Count==0)throw new CadValidationException("Drawing source has no model geometry.");
        GeometryInstanceGuard.Validate(document,inputs.ToArray());return inputs.ToImmutable();
    }

    public static DrawingSource Source(DocumentSnapshot document,GeometryInstance input)=>new(input.Path,input.BodyId,
        document.Bodies[input.BodyId].Producer,input.Geometry.Revision,input.Geometry.AssetId,input.WorldTransform);

    public static bool Resolve(DocumentSnapshot document,DrawingSource source,out GeometryInstance? input)
    {
        input=null;
        var occurrence=document.EnumerateOccurrences().FirstOrDefault(o=>o.Path.Equals(source.Path));
        if(occurrence is null||document.Definitions[occurrence.DefinitionId] is not PartDefinition part||
           !part.Bodies.Contains(source.Body)||!document.Bodies.TryGetValue(source.Body,out var body)||
           body.Producer!=source.Feature||
           body.Geometry.Kind==BodyKind.Empty||body.Producer is {} producer&&document.Features[producer].IsStale)return false;
        input=new(source.Path,source.Body,body.Geometry,occurrence.WorldTransform);return true;
    }

    public static async Task VerifyDatumAsync(DocumentCommandContext context,AssemblyDatumReference datum,
        TechnicalDrawingView view,CancellationToken token)
    {
        if(!view.Sources.Any(s=>s.Path.Equals(datum.Path)&&s.Body==datum.BodyId))
            throw new CadValidationException("Dimension datum does not belong to its drawing view.");
        var resolver=context.Kernel as IAssemblyDatumResolver??throw new NotSupportedException("Analytic datum resolver unavailable.");
        var actual=await resolver.ResolveAssemblyDatumAsync(context.Snapshot,datum.Path,datum.BodyId,
            datum.FullTopologyIndex,datum.Fingerprint,context.Assets,token).ConfigureAwait(false);
        if(actual!=datum)throw new CadValidationException("Drawing dimension datum differs from the original BRep.");
    }

    public static double Measure(DocumentSnapshot document,TechnicalDrawingDimension dimension)
    {
        var positions=document.EnumerateOccurrences().ToDictionary(o=>o.Path,o=>o.WorldTransform);
        var first=dimension.First;
        if(!positions.TryGetValue(first.Path,out var a))throw new CadValidationException("Drawing datum instance is missing.");
        return dimension.Kind switch
        {
            DrawingMeasureKind.Radius=>first.RadiusMm,
            DrawingMeasureKind.Diameter=>first.RadiusMm*2,
            DrawingMeasureKind.Length when dimension.Second is {} second=>
                (a.Apply(first.LocalPoint)-positions[second.Path].Apply(second.LocalPoint)).Length,
            DrawingMeasureKind.Angle when dimension.Second is {} second=>
                Math.Acos(Math.Clamp(a.Rotation.Rotate(first.LocalAxis).Dot(
                    positions[second.Path].Rotation.Rotate(second.LocalAxis)),-1,1))*180/Math.PI,
            _=>throw new CadValidationException("Unsupported drawing measure.")
        };
    }
}
