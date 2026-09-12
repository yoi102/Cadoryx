using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

/// <summary>Register or explicitly reselect a reference only after unique native resolution.</summary>
public sealed class UpsertTopologyReferenceCommand(TopologyReference reference) : ICadDocumentCommand
{
    public string Name=>"Set topology reference";
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        reference.Validate();
        if(context.Kernel is not ITopologyResolver resolver)throw new NotSupportedException("Kernel does not support topology references.");
        if(!context.Snapshot.Features.TryGetValue(reference.FeatureId,out var feature)||feature.Result.Revision!=reference.OriginRevision)
            throw new CadValidationException("Reselect against the current geometry revision.");
        if(context.Snapshot.Bodies.TryGetValue(feature.OutputBodyId,out var body)&&context.Snapshot.Layers[body.LayerId].IsLocked)
            throw new CadValidationException("Output layer is locked.");
        var result=await resolver.ResolveAsync(context.Snapshot,reference,context.Assets,cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if(result.Status!=TopologyResolutionStatus.Resolved)throw new CadValidationException(result.Diagnostic);
        return new((context.Snapshot with{TopologyReferences=context.Snapshot.TopologyReferences.SetItem(reference.Id,reference)}).WithNewState());
    }
}
public sealed class RemoveTopologyReferenceCommand(TopologyReferenceId id) : ICadDocumentCommand
{
    public string Name=>"Remove topology reference";
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(!context.Snapshot.TopologyReferences.ContainsKey(id))throw new CadValidationException("Topology reference does not exist.");
        return Task.FromResult(new PreparedDocumentEdit((context.Snapshot with{TopologyReferences=context.Snapshot.TopologyReferences.Remove(id)}).WithNewState()));
    }
}
