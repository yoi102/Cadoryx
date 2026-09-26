using Cadoryx.Cli;
using var cancellation=new CancellationTokenSource();
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
// Opt-in lifecycle diagnostic for the published-process gate; never used by ordinary CLI commands.
if(args is ["--asset-cache-seed",var cacheRoot,var readyPath])
{
    using var store=new Cadoryx.Kernel.Abstractions.DiskAssetStore(cacheRoot);
    using var lease=store.Stage([1,3,5,7]);
    await using(var signal=new FileStream(readyPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
    {await signal.WriteAsync(System.Text.Encoding.UTF8.GetBytes(store.DirectoryPath));signal.Flush(true);}
    await Task.Delay(Timeout.Infinite,cancellation.Token);return 0;
}
return await CadCommandLine.RunAsync(args,Console.Out,Console.Error,cancellation.Token);
