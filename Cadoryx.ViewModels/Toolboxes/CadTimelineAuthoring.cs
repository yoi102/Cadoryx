using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadTimelineAuthoring
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);
    internal static async Task<string> ApplyAsync(CadDocumentViewModel document, DocumentHistoryExpectation expectation,
        CadTimelineEditRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (document.IsDetached || document.IsClosingRequested) throw new InvalidOperationException("The active document is closing or detached.");
        var snapshot = document.Session.Snapshot; var beforeCursor = document.Session.FeatureCursor;
        expectation.Validate(snapshot, beforeCursor);
        if (request.Action is "rollback" or "return_end")
        {
            if (request.Id is not null || request.Revision is not null || request.Suppressed is not null || request.Order is not null)
                throw new ArgumentException("Preview actions do not accept author edit fields.");
            if (request.Action == "rollback")
            {
                if (request.PartId is null || request.PartId == Guid.Empty || request.Position is null || request.Position is < 0 or > 1000000)
                    throw new ArgumentException("Rollback requires exact partId and a nonnegative position.");
                await document.Session.RollbackFeaturesAsync(new(request.PartId.Value), request.Position.Value, token, expectation);
            }
            else
            {
                if (request.PartId is not null || request.Position is not null) throw new ArgumentException("Return to end accepts no part or position.");
                await document.Session.ReturnToHistoryEndAsync(token, expectation);
            }
        }
        else
        {
            if (document.IsReadOnly) throw new InvalidOperationException("Return to the history end before editing an editable document.");
            ICadDocumentCommand command;
            if (request.Action == "set_suppressed")
            {
                if (request.Id is null || request.Id == Guid.Empty || request.Revision is null || request.Revision == Guid.Empty ||
                    request.Suppressed is null || request.PartId is not null || request.Position is not null || request.Order is not null)
                    throw new ArgumentException("Suppression requires exactly id, revision and suppressed.");
                var feature = snapshot.Features.GetValueOrDefault(new(request.Id.Value)) ?? throw new ArgumentException("Exact feature ID is missing.");
                if (feature.Result.Revision.Value != request.Revision) throw new StaleDocumentException();
                command = new SetFeatureSuppressionCommand(feature.Id, request.Suppressed.Value);
            }
            else if (request.Action == "reorder")
            {
                if (request.PartId is null || request.PartId == Guid.Empty || request.Order is null || request.Order.Count is < 1 or > 1024 ||
                    request.Order.Any(id => id == Guid.Empty) || request.Id is not null || request.Revision is not null || request.Suppressed is not null || request.Position is not null)
                    throw new ArgumentException("Reorder requires an exact partId and its complete feature order (1–1024 entries).");
                command = new ReorderFeaturesCommand(new(request.PartId.Value), request.Order.Select(id => new FeatureId(id)).ToImmutableArray());
            }
            else throw new ArgumentException("Unknown history action.");
            await document.Session.ExecuteAsync(command, token, expectation.Author);
        }
        var after = document.Session.Snapshot; var cursor = document.Session.FeatureCursor;
        return JsonSerializer.Serialize(new { success = true, action = request.Action, documentId = after.Id.ToString(), stateId = after.StateId.ToString(),
            changed = snapshot.StateId != after.StateId, previewChanged = beforeCursor != cursor, authorState = true,
            cursor = cursor is null ? null : new { stateId = cursor.State.ToString(), partId = cursor.Part.ToString(), cursor.Position } }, OutputJson);
    }
}
