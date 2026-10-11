using Cadoryx.CommandLine;
using System.Text.Json;

namespace Cadoryx.ViewModels.Toolboxes;

internal sealed partial class CadCommandContext
{
    public CadCommandResult InspectAuthoring(string area, int offset, int limit, Guid? partId = null)
    {
        ValidateInvocation();
        return new(true, CadAuthoringInspection.Read(document.Session, area, null, partId, offset, limit));
    }

    public async Task<CadCommandResult> EditParameterAsync(CadParameterEditRequest request, CancellationToken cancellationToken = default)
    {
        EnsureEditable();
        return new(true, await CadParameterAuthoring.ApplyAsync(document, expectation, request, cancellationToken));
    }

    public async Task<CadCommandResult> EditTimelineAsync(CadTimelineEditRequest request, CancellationToken cancellationToken = default)
    {
        ValidateInvocation();
        return new(true, await CadTimelineAuthoring.ApplyAsync(document, new(expectation, capturedCursor), request, cancellationToken));
    }
    public async Task<CadCommandResult> EditDatumAsync(string requestJson,CancellationToken cancellationToken=default)
    {
        ValidateInvocation();
        if(requestJson.Length>16384)throw new ArgumentException("Datum request exceeds its input budget.");
        using var parsed=JsonDocument.Parse(requestJson);
        return new(true,await CadDatumAuthoring.ApplyAsync(document,expectation,parsed.RootElement,false,cancellationToken));
    }
    public CadCommandResult InspectModelContract(string type,int offset=0,int limit=1){ValidateInvocation();return new(true,CadModelContract.Read(type,offset,limit));}
    public async Task<CadCommandResult> EditModelAsync(string requestJson,CancellationToken cancellationToken=default)
    {
        ValidateInvocation();if(requestJson.Length>32768)throw new ArgumentException("Model request exceeds its input budget.");
        using var parsed=JsonDocument.Parse(requestJson);
        return new(true,await CadModelAuthoring.ApplyAsync(document,expectation,parsed.RootElement,false,cancellationToken));
    }
}
