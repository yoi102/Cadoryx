using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Antelcat.I18N.WPF;
using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Lang;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf;
using Cadoryx.wpf.Services.Application;
using Cadoryx.wpf.Views;
using CommunityToolkit.Mvvm.DependencyInjection;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;
using Strings=Cadoryx.Lang.Strings.Strings;

// This host sets up one owned fixture and observes the production editor.
// It never invokes an editor command or injects keyboard/mouse/UIA input.
internal static class WindowHost
{
    static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static void Write(string output,string name,object value)=>File.WriteAllText(Path.Combine(output,name),JsonSerializer.Serialize(value,Json));
    static double Expected(string kind)=>kind switch
    {
        "sphere"=>Math.PI*4*12-2*Math.PI/3*(216-Math.Pow(32,1.5)),
        "cylinder"=>4*(48-(2*Math.Sqrt(32)+36*Math.Asin(1d/3))),
        _=>96
    };
    static T Control<T>(Window window,string name) where T:FrameworkElement=>window.FindName(name) as T??throw new InvalidOperationException("Missing production control "+name);
    [STAThread]
    static int Main(string[] args)
    {
        Require((args.Length==5||args.Length==6)&&args[0]=="--d6-newbody-ui-smoke","Expected smoke output fixture language close route and optional create/edit phase.");
        string output=Path.GetFullPath(args[1]),fixture=Path.GetFullPath(args[2]);
        Require(!Directory.Exists(output)&&File.Exists(fixture),"Fresh output and existing exact fixture required.");
        Require(args[3] is "en-US" or "zh-CN" or "ja-JP"&&args[4] is "confirm" or "cancel" or "close","Unsupported UI matrix entry.");
        string phase=args.Length==6?args[5]:"edit";Require(phase is "create" or "edit","Unsupported UI author phase.");
        Directory.CreateDirectory(output);
        // App.InitializeComponent uses its explicit component assembly URI; do not overwrite WPF ResourceAssembly.
        var app=new App();app.InitializeComponent();
        app.Startup+=(_,_)=>app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(async()=>
        {
            try{await ObserveAsync(app,output,fixture,args[3],args[4],phase);app.Shutdown(0);}
            catch(Exception error)
            {
                Write(output,"failure.json",new{recordedAt=DateTimeOffset.UtcNow,state="FAIL_OWNED_D6_WINDOW_OBSERVER",error=error.ToString(),desktopInputInjectedByHost=false,cleanup=false,AF="ACTIVE"});
                app.Shutdown(1);
            }
        }));
        return app.Run();
    }
    static async Task ObserveAsync(App app,string output,string fixture,string language,string route,string phase)
    {
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));
        using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        try
        {
            var mainWindow=(MainWindow)app.MainWindow;var main=(MainWindowViewModel)mainWindow.DataContext;
            var storage=Ioc.Default.GetRequiredService<IDocumentStorage>();var kernel=Ioc.Default.GetRequiredService<IGeometryKernel>();
            Require(kernel is OcctGeometryKernel,"Production OCCT kernel required.");
            Require(main.ActiveDocument is {} initial&&initial.Session.Snapshot.Features.Count==0,"Fresh initial diagnostic document required.");
            await main.ActiveDocument!.Session.SaveAsync(storage,Path.Combine(output,"initial-empty.cadoryx"));
            main.CurrentCultureLCID=CultureInfo.GetCultureInfo(language).LCID;
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await main.OpenPathAsync(fixture);
            var document=main.ActiveDocument??throw new InvalidOperationException("Fixture did not open.");
            var session=document.Session;
            var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            var recipe=(ExtrudeRecipe)feature.Recipe;var options=recipe.Options!;
            Require(options.Operation==ExtrusionOperation.NewBody&&options.Termination==ExtrusionTermination.FiniteSurface&&
                options.Source is null&&options.SupportFace is null&&feature.Inputs.IsEmpty,"Exact independent fixture required.");
            string label=Path.GetFileNameWithoutExtension(fixture),kind=label.Split('-')[0];double expected=Expected(kind);
            if(phase=="create")
            {
                if(kind=="sphere")
                {
                    var draft=new SketchDraft(CadSketch.Create(feature.PartId,"D6 creation circle",recipe.Placement));draft.AddCircle(new(5,5),2);
                    await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,Ioc.Default.GetRequiredService<ISketchConstraintSolver>()));
                }
                document.StartTool("Extrude");document.SelectedTargetPart=feature.PartId;document.ProfilePoints.Clear();
                foreach(var p in new[]{new Point2d(3,3),new(7,3),new(7,7),new(3,7)})document.ProfilePoints.Add(new(p.X,p.Y));
            }
            await session.SaveAsync(storage,Path.Combine(output,"before-editor.cadoryx"));
            var baseline=session.Snapshot;var baselineHistory=session.HistoryUsage;
            mainWindow.Title="D6 owned UI acceptance "+language+" "+label+" "+phase+" "+route;
            Write(output,"fixture-opened.json",new{language,route,phase,label,fixture,fixtureSha256=Hash(fixture),documentId=baseline.Id,
                baselineStateId=baseline.StateId,featureId=feature.Id,requiredLimit=options.LimitFace,requiredSide=options.Side,
                requiredFrame=recipe.Placement,requiredCircleProfile=kind=="sphere"?"D6 creation circle":null,
                expectedVolumeMm3=expected,hostDoesNotInjectInput=true,productionTreeEditEvent=phase=="edit",productionCreationCommand=phase=="create"});
            // Only open the production route. All author controls and preview,
            // confirm/cancel actions are operated externally through Computer Use.
            // Queue the opening route so the observer reaches its first await
            // before ShowDialog enters the nested Dispatcher message loop.
            Task productionOpen=app.Dispatcher.InvokeAsync(async()=>
            {
                if(phase=="edit")document.EditFeature(feature.Id);
                else await main.EditExtrusionsCommand.ExecuteAsync(null);
            },DispatcherPriority.Normal).Task.Unwrap();
            var readiness=Stopwatch.StartNew();ExtrusionEditorWindow? host=null;
            while(host is null)
            {
                Require(readiness.Elapsed.TotalSeconds<30,"Production modal readiness exceeded original30s.");
                await Task.Delay(70);host=app.Windows.OfType<ExtrusionEditorWindow>().SingleOrDefault();
            }
            var editor=host.Editor;
            Require(host.Owner==mainWindow&&editor.IsNew==(phase=="create"),"Production owned creation/edit route differs.");
            Require(editor.Operation==ExtrusionOperation.NewBody&&!editor.CanChooseSupport&&editor.SelectedSupport is null,"Editor requires private support.");
            Require(!Control<ComboBox>(host,"SupportPicker").IsEnabled&&!Control<ComboBox>(host,"TargetPicker").IsEnabled,"Production bindings enable a target/support.");
            Require(host.Title==Strings.ExtrusionAdvanced,"Production dialog title is not localized.");
            bool closed=false;host.Closed+=(_,_)=>closed=true;
            var evidence=new HashSet<string>();var transitions=new List<object>();Task? lastPreview=null;string? prior=null;
            var watch=Stopwatch.StartNew();
            while(!closed)
            {
                Require(watch.Elapsed.TotalSeconds<300,"D6 window observation exceeded300s; original native and command budgets are unchanged.");
                Require(!editor.CanChooseSupport&&editor.SelectedSupport is null,"New body acquired a support requirement.");
                bool unchanged=ReferenceEquals(baseline,session.Snapshot),scene=document.PreviewScene is not null;
                bool correctPreview=document.PreviewScene?.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-expected)<1e-5)==true;
                var load=editor.LoadFacesCommand.ExecutionTask;var preview=editor.PreviewCommand.ExecutionTask;
                if(load is {IsCompleted:true}&&editor.SelectedSurface is null&&!editor.IsWorking&&unchanged)
                    evidence.Add("INVENTORY_CLEARED_EXPLICIT_SELECTION");
                if(preview is {IsCompleted:true}&&!ReferenceEquals(preview,lastPreview)&&!editor.IsWorking)
                {
                    lastPreview=preview;
                    if(editor.SelectedSurface is null&&!editor.CanConfirm&&!scene&&unchanged&&evidence.Contains("INVENTORY_CLEARED_EXPLICIT_SELECTION"))
                        evidence.Add("MISSING_LIMIT_PREVIEW_REJECTED");
                    if(editor.SelectedSurface?.Limit.Selection==options.LimitFace&&editor.SurfaceBranch==ExtrusionSurfaceBranch.Near&&editor.CanConfirm&&correctPreview&&unchanged&&evidence.Contains("MISSING_LIMIT_PREVIEW_REJECTED"))
                        evidence.Add(evidence.Contains("FAR_PREVIEW_REJECTED")?"NEAR_PREVIEW_RESTORED":"NEAR_PREVIEW_READY");
                    if(editor.SurfaceBranch==ExtrusionSurfaceBranch.Far&&!editor.CanConfirm&&!scene&&unchanged&&evidence.Contains("BRANCH_INVALIDATED_READY_PREVIEW"))
                        evidence.Add("FAR_PREVIEW_REJECTED");
                }
                if(editor.SurfaceBranch==ExtrusionSurfaceBranch.Far&&!editor.CanConfirm&&!scene&&unchanged&&evidence.Contains("NEAR_PREVIEW_READY"))
                    evidence.Add("BRANCH_INVALIDATED_READY_PREVIEW");
                string state=string.Join('|',editor.IsWorking,editor.SurfaceBranch,editor.CanConfirm,editor.SelectedSurface is not null,unchanged,scene,editor.Status,evidence.Count);
                if(state!=prior)
                {
                    prior=state;transitions.Add(new{recordedAt=DateTimeOffset.UtcNow,elapsedMs=watch.Elapsed.TotalMilliseconds,
                        editor.IsWorking,editor.SurfaceBranch,editor.CanConfirm,explicitLimit=editor.SelectedSurface?.Limit.Selection,
                        originalSnapshotUnchanged=unchanged,hasPreview=scene,correctPreview,editor.Status,evidence=evidence.Order().ToArray()});
                    Write(output,"state.json",new{language,route,phase,label,modalTitle=host.Title,modalVisible=host.IsVisible,mainTitle=mainWindow.Title,evidence=evidence.Order().ToArray(),transitions});
                }
                await Task.Delay(70);
            }
            await productionOpen;
            var settled=Stopwatch.StartNew();
            while(main.IsBusy)
            {
                Require(settled.Elapsed.TotalSeconds<30,"Production modal completion exceeded original30s.");
                await Task.Delay(70);
            }
            Require(evidence.SetEquals(new[]{"INVENTORY_CLEARED_EXPLICIT_SELECTION","MISSING_LIMIT_PREVIEW_REJECTED","NEAR_PREVIEW_READY","BRANCH_INVALIDATED_READY_PREVIEW","FAR_PREVIEW_REJECTED","NEAR_PREVIEW_RESTORED"}),"Required actual control trace incomplete.");
            var accepted=session.Snapshot;
            FeatureId outputFeature=feature.Id;
            if(route=="confirm")
            {
                Require(!ReferenceEquals(baseline,accepted),"Confirm did not commit.");accepted.Validate();
                if(phase=="create")outputFeature=accepted.Features.Values.Single(f=>!baseline.Features.ContainsKey(f.Id)).Id;
                var actual=accepted.Features[outputFeature];var author=(ExtrudeRecipe)actual.Recipe;
                Require(author.Options==options&&actual.Inputs.IsEmpty&&Math.Abs(actual.Result.VolumeMm3-expected)<1e-5,"Confirmed independent author differs.");
                if(phase=="create")Require(accepted.Bodies.Count==baseline.Bodies.Count+1,"Creation did not add one independent body.");
                foreach(var body in baseline.Bodies.Values.Where(b=>phase=="create"||b.Producer!=feature.Id))Require(accepted.Bodies[body.Id]==body,"Original body consumed by confirm.");
                await session.UndoAsync();Require(ReferenceEquals(baseline,session.Snapshot),"Single undo did not restore original snapshot.");
                await session.RedoAsync();Require(ReferenceEquals(accepted,session.Snapshot),"Single redo did not restore accepted snapshot.");
            }
            else Require(ReferenceEquals(baseline,accepted)&&session.HistoryUsage==baselineHistory,"Cancel/close changed document or history.");
            await ((OcctGeometryKernel)kernel).ConfirmFiniteSurfaceExtrusionAsync((ExtrudeRecipe)accepted.Features[outputFeature].Recipe,accepted.Features[outputFeature].Result,session.Assets);
            string saved=Path.Combine(output,"actual-editor-result.cadoryx");await session.SaveAsync(storage,saved);
            using(var loaded=await storage.LoadAsync(saved,session.Assets))
            {
                loaded.Snapshot.Validate();var restored=loaded.Snapshot.Features[outputFeature];
                Require(((ExtrudeRecipe)restored.Recipe).Options==((ExtrudeRecipe)accepted.Features[outputFeature].Recipe).Options&&
                    restored.Inputs.IsEmpty&&Math.Abs(restored.Result.VolumeMm3-expected)<1e-5,"Actual editor result failed exact reopen.");
            }
            Require(await main.CloseAllAsync(),"Owned saved diagnostic documents did not close.");
            await Ioc.Default.GetRequiredService<RecoveryHost>().StopAsync();await Task.Delay(70);
            int assetCount=((IAssetStoreStatistics)Ioc.Default.GetRequiredService<IAssetStore>()).Count;
            Require(assetCount==0,"Diagnostic documents retained assets.");listener.Flush();bindings.Flush();
            Require(new FileInfo(Path.Combine(output,"bindings.log")).Length==0,"Production window binding errors.");
            using var process=Process.GetCurrentProcess();string nativeDirectory=Path.Combine(AppContext.BaseDirectory,"occt");
            var native=process.Modules.Cast<ProcessModule>().Where(m=>string.Equals(Path.GetDirectoryName(m.FileName),nativeDirectory.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))
                .Select(m=>new{name=m.ModuleName,path=m.FileName,sha256=Hash(m.FileName!)}).ToArray();
            Require(native.Length==62,"Actual window native62 binding missing.");
            Write(output,"result.json",new{state="D6_PRODUCTION_WINDOW_CONTROL_TRACE_AND_DOCUMENT_CHECKS_COMPLETE_PENDING_DESKTOP_REVIEW",language,route,phase,label,outputFeature,
                expectedVolumeMm3=expected,saved,sha256=Hash(saved),native,evidence=evidence.Order().ToArray(),transitions,assetsRemaining=assetCount,
                hostInjectedInput=false,externalDesktopReviewRequired=true,performanceAcceptance=false,productionApplied=false,cleanup=false,AF="ACTIVE",G="USER_DEFERRED"});
        }
        finally{PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
}

