using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Cli;

public static partial class CadCommandLine
{
    private sealed record MaterialOptions(string Input,string Request,string? Output,bool Overwrite,bool Memory)
    {
        public static MaterialOptions Parse(string[] args)
        {
            if(args.Length<4||args[0]!="material")throw new ArgumentException("material requires INPUT.cadoryx and --request REQUEST.json.");
            var input=Path.GetFullPath(args[1]);string? request=null,output=null;bool overwrite=false,memory=false;
            var seen=new HashSet<string>(StringComparer.Ordinal);
            for(int i=2;i<args.Length;i++)
            {
                var key=args[i];if(!seen.Add(key))throw new ArgumentException("Duplicate option: "+key);
                string Value(){if(++i>=args.Length||args[i].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException("Missing value for "+key);return args[i];}
                switch(key)
                {
                    case "--request":request=Path.GetFullPath(Value());break;
                    case "--output":output=Path.GetFullPath(Value());break;
                    case "--overwrite":overwrite=true;break;
                    case "--memory":memory=true;break;
                    default:throw new ArgumentException("Unsupported material option: "+key);
                }
            }
            if(!Path.GetExtension(input).Equals(".cadoryx",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Material authoring requires a .cadoryx document.");
            if(request is null||!Path.GetExtension(request).Equals(".json",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("--request requires a JSON file.");
            if(output is not null&&!Path.GetExtension(output).Equals(".cadoryx",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Material output must be a .cadoryx document.");
            if(StringComparer.OrdinalIgnoreCase.Equals(input,output)||StringComparer.OrdinalIgnoreCase.Equals(request,output)||StringComparer.OrdinalIgnoreCase.Equals(input,request))
                throw new ArgumentException("Input, request and output must be different files.");
            return new(input,request,output,overwrite,memory);
        }
    }

    private static async Task<int> RunMaterialAsync(string[] args,TextWriter output,TextWriter error,CancellationToken token)
    {
        MaterialOptions? options=null;
        try
        {
            token.ThrowIfCancellationRequested();
            if(args is ["material-contract"])
            {await output.WriteLineAsync(CadMaterialAuthoringService.Contract());return 0;}
            options=MaterialOptions.Parse(args);
        }
        catch(OperationCanceledException){await error.WriteLineAsync("Cancelled.");return 130;}
        catch(ArgumentException ex){await error.WriteLineAsync(ex.Message);return 2;}

        JsonDocument parsed;
        try
        {
            await using var stream=new FileStream(options.Request,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.Asynchronous);
            if(stream.Length is <=0 or >65536)throw new ArgumentException("Material request file exceeds its input budget.");
            using var reader=new StreamReader(stream,new UTF8Encoding(false,true),detectEncodingFromByteOrderMarks:false);
            var text=await reader.ReadToEndAsync(token);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            parsed=CadMaterialAuthoringService.ParseRequest(text);
        }
        catch(OperationCanceledException){await error.WriteLineAsync("Cancelled.");return 130;}
        catch(Exception ex) when(ex is ArgumentException or JsonException or DecoderFallbackException)
        {await error.WriteLineAsync(ex.Message);return 2;}
        catch(Exception ex){await error.WriteLineAsync(ex.Message);return 1;}

        using(parsed)
        try
        {
            token.ThrowIfCancellationRequested();var request=parsed.RootElement;
            var action=CadMaterialAuthoringService.Action(request);
            if(action=="contract")
            {
                CadMaterialAuthoringService.ValidateContract(request);
                if(options.Output is not null)throw new ArgumentException("A material contract query cannot write a document.");
                await output.WriteLineAsync(CadMaterialAuthoringService.Contract());return 0;
            }
            bool readOnly=action=="inspect";
            if(readOnly&&options.Output is not null)throw new ArgumentException("A material inspection cannot write a document.");
            if(!readOnly&&options.Output is null)throw new ArgumentException("Material mutations require --output OUTPUT.cadoryx.");
            if(options.Output is not null&&!options.Overwrite&&File.Exists(options.Output))throw new IOException("Output exists; use --overwrite to replace it.");

            // Read-only inspection can discover exact identities. Mutations never invent them.
            bool explicitIdentity=!readOnly||request.TryGetProperty("documentId",out _)||request.TryGetProperty("stateId",out _);
            var expectation=explicitIdentity?CadMaterialAuthoringService.Expectation(request):null;
            using var disk=options.Memory?null:new DiskAssetStore(Path.Combine(Path.GetTempPath(),"Cadoryx","CliAssets"));
            IAssetStore assets=(IAssetStore?)disk??new MemoryAssetStore();var kernel=CreateKernel();var storage=new CadDocumentStorage(datumResolver:kernel);
            using var loaded=await storage.LoadAsync(options.Input,assets,token);
            await using var session=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher(),options.Input);
            expectation??=DocumentCommandExpectation.Capture(session.Snapshot);
            var result=await CadMaterialAuthoringService.ApplyAsync(session,expectation,request,explicitIdentity,token);
            if(readOnly){await output.WriteLineAsync(result);return 0;}

            using var capture=session.Capture();var receipt=JsonNode.Parse(result)!.AsObject();receipt["output"]=options.Output;
            string? outputHash=null;
            await AtomicOutput(options.Output!,options.Overwrite,async path=>
            {await storage.SaveAsync(capture.Snapshot,assets,path,token);outputHash=await Hash(path,token);},token);
            receipt["outputSha256"]=outputHash;
            await output.WriteLineAsync(receipt.ToJsonString(Json));return 0;
        }
        catch(OperationCanceledException){await error.WriteLineAsync("Cancelled.");return 130;}
        catch(Exception ex)
        {
            await error.WriteLineAsync(ex.Message);
            await output.WriteLineAsync(JsonSerializer.Serialize(new{success=false,message=ex.Message,
                nextRead=new{command="material",input=options.Input,request=new{action="inspect"}}},Json));return 1;
        }
    }
}
