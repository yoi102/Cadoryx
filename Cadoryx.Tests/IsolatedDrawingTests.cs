using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

[Collection("IsolatedNativeDeadlines")]
public sealed class IsolatedDrawingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string Root=Path.Combine(Path.GetTempPath(),"Cadoryx","ProjectionJobs");
    private static HashSet<string> Jobs(AssetId source)=>Directory.Exists(Root)?Directory.GetDirectories(Root,"projection-*").Where(dir=>
    {
        try
        {
            using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"request.json")));
            return json.RootElement.GetProperty("Assets").EnumerateArray().Any(id=>id.GetProperty("Sha256").GetString()==source.Sha256);
        }
        catch(IOException){return false;}catch(JsonException){return false;}
    }).ToHashSet():[];

    [Theory][InlineData(DrawingViewKind.Front)][InlineData(DrawingViewKind.Top)][InlineData(DrawingViewKind.Right)]
    [InlineData(DrawingViewKind.Isometric)][InlineData(DrawingViewKind.Section)]
    public async Task RealWorkerRetainsExactProjectionAndSourceAlignedHatchRules(DrawingViewKind kind)
    {
        await using var f=await Fixture.Create();var source=f.Body.Geometry.AssetId;var jobs=Jobs(source);int held=f.Assets.Count;
        var first=f.Input();GeometryInstance[] inputs=[first,first with{Path=first.Path.Append(ComponentSlotId.New()),
            WorldTransform=new(new(20,0,0),Quaterniond.FromEulerDegrees(0,0,31))}];
        SectionHatchPattern[] patterns=[new(37,.75),new(123,1.25)];Vector3d? normal=kind==DrawingViewKind.Section?Vector3d.UnitZ:null;
        var expected=await new OcctGeometryKernel().ProjectAsync(inputs,kind,normal,4,f.Assets,sectionHatchPatterns:patterns);
        var actual=await f.Kernel.ProjectAsync(inputs,kind,normal,4,f.Assets,sectionHatchPatterns:patterns);
        Assert.Equal(expected.Minimum,actual.Minimum);Assert.Equal(expected.Maximum,actual.Maximum);
        Assert.Equal(Signatures(expected),Signatures(actual));
        Assert.Equal(held,f.Assets.Count);Assert.True(jobs.SetEquals(Jobs(source)));
        if(kind==DrawingViewKind.Section)Assert.Contains(actual.Strokes,s=>s.Kind==DrawingLineKind.SectionHatch);
        output.WriteLine($"{kind}: {actual.Strokes.Length} exact source-equivalent strokes; two placements/custom source-aligned hatch rules, zero worker jobs/assets.");
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task NativeHlrCancellationOrDeadlineKillsOwnedProcessWithoutPartialView(bool deadline)
    {
        await using var f=await Fixture.Create(modeled:true,budget:deadline?TimeSpan.FromSeconds(8):null);
        var sheet=DrawingSheetTemplate.Create("Thread projection");await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        var before=f.Session.Snapshot;int held=f.Assets.Count;var source=f.Body.Geometry.AssetId;var jobs=Jobs(source);
        using var cancel=new CancellationTokenSource();var watch=Stopwatch.StartNew();
        var work=f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(80,60),2),cancel.Token);
        var(dir,pid)=await Started(source,jobs,work);using var process=Process.GetProcessById(pid);
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"Cadoryx.Cli.exe")),process.MainModule!.FileName);
        await Task.Delay(300);Assert.False(work.IsCompleted,"The native HLR must still be computing at cancellation.");
        if(deadline)
        {
            var error=await Assert.ThrowsAsync<CadValidationException>(()=>work);Assert.Contains("exceeded 8 seconds",error.Message);
            Assert.InRange(watch.Elapsed.TotalSeconds,7,18);
        }
        else
        {
            watch.Restart();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>work);
            Assert.InRange(watch.Elapsed.TotalSeconds,0,5);
        }
        Assert.True(process.HasExited);Assert.False(Directory.Exists(dir));Assert.True(jobs.SetEquals(Jobs(source)));
        Assert.Same(before,f.Session.Snapshot);Assert.Empty(f.Session.Snapshot.DrawingSheets[sheet.Id].Views);Assert.Equal(held,f.Assets.Count);
        using var box=await f.Kernel.EvaluateAsync(new BoxRecipe(1,2,3,RigidTransform3d.Identity),f.Assets);Assert.Equal(6,box.Geometry.VolumeMm3,6);
        output.WriteLine($"Native HLR start PID {pid}; {(deadline?"deadline":"cancel")} returned in {watch.Elapsed.TotalSeconds:F3}s, process/directory gone, original snapshot/assets and empty sheet retained.");
    }

    [Fact] public async Task WorkerFailureAndPreCancellationRetainDocumentAndAssets()
    {
        await using var f=await Fixture.Create();var before=f.Session.Snapshot;int held=f.Assets.Count;
        var source=f.Body.Geometry.AssetId;var jobs=Jobs(source);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Kernel.ProjectAsync([f.Input()],DrawingViewKind.Detail,null,0,f.Assets));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Kernel.ProjectAsync([f.Input()],DrawingViewKind.Top,null,0,f.Assets,cancel.Token));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(held,f.Assets.Count);Assert.True(jobs.SetEquals(Jobs(source)));
    }

    [Fact] public async Task RealWorkerRejectsTamperedSourceBeforeNativeProjection()
    {
        await using var f=await Fixture.Create();var id=f.Body.Geometry.AssetId;int held=f.Assets.Count;var jobs=Jobs(id);
        string directory=Path.Combine(Root,"projection-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        Assert.Equal(Path.GetFullPath(Root),Path.GetDirectoryName(Path.GetFullPath(directory)));
        try
        {
            using var lease=f.Assets.Acquire(id);var bytes=lease.Content.ToArray();bytes[0]^=1;
            await File.WriteAllBytesAsync(Path.Combine(directory,id.Sha256+".brep"),bytes);
            await File.WriteAllTextAsync(Path.Combine(directory,"request.json"),JsonSerializer.Serialize(new{Contract=1,Instances=new[]{f.Input()},
                Assets=new[]{id},Kind=DrawingViewKind.Top,SectionNormal=(Vector3d?)null,SectionOffsetMm=0d,HatchSpacingMm=2.5,Patterns=(SectionHatchPattern[]?)null}));
            var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"Cadoryx.Cli.exe"))
                {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            start.ArgumentList.Add("--native-drawing-worker");start.ArgumentList.Add(directory);
            using var process=Process.Start(start)!;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try{await process.WaitForExitAsync(deadline.Token);}finally{if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}}
            Assert.Equal(1,process.ExitCode);Assert.False(File.Exists(Path.Combine(directory,"native-started.txt")));
            using var response=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"response.json")));
            Assert.Contains("input hash mismatch",response.RootElement.GetProperty("Error").GetString());Assert.Equal(held,f.Assets.Count);
        }
        finally{Directory.Delete(directory,recursive:true);}
        Assert.True(jobs.SetEquals(Jobs(id)));output.WriteLine("Tampered source rejected before native projection; parent assets unchanged.");
    }

    private static string[] Signatures(DrawingProjectionResult result)
    {
        static string Number(double value)=>(Math.Abs(value)<1e-8?0:value).ToString("F8",CultureInfo.InvariantCulture);
        return result.Strokes.Select(s=>
        {
            var points=s.Points.Select(p=>Number(p.X)+","+Number(p.Y)).ToArray();
            string forward=string.Join(";",points),reverse=string.Join(";",points.Reverse());
            return $"{s.Kind}/{s.Hidden}/{s.Closed}/"+(string.CompareOrdinal(forward,reverse)<0?forward:reverse);
        }).Order(StringComparer.Ordinal).ToArray();
    }
    private static async Task<(string Directory,int Pid)> Started(AssetId source,HashSet<string> previous,Task work)
    {
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(30))
        {
            foreach(var dir in Jobs(source).Except(previous))
            {
                string file=Path.Combine(dir,"native-started.txt");
                if(File.Exists(file)&&int.TryParse(await NativeWorkerStartedMarkerTests.ReadPublishedMarker(file),out int pid))return(dir,pid);
            }
            if(work.IsCompleted){await work;throw new Xunit.Sdk.XunitException("Drawing worker finished before native calculation started.");}
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException("Drawing worker did not enter native projection.");
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets {get;}=new();internal OcctGeometryKernel Kernel {get;}
        internal CadDocumentSession Session {get;}private GeometryResult? source;
        internal CadBody Body=>Session.Snapshot.Bodies.Values.Single();
        private Fixture(TimeSpan? budget)
        {
            Kernel=new(null,new IsolatedDrawingExecutor(Path.Combine(AppContext.BaseDirectory,"Cadoryx.Cli.exe"),budget));
            Session=new(Cadoryx.Db.DocumentSnapshot.Create("Independent native drawing"),Assets,Kernel,new InlineSessionDispatcher());
        }
        internal GeometryInstance Input()=>new(Session.Snapshot.EnumerateOccurrences().Single().Path,Body.Id,Body.Geometry,RigidTransform3d.Identity);
        internal static async Task<Fixture> Create(bool modeled=false,TimeSpan? budget=null)
        {
            var f=new Fixture(budget);
            try
            {
                if(modeled)
                {
                    string path=Path.Combine(AppContext.BaseDirectory,"Fixtures","Drawing","modeled-m6-left-blind.brep");
                    Assert.Equal("FE2F36DAEAA88198057B88F958980EEB44F919C3FC77693ACB46FF348C1B09FB",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path))));
                    using var shape=ShapeExchange.ReadBrep(path);f.source=OcctGeometryBridge.StoreShape(shape,f.Assets);
                    await f.Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(f.source.Geometry),"Verified LH tooth solid"));
                }
                else await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(12,10,8,RigidTransform3d.Identity),"Block"));
                return f;
            }
            catch{await f.DisposeAsync();throw;}
        }
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();source?.Dispose();Assert.Equal(0,Assets.Count);}
    }
}
