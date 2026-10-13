using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Editor;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadMaterialAuthoring
{
    internal static readonly string Schema = CadMaterialAuthoringService.Schema;
    internal static string Contract() => CadMaterialAuthoringService.Contract();
    internal static async Task<string> ExecuteAsync(AiToolCall call,CadDocumentViewModel? document,CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var parsed=CadMaterialAuthoringService.ParseRequest(call.ArgumentsJson);var args=parsed.RootElement;
            if(CadMaterialAuthoringService.Action(args)=="contract")
            {CadMaterialAuthoringService.ValidateContract(args);return Contract();}
            if(document is null)throw new InvalidOperationException("No active document.");
            return await ApplyAsync(document,CadMaterialAuthoringService.Expectation(args),args,true,token);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error) when(error is ArgumentException or FormatException or JsonException or InvalidOperationException or KeyNotFoundException or NotSupportedException)
        {return JsonSerializer.Serialize(new{success=false,message=error.Message,nextRead=new{tool="cad_status",arguments=new{}}});}
    }
    internal static Task<string> ApplyAsync(CadDocumentViewModel document,DocumentCommandExpectation expected,JsonElement args,bool explicitIdentity,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();expected.Validate(document.Session.Snapshot);
        if(document.IsDetached||document.IsClosingRequested||document.Session.IsClosing)throw new InvalidOperationException("Document is closing or detached.");
        if(CadMaterialAuthoringService.Action(args)!="inspect"&&document.IsReadOnly)throw new InvalidOperationException("The document cannot be edited.");
        return CadMaterialAuthoringService.ApplyAsync(document.Session,expected,args,explicitIdentity,token);
    }
}

internal sealed partial class CadCommandContext
{
    public async Task<CadCommandResult> EditMaterialAsync(string requestJson,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();ValidateInvocation();using var parsed=CadMaterialAuthoringService.ParseRequest(requestJson);
        if(CadMaterialAuthoringService.Action(parsed.RootElement)=="contract")
        {CadMaterialAuthoringService.ValidateContract(parsed.RootElement);return new(true,CadMaterialAuthoring.Contract());}
        return new(true,await CadMaterialAuthoring.ApplyAsync(document,expectation,parsed.RootElement,false,cancellationToken));
    }
}
