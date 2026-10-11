using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

public static class DrawingHoleTableCommands
{
    public static ICadDocumentCommand Put(Guid sheet,DrawingHoleTable table,bool replace=false)=>new PutCommand(sheet,table,replace);
    public static ICadDocumentCommand Delete(Guid sheet,Guid id)=>new EditDocumentCommand("Delete hole table",document=>
    {
        var current=document.DrawingSheets[sheet];
        if(!current.HoleTables.Any(t=>t.Id==id))throw new CadValidationException("Hole table is missing.");
        return document with{DrawingSheets=document.DrawingSheets.SetItem(sheet,current with{HoleTables=[..current.HoleTables.Where(t=>t.Id!=id)]})};
    });
    public static ICadDocumentCommand DeleteRow(Guid sheet,Guid table,Guid row)=>new EditDocumentCommand("Delete hole row",document=>
    {
        var current=document.DrawingSheets[sheet];int index=current.HoleTables.FindIndex(t=>t.Id==table);
        if(index<0||!current.HoleTables[index].Rows.Any(r=>r.Id==row))throw new CadValidationException("Hole table row is missing.");
        var value=current.HoleTables[index];
        return document with{DrawingSheets=document.DrawingSheets.SetItem(sheet,current with
        {HoleTables=current.HoleTables.SetItem(index,value with{Rows=[..value.Rows.Where(r=>r.Id!=row)]})})};
    });
    public static ICadDocumentCommand Move(Guid sheet,Guid table,Point2d position)=>new EditDocumentCommand("Move hole table",document=>
    {
        var current=document.DrawingSheets[sheet];int index=current.HoleTables.FindIndex(t=>t.Id==table);
        if(index<0)throw new CadValidationException("Hole table is missing.");
        return document with{DrawingSheets=document.DrawingSheets.SetItem(sheet,current with
        {HoleTables=current.HoleTables.SetItem(index,current.HoleTables[index] with{PositionMm=position})})};
    });
    internal static async Task VerifyAsync(DocumentCommandContext context,TechnicalDrawingView view,DrawingHoleEvidence evidence,CancellationToken token)
    {
        evidence.Validate(context.Snapshot.Id);
        await DrawingAnnotationCommands.VerifyAsync(context,view,evidence.Source,token).ConfigureAwait(false);
        var resolver=context.Kernel as IDrawingHoleResolver??throw new NotSupportedException("Current manufacturing hole inspection unavailable.");
        var actual=await resolver.ResolveDrawingHoleAsync(context.Snapshot,evidence.Source,context.Assets,token).ConfigureAwait(false);
        if(!evidence.SameProof(actual))throw new CadValidationException("Hole dimensions or topology proof differ from the original current solid.");
    }
    internal static async Task<TechnicalDrawingSheet> UpdateAsync(DocumentCommandContext context,TechnicalDrawingSheet sheet,CancellationToken token)
    {
        if(sheet.HoleTables.IsEmpty)return sheet;
        var tables=sheet.HoleTables.ToBuilder();
        for(int i=0;i<tables.Count;i++)
        {
            var table=tables[i];var rows=table.Rows.ToBuilder();string? basisProblem=null;
            try
            {
                var view=sheet.Views.Single(v=>v.Id==table.ViewId);
                if(view.StaleReason is not null||!view.Sources.Any(s=>s.Path.Equals(table.CoordinatePath)))
                    throw new CadValidationException("Coordinate occurrence no longer belongs to the current drawing view.");
                table=table.RefreshCoordinates(context.Snapshot);
            }
            catch(Exception error) when(error is not OperationCanceledException)
            {var text="Hole coordinate datum is stale; reselect or detach explicitly: "+error.Message;basisProblem=text[..Math.Min(2048,text.Length)];}
            for(int j=0;j<rows.Count;j++)
            {
                var row=rows[j];
                try
                {
                    if(basisProblem is not null)throw new CadValidationException(basisProblem);
                    var view=sheet.Views.Single(v=>v.Id==table.ViewId);
                    var reference=await DrawingAttachmentContinuation.ResolveAsync(context,view,row.Evidence.Source,token).ConfigureAwait(false);
                    var evidence=row.Evidence;
                    if(reference==row.Evidence.Source)
                    {
                        // A symbolic-author edit can leave the exact BRep and attachment unchanged.
                        // Reprove that original wall before refreshing only its current author intent.
                        var resolver=context.Kernel as IDrawingHoleResolver??throw new NotSupportedException("Current manufacturing hole inspection unavailable.");
                        var actual=await resolver.ResolveDrawingHoleAsync(context.Snapshot,reference,context.Assets,token).ConfigureAwait(false);
                        if(!evidence.SameProof(actual with{SpecificationSource=evidence.SpecificationSource,AuthoredFeature=evidence.AuthoredFeature,Standard=evidence.Standard,Thread=evidence.Thread}))
                            throw new CadValidationException("Hole dimensions or topology proof changed without an exact attachment transition.");
                        // Complete identical proof is a no-op. ImmutableArray record equality also
                        // carries identity; avoid replacing existing geometry proof just because
                        // independent native inspection allocated an equivalent face-index array.
                        if(!evidence.SameProof(actual))evidence=actual;
                    }
                    else
                    {
                        var resolver=context.Kernel as IDrawingHoleResolver??throw new NotSupportedException("Current manufacturing hole inspection unavailable.");
                        evidence=await resolver.ResolveDrawingHoleAsync(context.Snapshot,reference,context.Assets,token).ConfigureAwait(false);
                    }
                    rows[j]=row with{Evidence=evidence,CoordinatesMm=table.Coordinates(context.Snapshot,evidence),StaleReason=null};
                }
                catch(Exception error) when(error is not OperationCanceledException)
                {var text="Hole row is stale; explicitly reselect the current shaft: "+error.Message;rows[j]=row with{StaleReason=text[..Math.Min(2048,text.Length)]};}
            }
            if(!rows.SequenceEqual(table.Rows))table=table with{Rows=rows.ToImmutable()};
            tables[i]=table.StaleReason==basisProblem?table:table with{StaleReason=basisProblem};
        }
        return tables.SequenceEqual(sheet.HoleTables)?sheet:sheet with{HoleTables=tables.ToImmutable()};
    }
    private sealed class PutCommand(Guid sheetId,DrawingHoleTable table,bool replace):ICadDocumentCommand
    {
        public string Name=>"Set current manufacturing hole table";
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            var document=context.Snapshot;
            if(table.CoordinateBinding is not null)await DatumRecompute.ConfirmAsync(context,document,token).ConfigureAwait(false);
            var value=table.RefreshCoordinates(document);value.Validate(document.Id);var sheet=document.DrawingSheets[sheetId];
            int index=sheet.HoleTables.FindIndex(t=>t.Id==table.Id);
            if((index>=0)!=replace)throw new CadValidationException("Hole table identity differs from the requested edit.");
            var view=sheet.Views.SingleOrDefault(v=>v.Id==table.ViewId)??throw new CadValidationException("Hole table view is missing.");
            if(view.StaleReason is not null||!document.EnumerateOccurrences().Any(o=>o.Path.Equals(table.CoordinatePath)&&o.DefinitionId==table.CoordinateDefinition)||
                !view.Sources.Any(s=>s.Path.Equals(table.CoordinatePath)))throw new CadValidationException("Hole table coordinate occurrence must belong to its current view.");
            var rows=table.Rows.ToBuilder();
            for(int i=0;i<rows.Count;i++)
            {
                var previous=replace?sheet.HoleTables[index].Rows.FirstOrDefault(r=>r.Id==rows[i].Id):null;
                if(previous?.StaleReason is not null&&previous.Evidence.SameProof(rows[i].Evidence))
                {rows[i]=rows[i] with{CoordinatesMm=previous.CoordinatesMm,StaleReason=previous.StaleReason};continue;}
                await VerifyAsync(context,view,rows[i].Evidence,token).ConfigureAwait(false);
                rows[i]=rows[i] with{CoordinatesMm=value.Coordinates(document,rows[i].Evidence),StaleReason=null};
            }
            value=value with{Rows=rows.ToImmutable(),StaleReason=null};
            var next=sheet with{HoleTables=replace?sheet.HoleTables.SetItem(index,value):sheet.HoleTables.Add(value)};
            next.Validate(document.Id);token.ThrowIfCancellationRequested();
            return new((document with{DrawingSheets=document.DrawingSheets.SetItem(sheetId,next)}).WithNewState());
        }
    }
}
