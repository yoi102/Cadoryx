using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

internal static class DrawingAttachmentContinuation
{
    internal static async Task<DrawingTopologyReference> ResolveAsync(DocumentCommandContext context,TechnicalDrawingView view,
        DrawingTopologyReference reference,CancellationToken token)
    {
        try
        {
            await DrawingAnnotationCommands.VerifyAsync(context,view,reference,token).ConfigureAwait(false);
            return reference;
        }
        catch(Exception error) when(error is not OperationCanceledException)
        {
            if(view.StaleReason is not null)throw;
            var author=reference.AuthorSelection();
            if(author.FeatureId is not {} source)throw;
            var candidates=new List<DrawingSource>();
            foreach(var target in view.Sources.Where(s=>s.Path.Equals(author.Path)&&s.Feature is not null))
            {
                var plan=TopologyHistoryPaths.Plan(context.Snapshot,source,target.Feature!.Value,token);
                if(plan.Failure is null)candidates.Add(target);
                else if(plan.Failure.Status is HistoryResolutionStatus.Ambiguous or HistoryResolutionStatus.LimitExceeded or HistoryResolutionStatus.WrongContext)
                    throw new CadValidationException("Manufacturing attachment history cannot select a target: "+plan.Failure.Diagnostic);
            }
            if(candidates.Count!=1)throw new CadValidationException("Manufacturing attachment requires one explicit reachable drawing source; no target was substituted.");
            var resolver=context.Kernel as IDrawingTopologyHistoryResolver??throw new NotSupportedException("Manufacturing attachment history resolver unavailable.");
            var result=await resolver.ResolveDrawingHistoryAsync(context.Snapshot,author,candidates[0],context.Assets,token).ConfigureAwait(false);
            if(result.History.Status!=HistoryResolutionStatus.Resolved||result.Current is not {} current)
                throw new CadValidationException("Manufacturing attachment history stopped: "+result.History.Diagnostic);
            return current with{HistoryOrigin=author};
        }
    }
}
