using System.Collections.Immutable;
using System.IO.Compression;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Occt;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Xunit;

namespace Cadoryx.Tests;

public sealed class DatumPlaneTests
{
    private static DatumBook Book(params CadDatumPlane[] planes)=>DatumBook.Empty with{Planes=planes.ToImmutableDictionary(p=>p.Id)};
    private static CadDatumPlane Offset(DefinitionId part,string name,double offset=0)=>new(Guid.NewGuid(),part,name,DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY),Value:offset);
    private static ParameterBinding Drive(CadParameter p,ParameterTarget target)=>new(Guid.NewGuid(),p.Id,target);
    [Theory][InlineData(0)][InlineData(90)][InlineData(180)][InlineData(-90)]
    public async Task OffsetAngleAndAntiparallelMidplaneHaveDeterministicLocalFrames(double degrees)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var a=Offset(part,"A",10);var b=Offset(part,"B",30);
            var middle=new CadDatumPlane(Guid.NewGuid(),part,"Mid",DatumPlaneKind.Midplane,DatumPlaneSource.Named(a.Id),DatumPlaneSource.Named(b.Id));
            var angle=new CadDatumPlane(Guid.NewGuid(),part,"Tilt",DatumPlaneKind.Angle,DatumPlaneSource.Named(middle.Id),Value:degrees*Math.PI/180,Hinge:DatumHinge.V);
            await session.ExecuteAsync(new SetDatumsCommand(Book(a,b,middle,angle)));var result=session.Snapshot.Datums.Planes[angle.Id];
            Assert.Equal(new Vector3d(0,0,20),result.Frame.Translation);Assert.True(DatumPlanes.SameFrame(result.Frame,new(new(0,0,20),Quaterniond.FromAxisAngle(new(0,1,0),degrees*Math.PI/180))));
            var flip=b with{Kind=DatumPlaneKind.Angle,Primary=DatumPlaneSource.Named(b.Id),Value=Math.PI};
            flip=flip with{Id=Guid.NewGuid(),Name="Flip"};var anti=middle with{Secondary=DatumPlaneSource.Named(flip.Id)};
            await session.ExecuteAsync(new SetDatumsCommand(session.Snapshot.Datums with{Planes=session.Snapshot.Datums.Planes.SetItem(flip.Id,flip).SetItem(anti.Id,anti)}));
            Assert.Equal(new Vector3d(0,0,20),session.Snapshot.Datums.Planes[middle.Id].Frame.Translation);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task SemanticBoxDimensionAndParameterUpdatePlaneSketchNativeFeatureDrawingAndHistoryTogether()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,10,5,RigidTransform3d.Identity),"Base",part));var box=session.Snapshot.Features.Values.Single();
            var plane=new CadDatumPlane(Guid.NewGuid(),part,"Top + clearance",DatumPlaneKind.Offset,DatumPlaneSource.Box(box.Id,BoxBoundary.ZMax),Value:2);
            await session.ExecuteAsync(new SetDatumsCommand(Book(plane)));plane=session.Snapshot.Datums.Planes[plane.Id];
            var draft=new SketchDraft(CadSketch.Create(part,"Bracket",plane.Frame));draft.AddRectangle(new(0,0),new(4,3));
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver,datum:plane.Id));var sketch=session.Snapshot.Sketches[draft.Value.Id];var source=SketchProfileReference.Create(sketch,sketch.Lines.Select(l=>l.Id));
            await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Bracket",part,sketchSource:source));var extrude=session.Snapshot.Features.Values.Single(f=>f.Id!=box.Id);
            var sheet=TechnicalDrawingSheet.A4Landscape("Datum drawing");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(100,100),1));
            var height=new CadParameter(Guid.NewGuid(),"height",ParameterDimension.Length,"12 mm");var offset=new CadParameter(Guid.NewGuid(),"clearance",ParameterDimension.Length,"height / 4");
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(new([height,offset],[Drive(height,ParameterTarget.Feature(box.Id,"Z")),Drive(offset,ParameterTarget.Datum(plane.Id,"Offset"))])));
            var after=session.Snapshot;Assert.Equal(15,after.Sketches[sketch.Id].Plane.Translation.Z,8);Assert.Equal(15,Assert.IsType<ExtrudeRecipe>(after.Features[extrude.Id].Recipe).Placement.Translation.Z,8);
            Assert.Equal(24,after.Features[extrude.Id].Result.VolumeMm3,6);Assert.Equal(after.Features[extrude.Id].Result.Revision,after.DrawingSheets[sheet.Id].Views.Single().Sources.Single(s=>s.Feature==extrude.Id).Revision);
            after.Validate();await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            await session.ExecuteAsync(new SetParametersCommand(after.Parameters with{Parameters=[height with{Expression="20 mm"},offset]}));Assert.Equal(25,session.Snapshot.Sketches[sketch.Id].Plane.Translation.Z,8);
            var path=files.PathFor("datums.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);loaded.Snapshot.Validate();
            Assert.Equal(session.Snapshot.Datums.Planes[plane.Id].Frame,loaded.Snapshot.Datums.Planes[plane.Id].Frame);Assert.Equal(session.Snapshot.Datums.SketchBindings[sketch.Id],loaded.Snapshot.Datums.SketchBindings[sketch.Id]);
            Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["document"].Version,FormatEvolutionTests.Manifest(path).Sections.Single(s=>s.Kind=="document").SchemaVersion);Assert.Contains("cadoryx.associated-datums.1",FormatEvolutionTests.Manifest(path).RequiredCapabilities);
            before=session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetDatumsCommand(before.Datums with{Planes=before.Datums.Planes.SetItem(plane.Id,before.Datums.Planes[plane.Id] with{Value=9})})));Assert.Same(before,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task AuthoredBoxFacesFollowPlacementAndReferenceChangesWithoutNativeFaceIndexGuessing()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var placement=new RigidTransform3d(new(100,20,0),Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2));
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,10,5,placement),"Box",part));var feature=session.Snapshot.Features.Values.Single();
        var planes=Enum.GetValues<BoxBoundary>().Select(b=>new CadDatumPlane(Guid.NewGuid(),part,b.ToString(),DatumPlaneKind.Offset,DatumPlaneSource.Box(feature.Id,b))).ToArray();
        await session.ExecuteAsync(new SetDatumsCommand(Book(planes)));foreach(var p in planes)Assert.Single(session.Snapshot.Datums.Planes[p.Id].SourceRevisions);
        var xmax=planes.Single(p=>p.Primary.Boundary==BoxBoundary.XMax);Assert.True((session.Snapshot.Datums.Planes[xmax.Id].Frame.Translation-placement.Apply(new(20,0,0))).Length<1e-9);
        await session.ExecuteAsync(new RecomputeCommand(feature.Id,new BoxRecipe(40,10,5,placement)));Assert.True((session.Snapshot.Datums.Planes[xmax.Id].Frame.Translation-placement.Apply(new(40,0,0))).Length<1e-9);session.Snapshot.Validate();
    }
    [Fact] public async Task SharedPartWorkplaneUsesExplicitOccurrenceAndDetachRetainsLastWorldFrame()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var plane=Offset(part,"Work",7);await session.ExecuteAsync(new SetDatumsCommand(Book(plane)));plane=session.Snapshot.Datums.Planes[plane.Id];
        var slot=ComponentSlotId.New();var root=(AssemblyDefinition)session.Snapshot.Definitions[session.Snapshot.RootAssemblyId];var placement=new RigidTransform3d(new(100,40,0),Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2));
        await session.ExecuteAsync(new SnapshotCommand(s=>s with{Definitions=s.Definitions.SetItem(root.Id,root with{Children=root.Children.Add(new(slot,part,"Second instance",placement))})}));
        var occurrence=session.Snapshot.EnumerateOccurrences().Single(o=>o.Path.Slots.Last()==slot);var book=session.Snapshot.Datums with{WorkPlane=new(plane.Id,occurrence.Path,plane.Revision,occurrence.WorldTransform)};
        await session.ExecuteAsync(new SetDatumsCommand(book));var work=session.Snapshot.Settings.WorkPlane;Assert.True(DatumPlanes.SameFrame(new(work.Origin,work.Rotation),placement*plane.Frame));
        var before=session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(DocumentEdits.SetSettings(before.Settings with{WorkPlane=new()})));Assert.Same(before,session.Snapshot);
        var nextPlacement=placement with{Translation=new(200,40,0)};await session.ExecuteAsync(new SnapshotCommand(s=>s with{Definitions=s.Definitions.SetItem(root.Id,((AssemblyDefinition)s.Definitions[root.Id]) with{Children=((AssemblyDefinition)s.Definitions[root.Id]).Children.Select(c=>c.Id==slot?c with{LocalTransform=nextPlacement}:c).ToImmutableArray()})}));
        Assert.True(DatumPlanes.SameFrame(new(session.Snapshot.Settings.WorkPlane.Origin,session.Snapshot.Settings.WorkPlane.Rotation),nextPlacement*plane.Frame));
        work=session.Snapshot.Settings.WorkPlane;await session.ExecuteAsync(new SetDatumsCommand(session.Snapshot.Datums with{WorkPlane=null}));Assert.Equal(work,session.Snapshot.Settings.WorkPlane);
        await session.ExecuteAsync(DocumentEdits.SetSettings(session.Snapshot.Settings with{WorkPlane=new()}));
    }
    [Fact] public async Task InvalidSourcesCrossOwnerCyclesNonparallelAndForgedCachesAreRejectedAtomically()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var a=Offset(part,"A",4);var b=Offset(part,"B",8);await session.ExecuteAsync(new SetDatumsCommand(Book(a,b)));var before=session.Snapshot;
        var badBooks=new[]{Book(a with{Primary=DatumPlaneSource.Named(b.Id)},b with{Primary=DatumPlaneSource.Named(a.Id)}),Book(a with{Primary=DatumPlaneSource.Named(Guid.NewGuid())}),
            Book(a,b with{Name="a"}),Book(a with{Value=double.NaN}),Book(a,b with{Primary=DatumPlaneSource.Base(DocumentWorkPlaneKind.XZ)},new(Guid.NewGuid(),part,"M",DatumPlaneKind.Midplane,DatumPlaneSource.Named(a.Id),DatumPlaneSource.Named(b.Id))),
            Book(a with{Primary=DatumPlaneSource.Box(FeatureId.New(),BoxBoundary.ZMax)})};
        foreach(var book in badBooks){await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetDatumsCommand(book)));Assert.Same(before,session.Snapshot);Assert.Equal(0,assets.Count);}
        Assert.Throws<CadValidationException>(()=>(before with{Datums=before.Datums with{Planes=before.Datums.Planes.SetItem(a.Id,before.Datums.Planes[a.Id] with{Frame=RigidTransform3d.Translate(0,0,99)})}}).Validate());
        var other=DefinitionId.New();await session.ExecuteAsync(ResourceCommands.AddPart(other,"Other"));before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetDatumsCommand(Book(a,b with{PartId=other,Primary=DatumPlaneSource.Named(a.Id)}))));Assert.Same(before,session.Snapshot);
    }
    [Fact] public void MixedFeatureDatumSketchCycleIsDetectedBeforeNativeEvaluation()
    {
        var(initial,part)=SketchCommandTests.Document();var sketch=CadSketch.Create(part,"S",RigidTransform3d.Identity);var feature=FeatureId.New();var plane=Offset(part,"Cycle") with{Primary=DatumPlaneSource.Box(feature,BoxBoundary.ZMax)};
        var fake=initial with{Sketches=initial.Sketches.Add(sketch.Id,sketch),Features=initial.Features.Add(feature,new(feature,part,"F",new BoxRecipe(1,1,1,RigidTransform3d.Identity),[],BodyId.New(),new(new AssetId(new string('a',64)),GeometryRevisionId.New(),BodyKind.Solid,new(Vector3d.Zero,new(1,1,1)),1))
            {SketchSource=SketchProfileReference.Create(sketch,[])}),Datums=Book(plane) with{SketchBindings=ImmutableDictionary<SketchId,SketchDatumBinding>.Empty.Add(sketch.Id,new(sketch.Id,plane.Id,plane.Revision))}};
        Assert.Contains("cycle",Assert.Throws<CadValidationException>(()=>DatumPlanes.Order(fake)).Message);
    }
    [Fact] public async Task BoundSketchCoordinateEditRequiresDetach()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"S",RigidTransform3d.Identity));draft.AddRectangle(new(0,0),new(4,3));await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));var s=session.Snapshot.Sketches[draft.Value.Id];
        var p=Offset(part,"P",5);var book=Book(p) with{SketchBindings=ImmutableDictionary<SketchId,SketchDatumBinding>.Empty.Add(s.Id,new(s.Id,p.Id,p.Revision))};await session.ExecuteAsync(new SetDatumsCommand(book));s=session.Snapshot.Sketches[s.Id];
        var before=session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(s with{Plane=RigidTransform3d.Identity},solver)));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new SetDatumsCommand(session.Snapshot.Datums with{SketchBindings=ImmutableDictionary<SketchId,SketchDatumBinding>.Empty}));Assert.Equal(5,session.Snapshot.Sketches[s.Id].Plane.Translation.Z);
        await session.ExecuteAsync(new UpsertSketchCommand(session.Snapshot.Sketches[s.Id] with{Plane=RigidTransform3d.Identity},solver));Assert.Equal(0,session.Snapshot.Sketches[s.Id].Plane.Translation.Z);
    }
    [Fact] public async Task VersionTwentyOneMigrationAddsEmptyDatumsAndMasqueradeIsRefused()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var path=files.PathFor("old.cadoryx");await storage.SaveAsync(DocumentSnapshot.Create("Old"),assets,path);
        // Emulate the actual legacy four-field point payload before declaring its document version.
        SketchLegacyPointFixture.Prepare(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="document"?s with{SchemaVersion=21}:s).ToImmutableArray()});await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Where(s=>s.Kind is not ("datums" or "assembly-patterns")).ToImmutableArray(),RequiredCapabilities=m.RequiredCapabilities.Where(c=>c is not ("cadoryx.associated-datums.1" or "cadoryx.assembly-patterns.1")).ToImmutableArray()});using(var zip=ZipFile.Open(path,ZipArchiveMode.Update)){zip.GetEntry("sections/datums.msgpack")!.Delete();zip.GetEntry("sections/assembly-patterns.msgpack")!.Delete();}
        using var loaded=await storage.LoadAsync(path,assets);Assert.Empty(loaded.Snapshot.Datums.Planes);Assert.Null(loaded.Snapshot.Datums.WorkPlane);
    }
    [Fact] public async Task DatumEditorInputGuardPreviewCancelConfirmAndStaleState()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var document=new CadDocumentViewModel(session,kernel,new Cadoryx.ViewModels.Services.Platform.Notifications.CadMessageLog());
        try
        {
            var before=session.Snapshot;await using(var editor=new DatumEditorViewModel(document,kernel))
            {
                editor.PlaneName="Plane";editor.Value=7;editor.ApplyPlaneCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);
                editor.Value=8;Assert.False(editor.CanConfirm);await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);editor.ApplyPlaneCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm);editor.CancelCommand.Execute(null);Assert.Same(before,session.Snapshot);
            }
            await using(var editor=new DatumEditorViewModel(document,kernel)){editor.PlaneName="Plane";editor.Value=7;editor.ApplyPlaneCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);await editor.ConfirmCommand.ExecuteAsync(null);Assert.Equal(7,session.Snapshot.Datums.Planes.Values.Single().Frame.Translation.Z);}
            await using var stale=new DatumEditorViewModel(document,kernel);await stale.PreviewCommand.ExecuteAsync(null);Assert.True(stale.CanConfirm);await session.ExecuteAsync(new SnapshotCommand(s=>s with{Name="Changed"}));Assert.True(stale.IsStale);Assert.False(stale.CanConfirm);
        }
        finally{document.Detach();}
    }
    [Fact] public void BareReservedFunctionProducesValidationDiagnostic()=>Assert.Throws<CadValidationException>(()=>ParameterGraph.Evaluate(DocumentSnapshot.Create("Parser") with{Parameters=new([new(Guid.NewGuid(),"bad",ParameterDimension.Scalar,"sin")],[])}));
    [Theory][InlineData(false)][InlineData(true)] public void DatumDepthBudgetDoesNotDependOnDictionaryEnumeration(bool reverse)
    {
        var(doc,part)=SketchCommandTests.Document();var chain=new List<CadDatumPlane>();
        for(int i=0;i<129;i++)chain.Add(Offset(part,"Plane"+i) with{Primary=i==0?DatumPlaneSource.Base(DocumentWorkPlaneKind.XY):DatumPlaneSource.Named(chain[^1].Id)});
        IEnumerable<CadDatumPlane> Order(IEnumerable<CadDatumPlane> values)=>reverse?values.Reverse():values;
        Assert.Equal(128,DatumPlanes.Order(doc with{Datums=Book(Order(chain.Take(128)).ToArray())}).Length);
        Assert.Throws<CadValidationException>(()=>DatumPlanes.Order(doc with{Datums=Book(Order(chain).ToArray())}));
    }
    [Fact] public async Task AuthoredAngleDriverFullTurnChangesRevisionEvenWhenFrameMatches()
    {
        var(doc,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var plane=Offset(part,"Rotation") with{Kind=DatumPlaneKind.Angle};await session.ExecuteAsync(new SetDatumsCommand(Book(plane)));var old=session.Snapshot.Datums.Planes[plane.Id];
        var angle=new CadParameter(Guid.NewGuid(),"angle",ParameterDimension.Angle,"360 deg");await session.ExecuteAsync(new SetParametersCommand(new([angle],[Drive(angle,ParameterTarget.Datum(plane.Id,"Angle"))])));
        var changed=session.Snapshot.Datums.Planes[plane.Id];Assert.True(DatumPlanes.SameFrame(old.Frame,changed.Frame));Assert.NotEqual(old.Revision,changed.Revision);Assert.Equal(2*Math.PI,changed.Value,10);
    }
    [Fact] public async Task CircleProjectionRejectsTiltAtomicallyAndKernelFailureReleasesDatumCandidate()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new SketchAssociationTests.InspectingKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var source=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));source.AddCircle(new(0,0),3);
        await session.ExecuteAsync(new UpsertSketchCommand(source.Value,solver));var authored=session.Snapshot.Sketches[source.Value.Id];var target=new SketchDraft(CadSketch.Create(part,"Projection",RigidTransform3d.Identity));
        target.Project(authored,authored.Circles.Single().Id);target.Change(s=>s with{Circles=s.Circles.Select(c=>c with{IsConstruction=false}).ToImmutableArray()});
        await session.ExecuteAsync(new UpsertSketchCommand(target.Value,solver));var p=Offset(part,"Target plane");
        await session.ExecuteAsync(new SetDatumsCommand(Book(p) with{SketchBindings=ImmutableDictionary<SketchId,SketchDatumBinding>.Empty.Add(target.Value.Id,new(target.Value.Id,p.Id,p.Revision))}));var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetDatumsCommand(before.Datums with{Planes=before.Datums.Planes.SetItem(p.Id,before.Datums.Planes[p.Id] with{Kind=DatumPlaneKind.Angle,Value=Math.PI/4})})));Assert.Same(before,session.Snapshot);
        var profile=SketchProfileReference.CreateCircle(session.Snapshot.Sketches[target.Value.Id],target.Value.Circles.Single().Id);
        await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),4,RigidTransform3d.Identity)),"A",part,sketchSource:profile));var a=session.Snapshot.Bodies.Keys.Single();
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Identity),"B",part));var b=session.Snapshot.Bodies.Keys.Single(id=>id!=a);await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[a,b]));
        before=session.Snapshot;int count=assets.Count;kernel.FailBoolean=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>session.ExecuteAsync(new SetDatumsCommand(before.Datums with{Planes=before.Datums.Planes.SetItem(p.Id,before.Datums.Planes[p.Id] with{Value=10})})));Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
    }
    [Fact] public async Task AngleDriverUpdatesNativeExtrusionOrientationAndReferenceValue()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var plane=Offset(part,"Tilt") with{Kind=DatumPlaneKind.Angle};await session.ExecuteAsync(new SetDatumsCommand(Book(plane)));plane=session.Snapshot.Datums.Planes[plane.Id];
        var draft=new SketchDraft(CadSketch.Create(part,"Plate",plane.Frame));draft.AddRectangle(new(0,0),new(4,3));await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver,datum:plane.Id));var s=session.Snapshot.Sketches[draft.Value.Id];var profile=SketchProfileReference.Create(s,s.Lines.Select(l=>l.Id));
        await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,s.Plane)),"Tilted",part,sketchSource:profile));
        var angle=new CadParameter(Guid.NewGuid(),"tilt",ParameterDimension.Angle,"90 deg");var reference=new CadParameter(Guid.NewGuid(),"actual_tilt",ParameterDimension.Angle,"",ParameterTarget.Datum(plane.Id,"Angle"));
        await session.ExecuteAsync(new SetParametersCommand(new([angle,reference],[Drive(angle,ParameterTarget.Datum(plane.Id,"Angle"))])));
        Assert.Equal(Math.PI/2,ParameterGraph.Evaluate(session.Snapshot)[reference.Id].Value,10);Assert.True(DatumPlanes.SameFrame(session.Snapshot.Sketches[s.Id].Plane,new(Vector3d.Zero,Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2))));
        Assert.Equal(24,session.Snapshot.Features.Values.Single().Result.VolumeMm3,6);session.Snapshot.Validate();
    }
    private sealed class SnapshotCommand(Func<DocumentSnapshot,DocumentSnapshot> change):ICadDocumentCommand
    {public string Name=>"Fixture";public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)=>Task.FromResult(new PreparedDocumentEdit(change(context.Snapshot).WithNewState()));}
}
