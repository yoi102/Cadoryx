using Cadoryx.Db;

namespace Cadoryx.Commands;

/// <summary>Commits a completely postprocessed preview while its originating editor retains the asset leases.</summary>
public sealed class CommitPreparedDocumentCommand(DocumentSnapshot snapshot,long generation,string name,FeatureCursor? cursor=null):IFeatureCursorCommand
{
    public FeatureCursor? Cursor=>cursor;
    public string Name=>name;
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(context.Generation!=generation||snapshot.Id!=context.Snapshot.Id)throw new CadValidationException("The document changed after preview. Prepare a new preview before confirming.");
        snapshot.Validate();return Task.FromResult(new PreparedDocumentEdit(snapshot));
    }
}
