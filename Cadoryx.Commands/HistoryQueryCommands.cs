using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

public sealed class UpsertHistoryQueryCommand(HistoryQuery query) : ICadDocumentCommand
{
    public string Name=>"Save history query";
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();query.Validate(context.Snapshot.Id);
        var old=context.Snapshot.HistoryQueries.GetValueOrDefault(query.Id);
        if(old==query)return Task.FromResult(new PreparedDocumentEdit(context.Snapshot));
        return Task.FromResult(new PreparedDocumentEdit((context.Snapshot with
        {HistoryQueries=context.Snapshot.HistoryQueries.SetItem(query.Id,query)}).WithNewState()));
    }
}

public sealed class RemoveHistoryQueryCommand(HistoryQueryId id) : ICadDocumentCommand
{
    public string Name=>"Remove history query";
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!context.Snapshot.HistoryQueries.ContainsKey(id))throw new CadValidationException("History query does not exist.");
        return Task.FromResult(new PreparedDocumentEdit((context.Snapshot with
        {HistoryQueries=context.Snapshot.HistoryQueries.Remove(id)}).WithNewState()));
    }
}
