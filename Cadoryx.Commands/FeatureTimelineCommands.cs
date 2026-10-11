using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;

namespace Cadoryx.Commands;

public interface IFeatureCursorCommand:ICadDocumentCommand {FeatureCursor? Cursor {get;}}

/// <summary>Evaluate a new feature against explicitly visible intermediate outputs, then restore the complete graph.</summary>
public sealed class InsertFeatureAtCursorCommand(ICadDocumentCommand command,FeatureCursor cursor):IFeatureCursorCommand
{
    public FeatureCursor Cursor=>cursor;
    public string Name=>command.Name;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var view=FeatureTimeline.Preview(context.Snapshot,cursor);
        var result=await command.PrepareAsync(context with{Snapshot=view},token).ConfigureAwait(false);
        try{return new(FeatureTimeline.CompleteInsertion(context.Snapshot,result.Snapshot,cursor),[result]);}
        catch{result.Dispose();throw;}
    }
}

/// <summary>Reorder chronology only; never reorder Boolean arguments, rewire dependencies or invent topology bindings.</summary>
public sealed class ReorderFeaturesCommand(DefinitionId part,ImmutableArray<FeatureId> order):ICadDocumentCommand
{
    public string Name=>Strings.ReorderFeatures;
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();FeatureTimeline.ValidateOrder(context.Snapshot,part,order);
        var definition=(PartDefinition)context.Snapshot.Definitions[part];
        if(definition.Features.SequenceEqual(order))return Task.FromResult(new PreparedDocumentEdit(context.Snapshot));
        return Task.FromResult(new PreparedDocumentEdit((context.Snapshot with
            {Definitions=context.Snapshot.Definitions.SetItem(part,definition with{Features=order})}).WithNewState()));
    }
}
