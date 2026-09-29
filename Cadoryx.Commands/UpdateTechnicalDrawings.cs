using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

/// <summary>Rebuilds drawing caches in the initiating edit; failures retain the previous lines and mark them stale.</summary>
public static class UpdateTechnicalDrawings
{
    public static async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,
        DocumentSnapshot next,CancellationToken token)
    {
        if(next.DrawingSheets.Count==0||next.StateId==context.Snapshot.StateId)return new(next);
        var kernel=context.Kernel as ITechnicalDrawingKernel??throw new NotSupportedException("Drawing kernel unavailable.");
        var sheets=next.DrawingSheets.ToBuilder();
        foreach(var sheet in next.DrawingSheets.Values)
        {
            token.ThrowIfCancellationRequested();
            var views=ImmutableArray.CreateBuilder<TechnicalDrawingView>(sheet.Views.Length);
            foreach(var view in sheet.Views.OrderBy(v=>v.Kind==DrawingViewKind.Detail?1:0))
            {
                if(view.Kind==DrawingViewKind.Detail)
                {
                    var parent=views.FirstOrDefault(v=>v.Id==view.ParentView);
                    if(parent is null||parent.StaleReason is not null)
                        views.Add(view with{StaleReason="Parent drawing projection is stale."});
                    else views.Add(view with{Sources=parent.Sources,Strokes=parent.Strokes,StaleReason=null});
                    continue;
                }
                var inputs=new List<GeometryInstance>();string? problem=null;
                foreach(var source in view.Sources)
                {
                    if(!DrawingInputs.Resolve(next,source,out var input)||input is null)
                    {problem="A drawing source is missing or stale; reselect it explicitly.";break;}
                    inputs.Add(input);
                }
                if(problem is not null)
                {views.Add(view with{StaleReason=problem});continue;}
                bool changed=view.StaleReason is not null||inputs.Where((input,index)=>
                    input.Geometry.AssetId!=view.Sources[index].Asset||
                    input.Geometry.Revision!=view.Sources[index].Revision||
                    input.WorldTransform!=view.Sources[index].WorldTransform).Any();
                if(!changed){views.Add(view);continue;}
                try
                {
                    var projection=await kernel.ProjectAsync(inputs,view.Kind,view.SectionNormal,
                        view.SectionOffsetMm,context.Assets,token).ConfigureAwait(false);
                    views.Add(view with{Sources=[..inputs.Select(i=>DrawingInputs.Source(next,i))],
                        Strokes=projection.Strokes,StaleReason=null});
                }
                catch(Exception error) when(error is not OperationCanceledException)
                {views.Add(view with{StaleReason="Projection failed: "+error.Message});}
            }
            var byId=views.ToImmutableDictionary(v=>v.Id);
            var resolvedViews=sheet.Views.Select(v=>byId[v.Id]).ToImmutableArray();
            var dimensions=ImmutableArray.CreateBuilder<TechnicalDrawingDimension>(sheet.Dimensions.Length);
            foreach(var dimension in sheet.Dimensions)
            {
                var view=resolvedViews.First(v=>v.Id==dimension.ViewId);
                if(view.StaleReason is not null)
                {dimensions.Add(dimension with{StaleReason="Drawing view is stale."});continue;}
                try
                {
                    var current=new DocumentCommandContext(next,context.Generation,context.Assets,context.Kernel);
                    await DrawingInputs.VerifyDatumAsync(current,dimension.First,view,token).ConfigureAwait(false);
                    if(dimension.Second is {} second)
                        await DrawingInputs.VerifyDatumAsync(current,second,view,token).ConfigureAwait(false);
                    dimensions.Add(dimension with{Value=DrawingInputs.Measure(next,dimension),StaleReason=null});
                }
                catch(Exception error) when(error is not OperationCanceledException)
                {dimensions.Add(dimension with{StaleReason="Dimension reference is stale: "+error.Message});}
            }
            sheets[sheet.Id]=sheet with{Views=resolvedViews,Dimensions=dimensions.ToImmutable()};
        }
        return new(next with{DrawingSheets=sheets.ToImmutable()});
    }
}
