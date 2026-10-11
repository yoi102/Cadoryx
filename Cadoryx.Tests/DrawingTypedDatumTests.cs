using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed partial class DrawingAnnotationTests
{
    private static async Task<(DrawingTopologyReference Target,DrawingDatumSymbol A,DrawingDatumSymbol B,DrawingDatumSymbol C)> SizeDatums(Fixture f)
    {
        await f.Init(cylinder:true);var original=f.Session.Snapshot.Bodies.Values.Single();
        var target=await f.Reference(cylinder:true);
        var part=f.Session.Snapshot.Definitions.Values.OfType<PartDefinition>().Single();
        await f.Session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(2,10,RigidTransform3d.Translate(0,0,25)),"Datum A",part.Id));
        await f.Session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(3,10,RigidTransform3d.Translate(0,0,40)),"Datum B",part.Id));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
        async Task<DrawingDatumSymbol> Axis(string name,double x)
        {
            var body=f.Session.Snapshot.Bodies.Values.Single(b=>b.Name=="Datum "+name);
            var d=new DrawingDatumSymbol(Guid.NewGuid(),f.View,name,await f.Reference(cylinder:true,bodyId:body.Id),new(x,25))
                {FeatureType=DrawingDatumFeatureType.CylinderSizeAxis};
            await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,d));
            return f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single(s=>s.Id==d.Id);
        }
        var a=await Axis("A",25);var b=await Axis("B",45);
        var c=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"C",await f.Reference(plane:true,bodyId:original.Id),new(65,25));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,c));return (target,a,b,c);
    }
    private static DrawingFeatureControlFrame TypedFrame(Fixture f,DrawingTopologyReference target,string system="A(M)-B(L),C")=>
        new(Guid.NewGuid(),f.View,target,new(70,60),DrawingGeometricCharacteristic.Position,.025,DrawingToleranceTarget.Axis,true,DrawingMaterialCondition.Maximum)
        {DatumSystem=DrawingDatumSystemText.Parse(system,f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols)};
    private static void StripTypedFieldsForLegacy(string path)
    {
        FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {
        var book=JsonNode.Parse(MessagePackSerializer.ConvertToJson(bytes))!.AsArray();
        foreach(var sheet in book[0]!.AsArray())
        {
            foreach(var datum in sheet![11]!.AsArray()){var d=datum!.AsArray();while(d.Count>6)d.RemoveAt(d.Count-1);}
            foreach(var frame in sheet[12]!.AsArray()){var f=frame!.AsArray();while(f.Count>11)f.RemoveAt(f.Count-1);}
        }
        return MessagePackSerializer.ConvertFromJson(book.ToJsonString());
        });
        StripDrawingHistoryFieldsForLegacy(path);
    }

    [Theory][InlineData(DrawingStandard.ISO)][InlineData(DrawingStandard.GB)][InlineData(DrawingStandard.ASME)]
    public async Task TypedCommonAndOrderedDatumsKeepBoundaryOnEachReferenceThroughRenameStorageAndUndo(DrawingStandard standard)
    {
        await using var f=new Fixture();var (target,a,b,c)=await SizeDatums(f);using var files=new TestFiles();
        await f.Session.ExecuteAsync(new EditDocumentCommand("Standard",d=>d with{DrawingSheets=d.DrawingSheets.SetItem(f.Sheet,d.DrawingSheets[f.Sheet] with{Standard=standard})}));
        var authored=TypedFrame(f,target);await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,authored));
        var frame=f.Session.Snapshot.DrawingSheets[f.Sheet].ControlFrames.Single();
        Assert.Equal(2,frame.DatumSystem.Length);Assert.Equal(2,frame.DatumSystem[0].References.Length);
        Assert.Equal(new[]{a.Id,b.Id,c.Id},frame.ReferencedDatumIds.ToArray());Assert.Empty(frame.Datums);Assert.NotNull(frame.SizeEvidence);
        Assert.True(a.SizeEvidence!.SameProof(await f.Kernel.ResolveDrawingCylinderSizeAsync(f.Session.Snapshot,a.Source,f.Assets)));
        var labels=DrawingPageLayout.Create(f.Session.Snapshot.DrawingSheets[f.Sheet],1,1).Labels;
        Assert.Equal(2,labels.Count(l=>l.Text=="M"));Assert.Single(labels.Where(l=>l.Text=="L"));Assert.Single(labels.Where(l=>l.Text=="-"));
        var before=f.Session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingAnnotationCommands.Delete(f.Sheet,a.Id)));Assert.Same(before,f.Session.Snapshot);
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.RenameDatum(f.Sheet,a.Id,"D"));
        Assert.Equal("D(M)-B(L),C",DrawingDatumSystemText.Format(frame,f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols));
        var path=files.PathFor("typed.cadoryx");var storage=new CadDocumentStorage(datumResolver:f.Kernel);await f.Session.SaveAsync(storage,path);
        await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));
        using(var loaded=await storage.LoadAsync(path,f.Assets))
        {
            var actual=loaded.Snapshot.DrawingSheets[f.Sheet].ControlFrames.Single();Assert.True(frame.SizeEvidence!.SameProof(actual.SizeEvidence!));
            Assert.Equal(frame.DatumBindings.ToArray(),actual.DatumBindings.ToArray());
            Assert.True(frame.DatumSystem[0].SameReferences(actual.DatumSystem[0]));
            TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("typed.pdf"));
        }
        var old=File.ReadAllBytes(path);await Assert.ThrowsAsync<NotSupportedException>(()=>f.Session.SaveAsync(new CadDocumentStorage(),path));Assert.Equal(old,File.ReadAllBytes(path));
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Equal("D",f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols[0].Name);
    }

    [Theory][InlineData("self")][InlineData("repeat")][InlineData("four")][InlineData("missing")][InlineData("surface-boundary")]
    [InlineData("mixed-common")][InlineData("duplicate-feature")][InlineData("ambiguous-arrays")][InlineData("fake-size")][InlineData("wrong-binding")][InlineData("foreign-occurrence")]
    public async Task TypedManufacturingRulesRejectInvalidCombinationsAtomically(string mode)
    {
        await using var f=new Fixture();var (target,a,b,c)=await SizeDatums(f);var frame=TypedFrame(f,target);
        if(mode=="duplicate-feature")
        {
            var d=a with{Id=Guid.NewGuid(),Name="D"};await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,d));
            frame=TypedFrame(f,target,"A,D");
        }
        else if(mode=="foreign-occurrence")
        {
            var doc=f.Session.Snapshot;var part=doc.Definitions.Values.OfType<PartDefinition>().Single();var slot=ComponentSlotId.New();
            await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new(doc.Id,[]),part.Id,slot,"Other",RigidTransform3d.Translate(30,0,0)));
            await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
            var source=await f.Reference(cylinder:true,path:new(doc.Id,[slot]),bodyId:target.BodyId);
            frame=TypedFrame(f,source);
        }
        else frame=mode switch
        {
            "self"=>TypedFrame(f,a.Source,"A"),"repeat"=>TypedFrame(f,target,"A-B,A"),
            "four"=>frame with{DatumSystem=[new([new(a.Id)]),new([new(b.Id)]),new([new(c.Id)]),new([new(a.Id)])]},
            "missing"=>frame with{DatumSystem=[new([new(Guid.NewGuid())])]},
            "surface-boundary"=>TypedFrame(f,target,"C(M)"),"mixed-common"=>TypedFrame(f,target,"A-C"),
            "ambiguous-arrays"=>frame with{Datums=[c.Id]},
            "fake-size"=>frame with{SizeEvidence=a.SizeEvidence},
            "wrong-binding"=>frame with{DatumBindings=[new(a.Id,a.FeatureType,b.Source),new(b.Id,b.FeatureType,b.Source),new(c.Id,c.FeatureType,c.Source)]},_=>frame
        };
        var before=f.Session.Snapshot;var count=f.Assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame)));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
    }

    [Fact] public async Task CommonAxisRejectsParallelDisplacedSizeFeatures()
    {
        await using var f=new Fixture();var (target,_,b,_)=await SizeDatums(f);var doc=f.Session.Snapshot;
        var producer=doc.Features[b.Source.FeatureId!.Value];await f.Session.ExecuteAsync(new RecomputeCommand(producer.Id,new CylinderRecipe(3,10,RigidTransform3d.Translate(5,0,40))));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,b with{Source=await f.Reference(cylinder:true,bodyId:b.Source.BodyId),SizeEvidence=null},true));
        var before=f.Session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,TypedFrame(f,target))));Assert.Same(before,f.Session.Snapshot);
    }

    [Fact] public async Task DatumReselectionCannotSilentlyRefreshAFrameAndOldProofSurvivesGeometryChanges()
    {
        await using var f=new Fixture();var (target,a,_,_)=await SizeDatums(f);await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,TypedFrame(f,target,"A(M),C")));
        var before=f.Session.Snapshot;var old=before.DrawingSheets[f.Sheet].ControlFrames.Single();var producer=before.Features[a.Source.FeatureId!.Value];
        await f.Session.ExecuteAsync(new RecomputeCommand(producer.Id,new CylinderRecipe(2.5,10,RigidTransform3d.Translate(0,0,25))));
        var stale=f.Session.Snapshot.DrawingSheets[f.Sheet];Assert.NotNull(stale.DatumSymbols.Single(d=>d.Id==a.Id).StaleReason);Assert.NotNull(stale.ControlFrames.Single().StaleReason);
        Assert.True(a.SizeEvidence!.SameProof(stale.DatumSymbols.Single(d=>d.Id==a.Id).SizeEvidence!));Assert.Equal(old.DatumBindings.ToArray(),stale.ControlFrames.Single().DatumBindings.ToArray());
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,a with{Source=await f.Reference(cylinder:true,bodyId:a.Source.BodyId),SizeEvidence=null},true));
        // A normal refresh must compare authored datum sources rather than adopting the new binding.
        await f.Session.ExecuteAsync(new RefreshDrawingCachesCommand());
        Assert.NotNull(f.Session.Snapshot.DrawingSheets[f.Sheet].ControlFrames.Single().StaleReason);
        using var files=new TestFiles();var path=files.PathFor("stale.cadoryx");var storage=new CadDocumentStorage(datumResolver:f.Kernel);await f.Session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,f.Assets))Assert.NotNull(loaded.Snapshot.DrawingSheets[f.Sheet].ControlFrames.Single().StaleReason);
        Assert.Throws<CadValidationException>(()=>TechnicalDrawingPdf.Write(f.Session.Snapshot,files.PathFor("invalid.pdf")));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,old with{DatumBindings=[]},true));
        Assert.Null(f.Session.Snapshot.DrawingSheets[f.Sheet].ControlFrames.Single().StaleReason);
        for(int i=0;i<4;i++)await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
    }

    [Theory][InlineData("diameter")][InlineData("rings")][InlineData("type")][InlineData("nil-groups")][InlineData("duplicate-reference")][InlineData("changed-binding")]
    public async Task TypedCacheAndDatumSystemForgeryFailsLoadingWithoutPublishingAssets(string mutation)
    {
        await using var f=new Fixture();var (target,_,_,_)=await SizeDatums(f);await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,TypedFrame(f,target)));
        using var files=new TestFiles();var path=files.PathFor("forged.cadoryx");var storage=new CadDocumentStorage(datumResolver:f.Kernel);await f.Session.SaveAsync(storage,path);
        FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {
            var book=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);var s=book.Sheets[0];var d=s.Datums![0];var frame=s.Frames![0];
            s=mutation switch
            {
                "diameter"=>s with{Datums=[d with{Size=d.Size! with{Diameter=999}},s.Datums[1],s.Datums[2]]},
                "rings"=>s with{Datums=[d with{Size=d.Size! with{Rings=[99998,99999]}},s.Datums[1],s.Datums[2]]},
                "type"=>s with{Datums=[d with{FeatureType=99},s.Datums[1],s.Datums[2]]},
                "nil-groups"=>s with{Frames=[frame with{DatumSystem=null}]},
                "duplicate-reference"=>s with{Frames=[frame with{DatumSystem=[new([new(d.Id,1),new(d.Id,2)])]}]},
                _=>s with{Frames=[frame with{DatumBindings=frame.DatumBindings!.Select((b,i)=>i==0?b with{Source=s.Datums[1].Source}:b).ToArray()}]}
            };return MessagePackSerializer.Serialize(book with{Sheets=[s]});
        });
        int count=f.Assets.Count;await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }

    [Theory][InlineData(6)][InlineData(7)][InlineData(8)][InlineData(9)]
    public async Task GenuineLegacySurfaceFramesMigrateThroughFrozenIntermediateFormatsAndNewNilKeysAreRejected(int version)
    {
        await using var f=new Fixture();await f.Init();var source=await f.Reference();var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",source,new(25,25));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,new DrawingFeatureControlFrame(Guid.NewGuid(),f.View,await f.Reference(excluding:source.FullTopologyIndex),new(60,25),DrawingGeometricCharacteristic.Position,.02){Datums=[datum.Id]}));
        using var files=new TestFiles();var path=files.PathFor("old.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="drawings"?s with{SchemaVersion=version}:s).ToImmutableArray()});
        // A new key with an explicit nil still isn't an old-format field.
        FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {var b=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);return MessagePackSerializer.Serialize(b with{Sheets=b.Sheets.Select(s=>s with{Datums=s.Datums!.Select(d=>d with{Size=null}).ToArray()}).ToArray()});});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));
        StripTypedFieldsForLegacy(path);
        if(version==6)DrawingTemplateLibraryTests.StripLibraryFieldsForLegacy(path);
        else if(version==7)FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {var b=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);return MessagePackSerializer.Serialize(b with{Sheets=b.Sheets.Select(s=>s with{HoleTables=null}).ToArray()});});
        // The previous helper uses today's DTO; freeze the numeric-key lengths again after it.
        StripTypedFieldsForLegacy(path);
        using var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets);var actual=loaded.Snapshot.DrawingSheets[f.Sheet];
        Assert.Equal(datum.Source,actual.DatumSymbols[0].Source);Assert.Equal(datum.Id,actual.ControlFrames[0].ReferencedDatumIds.Single());Assert.Null(actual.ControlFrames[0].StaleReason);
        Assert.Equal(f.Session.Snapshot.DrawingSheets[f.Sheet].Views.ToArray(),actual.Views.ToArray(),TechnicalDrawingViewComparer.Instance);
    }
    private sealed class TechnicalDrawingViewComparer:IEqualityComparer<TechnicalDrawingView>
    {
        internal static TechnicalDrawingViewComparer Instance {get;}=new();
        public bool Equals(TechnicalDrawingView? a,TechnicalDrawingView? b)=>a is not null&&b is not null&&a with{Sources=b.Sources,Strokes=b.Strokes}==b&&a.Sources.SequenceEqual(b.Sources)&&
            a.Strokes.Length==b.Strokes.Length&&a.Strokes.Select((s,i)=>s with{Points=b.Strokes[i].Points}==b.Strokes[i]&&s.Points.SequenceEqual(b.Strokes[i].Points)).All(equal=>equal);
        public int GetHashCode(TechnicalDrawingView v)=>v.Id.GetHashCode();
    }
    [Fact] public async Task LegacyAxisConditionIsPreservedButCannotInventOriginalSizeProof()
    {
        await using var f=new Fixture();var (target,_,_,c)=await SizeDatums(f);
        var authored=TypedFrame(f,target) with{Datums=[c.Id],DatumSystem=[]};await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,authored));
        using var files=new TestFiles();var path=files.PathFor("v9-axis.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(datumResolver:f.Kernel),path);
        StripTypedFieldsForLegacy(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="drawings"?s with{SchemaVersion=9}:s).ToImmutableArray()});
        using var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets);var frame=loaded.Snapshot.DrawingSheets[f.Sheet].ControlFrames.Single();
        Assert.Equal(authored.Source,frame.Source);Assert.Equal(authored.Id,frame.Id);Assert.Equal(DrawingMaterialCondition.Maximum,frame.MaterialCondition);
        Assert.NotNull(frame.StaleReason);Assert.Null(frame.SizeEvidence);Assert.Equal(DrawingDatumFeatureType.Surface,loaded.Snapshot.DrawingSheets[f.Sheet].DatumSymbols[0].FeatureType);
        Assert.Throws<CadValidationException>(()=>TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("invalid.pdf")));
        await new CadDocumentStorage().SaveAsync(loaded.Snapshot,f.Assets,path);
    }
    private sealed class RefreshDrawingCachesCommand:ICadDocumentCommand
    {
        public string Name=>"Refresh drawing caches";
        public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)=>
            UpdateTechnicalDrawings.PrepareAsync(context,context.Snapshot.WithNewState(),token);
    }
    [Theory][InlineData("cancel")][InlineData("close")][InlineData("change")]
    public async Task LateOriginalSizeEvidenceCannotPublishAfterCancelCloseOrStateChange(string mode)
    {
        await using var f=new Fixture();var (_,a,_,_)=await SizeDatums(f);var gate=new GatedSizeKernel(f.Kernel);
        var candidate=a with{Id=Guid.NewGuid(),Name="D"};var before=f.Session.Snapshot;using var cancel=new CancellationTokenSource();
        var pending=f.Session.ExecuteAsync(new SizeKernelCommand(DrawingAnnotationCommands.PutDatum(f.Sheet,candidate),gate),cancel.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task? closing=null;
        if(mode=="cancel")cancel.Cancel();else if(mode=="close")closing=f.Session.DisposeAsync().AsTask();
        else await f.Session.ExecuteAsync(ResourceCommands.AddMaterial(new(MaterialId.New(),"Other edit",1)));
        gate.Release.TrySetResult();
        if(mode=="change")await Assert.ThrowsAsync<StaleDocumentException>(()=>pending);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        if(closing is not null){await closing;Assert.Equal(0,f.Assets.Count);}
        else if(mode=="cancel")Assert.Same(before,f.Session.Snapshot);
        else Assert.DoesNotContain(f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols,d=>d.Id==candidate.Id);
    }
    private sealed class SizeKernelCommand(ICadDocumentCommand inner,GatedSizeKernel kernel):ICadDocumentCommand
    {
        public string Name=>inner.Name;
        public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)=>inner.PrepareAsync(context with{Kernel=kernel},token);
    }
    private sealed class GatedSizeKernel(OcctGeometryKernel native):IGeometryKernel,IDrawingTopologyResolver,IDrawingSizeResolver
    {
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)=>native.EvaluateAsync(recipe,assets,token);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(Cadoryx.Db.DocumentSnapshot doc,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,token);
        public Task<CadExportReport> ExportAsync(Cadoryx.Db.DocumentSnapshot doc,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,options,token);
        public Task<DrawingTopologyReference> ResolveDrawingTopologyAsync(Cadoryx.Db.DocumentSnapshot doc,OccurrencePath path,BodyId body,int index,string fingerprint,IAssetStore assets,CancellationToken token=default)=>
            native.ResolveDrawingTopologyAsync(doc,path,body,index,fingerprint,assets,token);
        public async Task<DrawingCylinderSizeEvidence> ResolveDrawingCylinderSizeAsync(Cadoryx.Db.DocumentSnapshot doc,DrawingTopologyReference source,IAssetStore assets,CancellationToken token=default)
        {
            var pending=native.ResolveDrawingCylinderSizeAsync(doc,source,assets,CancellationToken.None);Entered.TrySetResult();await Release.Task;return await pending;
        }
    }
}
