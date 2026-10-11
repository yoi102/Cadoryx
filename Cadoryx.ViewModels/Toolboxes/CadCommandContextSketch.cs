using System.Text.Json;
using Cadoryx.CommandLine;

namespace Cadoryx.ViewModels.Toolboxes;

internal sealed partial class CadCommandContext
{
    public async Task<CadCommandResult> ReselectSketchRegionSourcesAsync(string requestJson,CancellationToken cancellationToken=default)
    {
        EnsureEditable();if(requestJson.Length>16384)throw new ArgumentException("Region source request exceeds its input budget.");
        using var parsed=JsonDocument.Parse(requestJson);
        return new(true,await CadSketchRegionReselectionAuthoring.ApplyAsync(document,parsed.RootElement,cancellationToken,expectation));
    }
    public async Task<CadCommandResult> OffsetSketchAsync(string requestJson,CancellationToken cancellationToken=default)
    {
        EnsureEditable();if(requestJson.Length>16384)throw new ArgumentException("Sketch offset request exceeds its input budget.");
        using var parsed=JsonDocument.Parse(requestJson);var args=parsed.RootElement;
        string action=CadAgentParameterTools.RequiredString(args,"action",32);
        return new(true,await CadSketchOffsetAuthoring.ApplyAsync(document,args,action,cancellationToken,expectation));
    }
    public async Task<CadCommandResult> EditSketchFiniteContactAsync(string requestJson,CancellationToken cancellationToken=default)
    {
        EnsureEditable();if(requestJson.Length>16384)throw new ArgumentException("Finite contact request exceeds its input budget.");
        using var parsed=JsonDocument.Parse(requestJson);var args=parsed.RootElement;
        string action=CadAgentParameterTools.RequiredString(args,"action",32);
        return new(true,await CadSketchFiniteContactAuthoring.ApplyAsync(document,args,action,cancellationToken,expectation));
    }
    public async Task<CadCommandResult> EditSketchCurvatureAsync(string requestJson,CancellationToken cancellationToken=default)
    {
        EnsureEditable();if(requestJson.Length>16384)throw new ArgumentException("Sketch curvature request exceeds its input budget.");
        using var parsed=JsonDocument.Parse(requestJson);var args=parsed.RootElement;
        string action=CadAgentParameterTools.RequiredString(args,"action",32);
        return new(true,await CadSketchCurvatureAuthoring.ApplyAsync(document,args,action,cancellationToken,expectation));
    }
    public async Task<CadCommandResult> EditSketchContactAsync(string requestJson,CancellationToken cancellationToken=default)
    {
        EnsureEditable();if(requestJson.Length>16384)throw new ArgumentException("Sketch contact request exceeds its input budget.");
        using var parsed=JsonDocument.Parse(requestJson);var args=parsed.RootElement;
        string action=CadAgentParameterTools.RequiredString(args,"action",32);
        var result=await CadSketchContactAuthoring.ApplyAsync(document,args,action,cancellationToken,expectation);
        return new(true,result);
    }
}
