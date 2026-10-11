using System.Diagnostics;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

[Collection("IsolatedNativeDeadlines")]
public sealed class IsolatedMetricThreadTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string Root=Path.Combine(Path.GetTempPath(),"Cadoryx","ThreadJobs");
    // Other documents/windows may own concurrent jobs. Observe only this fixture's
    // exact source asset instead of treating the shared temporary root as ours.
    private static HashSet<string> Jobs(AssetId source)=>Directory.Exists(Root)?Directory.GetDirectories(Root,"thread-*").Where(dir=>
    {
        try
        {
            using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"request.json")));
            return json.RootElement.GetProperty("Assets").EnumerateArray().Any(id=>id.GetProperty("Sha256").GetString()==source.Sha256);
        }
        catch(IOException){return false;}catch(JsonException){return false;}
    }).ToHashSet():[];

    [Fact] public async Task RealWorkerTransfersSolidHistoryAndToothProofAndReopensWithoutLeakingAssets()
    {
        await using var f=await Fixture.Create();var source=f.Body.Geometry.AssetId;var jobs=Jobs(source);
        await f.Session.ExecuteAsync(new AddHoleCommand(f.Body.Id,f.Recipe(4.25),"Thread"));
        var hole=f.Session.Snapshot.Features.Values.Single(v=>v.Recipe is HoleRecipe);
        Assert.NotNull(hole.TopologyHistory);hole.TopologyHistory.ValidateFor(hole);
        double groove=Math.Tau*4.25*((1d/16+3/Math.Sqrt(3))*(9-6.25)-2/(3*Math.Sqrt(3))*(27-15.625));
        Assert.InRange(Math.Abs(hole.Result.VolumeMm3-(1600*12-62.5*Math.PI-groove)),0,.002);
        var doc=f.Session.Snapshot;var body=doc.Bodies[hole.OutputBodyId];
        using var shape=OcctGeometryBridge.ReadShape(body.Geometry,f.Assets);using var map=RepairSnapshot.Create(shape);
        var tooth=map.Topology.First(t=>
        {
            if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);
            return face.GetFaceSurfaceSnapshot().SurfaceType is not (SurfaceGeometryType.Plane or SurfaceGeometryType.Cylinder);
        });
        var selected=await f.Kernel.ResolveDrawingTopologyAsync(doc,doc.EnumerateOccurrences().Single().Path,
            body.Id,tooth.Selection.Index,map.Fingerprint,f.Assets);
        var proof=await f.Kernel.ResolveDrawingHoleAsync(doc,selected,f.Assets);
        Assert.Equal(selected,proof.Source);Assert.Equal(((HoleRecipe)hole.Recipe).Thread,proof.Thread);
        Assert.Contains(selected.FullTopologyIndex,proof.ProvenFaceIndices);
        output.WriteLine($"Transferred solid {hole.Result.AssetId}, history schema {hole.TopologyHistory.SchemaVersion}, native tooth slot {selected.FullTopologyIndex}, {proof.ProvenFaceIndices.Length} proven faces.");
        using var files=new TestFiles();var storage=new CadDocumentStorage(datumResolver:f.Kernel);
        string path=files.PathFor("isolated-thread.cadoryx");await f.Session.SaveAsync(storage,path);
        using(var reopened=await storage.LoadAsync(path,f.Assets))
            Assert.Equal(hole.Result,reopened.Snapshot.Features[hole.Id].Result);
        Assert.True(jobs.SetEquals(Jobs(source)));
    }

    [Fact] public async Task RealWorkerReplaysTwoHolesWithExactSeedAndCopyIdentitiesAcrossStorage()
    {
        await using var f=await Fixture.Create();var source=f.Body.Geometry.AssetId;var jobs=Jobs(source);
        await f.Session.ExecuteAsync(new AddHoleCommand(f.Body.Id,f.Recipe(4.25),"Thread"));
        var seed=f.Session.Snapshot.Features.Values.Single(v=>v.Recipe is HoleRecipe);var before=f.Session.Snapshot;
        var layout=new RepetitionRecipe(seed.Result,RepetitionKind.Linear,RigidTransform3d.Identity,2,12,
            [Guid.NewGuid(),Guid.NewGuid()],RepetitionOutput.Fused);
        await f.Session.ExecuteAsync(new AddHolePatternCommand(seed.OutputBodyId,new(seed.Id,(HoleRecipe)seed.Recipe,layout),"Thread replay"));
        var current=f.Session.Snapshot;var pattern=current.Features.Values.Single(v=>v.Recipe is HolePatternRecipe);
        Assert.NotNull(pattern.TopologyHistory);pattern.TopologyHistory.ValidateFor(pattern);
        Assert.InRange(Math.Abs(pattern.Result.VolumeMm3-(2*seed.Result.VolumeMm3-1600*12)),0,.002);
        Assert.True(layout.CopyIds.SequenceEqual(((HolePatternRecipe)pattern.Recipe).Layout.CopyIds));
        using var files=new TestFiles();var storage=new CadDocumentStorage(datumResolver:f.Kernel);string path=files.PathFor("isolated-replay.cadoryx");
        await f.Session.SaveAsync(storage,path);using(var reopened=await storage.LoadAsync(path,f.Assets))
            Assert.Equal(pattern.Result,reopened.Snapshot.Features[pattern.Id].Result);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(current,f.Session.Snapshot);
        Assert.True(jobs.SetEquals(Jobs(source)));output.WriteLine($"Two-hole worker replay PASS: {pattern.Result.AssetId}, schema {pattern.TopologyHistory.SchemaVersion}, exact CopyIds and zero remaining jobs.");
    }

    [Fact] public async Task RealWorkerRejectsTamperedSourceBeforeStartingNativeModeling()
    {
        await using var f=await Fixture.Create();var source=f.Body.Geometry.AssetId;var jobs=Jobs(source);int count=f.Assets.Count;
        string directory=Path.Combine(Root,"thread-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        Assert.Equal(Path.GetFullPath(Root),Path.GetDirectoryName(Path.GetFullPath(directory)));
        try
        {
            var id=f.Body.Geometry.AssetId;using var hold=f.Assets.Acquire(id);var bytes=hold.Content.ToArray();bytes[0]^=1;
            await File.WriteAllBytesAsync(Path.Combine(directory,id.Sha256+".brep"),bytes);
            await File.WriteAllTextAsync(Path.Combine(directory,"request.json"),JsonSerializer.Serialize(new
                {Contract=1,Recipe=(GeometryRecipe)f.Recipe(4.25),Assets=new[]{id},Current=(GeometryAssetRef?)null,Selected=(DrawingTopologyReference?)null}));
            var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"Cadoryx.Cli.exe"))
                {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            start.ArgumentList.Add("--native-thread-worker");start.ArgumentList.Add(directory);
            using var process=Process.Start(start)!;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try{await process.WaitForExitAsync(deadline.Token);}finally{if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}}
            Assert.Equal(1,process.ExitCode);Assert.False(File.Exists(Path.Combine(directory,"native-started.txt")));
            using var response=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"response.json")));
            Assert.Contains("input hash mismatch",response.RootElement.GetProperty("Error").GetString());
            Assert.Equal(0,response.RootElement.GetProperty("Diagnostics").GetArrayLength());Assert.Equal(count,f.Assets.Count);
            output.WriteLine("Tampered exact source hash rejected before native modeling; no parent assets retained.");
        }
        finally{Directory.Delete(directory,recursive:true);}
        Assert.True(jobs.SetEquals(Jobs(source)));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task NativeStartedCancellationOrDeadlineKillsOnlyOwnedWorkerAndKeepsSnapshot(bool deadline)
    {
        await using var f=await Fixture.Create(deadline?TimeSpan.FromSeconds(8):null);var source=f.Body.Geometry.AssetId;var jobs=Jobs(source);
        var before=f.Session.Snapshot;int assets=f.Assets.Count;using var cancel=new CancellationTokenSource();
        var watch=Stopwatch.StartNew();var work=f.Session.ExecuteAsync(new AddHoleCommand(f.Body.Id,f.Recipe(9),"Long thread"),cancel.Token);
        var (directory,pid)=await Started(source,jobs,work);
        using var process=Process.GetProcessById(pid);
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"Cadoryx.Cli.exe")),process.MainModule!.FileName);
        await Task.Delay(300);Assert.False(work.IsCompleted,"The native work must still be active at cancellation.");
        if(deadline)
        {
            var error=await Assert.ThrowsAsync<CadValidationException>(()=>work);
            Assert.Contains("exceeded 8 seconds",error.Message);Assert.InRange(watch.Elapsed.TotalSeconds,7,18);
        }
        else
        {
            watch.Restart();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>work);
            Assert.InRange(watch.Elapsed.TotalSeconds,0,5);
        }
        Assert.True(process.HasExited);Assert.False(Directory.Exists(directory));Assert.True(jobs.SetEquals(Jobs(source)));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(assets,f.Assets.Count);
        output.WriteLine($"Native start PID {pid}; {(deadline?"deadline":"cancellation")} returned in {watch.Elapsed.TotalSeconds:F3}s; owned process exited, job directory removed, snapshot/assets unchanged.");
        // The parent's serial native queue is available again after the child dies.
        using var box=await f.Kernel.EvaluateAsync(new BoxRecipe(1,2,3,RigidTransform3d.Identity),f.Assets);
        Assert.Equal(6,box.Geometry.VolumeMm3,6);
    }

    [Fact] public async Task WorkerValidationFailureAndPreCancellationKeepAssetsAndTemporaryRoot()
    {
        await using var f=await Fixture.Create();var source=f.Body.Geometry.AssetId;var jobs=Jobs(source);var before=f.Session.Snapshot;int held=f.Assets.Count;
        // Author-valid but geometrically outside the part; it must be rejected by the real worker.
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new AddHoleCommand(f.Body.Id,
            f.Recipe(4.25) with{Position=new(100,100)},"Outside")));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Session.ExecuteAsync(new AddHoleCommand(f.Body.Id,f.Recipe(9),"Canceled"),cancel.Token));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(held,f.Assets.Count);Assert.True(jobs.SetEquals(Jobs(source)));
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
            if(work.IsCompleted){await work;throw new Xunit.Sdk.XunitException("Worker finished before native work started.");}
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException("Worker did not begin native thread construction.");
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets {get;}=new();internal OcctGeometryKernel Kernel {get;}
        internal CadDocumentSession Session {get;}
        internal CadBody Body=>Session.Snapshot.Bodies.Values.Single();
        private Fixture(TimeSpan? budget)
        {
            Kernel=new(new IsolatedMetricThreadExecutor(Path.Combine(AppContext.BaseDirectory,"Cadoryx.Cli.exe"),budget));
            Session=new(Cadoryx.Db.DocumentSnapshot.Create("Isolated thread"),Assets,Kernel,new InlineSessionDispatcher());
        }
        internal static async Task<Fixture> Create(TimeSpan? budget=null)
        {
            var f=new Fixture(budget);
            try{await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(40,40,12,RigidTransform3d.Identity),"Block"));return f;}
            catch{await f.Session.DisposeAsync();throw;}
        }
        internal HoleRecipe Recipe(double depth)=>new(Body.Geometry,RigidTransform3d.Translate(0,0,12),new(12,20),5,false,10,
            Thread:MetricThreadHoles.Create(6,ThreadHand.Right,false,depth,ThreadHoleRepresentation.Modeled));
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
