using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchCurveParameterTests
{
    internal static ParameterBinding Drive(CadParameter p,ParameterTarget t)=>new(Guid.NewGuid(),p.Id,t);
    internal static CadParameter Number(string name,double value,ParameterDimension dimension=ParameterDimension.Length)=>
        new(Guid.NewGuid(),name,dimension,value.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+(dimension switch
            {ParameterDimension.Length=>" mm",ParameterDimension.Angle=>" rad",_=>""}));

    [Fact] public void TargetsAreExplicitUniqueAndCarryAuthoredUnits()
    {
        var(initial,part)=SketchCommandTests.Document();var d=new SketchDraft(CadSketch.Create(part,"Definitions",RigidTransform3d.Identity));
        d.AddEllipse(new(new(20,0),new(5,0),new(0,2),0,Math.PI));d.AddFreeform(SketchExactEndpointTests.Geometry(1));d.AddFreeform(SketchExactEndpointTests.Geometry(2));
        var doc=initial with{Sketches=initial.Sketches.Add(d.Value.Id,d.Value)};
        var targets=ParameterTargets.All(doc).Where(t=>t.Kind==ParameterTargetKind.ExactSketchCurve).ToArray();
        Assert.Equal(targets.Length,targets.Distinct().Count());Assert.Equal(39,targets.Length);
        foreach(var t in targets)
        {
            var expected=t.Property is "StartAngle" or "SweepAngle"?ParameterDimension.Angle:
                t.Property is "Weight" or "Knot" or "FirstParameter" or "LastParameter"?ParameterDimension.Scalar:ParameterDimension.Length;
            Assert.True(ParameterTargets.Read(doc,t).HasDimension(expected));Assert.Null(t.Constraint);Assert.NotNull(t.Curve);
        }
    }
    [Theory][InlineData("missing")][InlineData("constraint")][InlineData("negative")][InlineData("outside")]
    [InlineData("absent-index")][InlineData("extra-index")][InlineData("structural")][InlineData("foreign-kind")]
    public void ForgedCurveIdentitiesAndTableFieldsAreRejected(string kind)
    {
        var(initial,part)=SketchCommandTests.Document();var(d,id,_)=SketchExactEndpointTests.Loop(part,2,false);
        var doc=initial with{Sketches=initial.Sketches.Add(d.Value.Id,d.Value)};var t=ParameterTarget.SketchCurve(d.Value.Id,id,"PoleX",1);
        t=kind switch
        {
            "missing"=>t with{Curve=Guid.NewGuid()},"constraint"=>t with{Constraint=Guid.NewGuid()},"negative"=>t with{Index=-1},
            "outside"=>t with{Index=1024},"absent-index"=>t with{Index=null},"extra-index"=>t with{Property="FirstParameter"},
            "structural"=>t with{Property="Degree",Index=null},_=>t with{Kind=ParameterTargetKind.Feature}
        };
        Assert.Throws<CadValidationException>(()=>ParameterTargets.Read(doc,t));
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task NamedDefinitionsUpdateExactEndpointsSolidDrawingUndoAndStorageTogether(int kind)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var(d,id,ids)=SketchExactEndpointTests.Loop(part,kind,false);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));
            var s=session.Snapshot.Sketches[d.Value.Id];var ends=SketchFixedCurveEndpoints.All(s).Single().Ends;var source=SketchProfileReference.CreateMixed(s,ids);
            await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane)),"Driven curve solid",part,sketchSource:source));
            var feature=session.Snapshot.Features.Values.Single();var sheet=TechnicalDrawingSheet.A4Landscape("Curve parameters");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1));
            var pairs=kind==0?new[]{(Number("height",3),ParameterTarget.SketchCurve(s.Id,id,"SineY"))}:
                s.Freeforms.Single().Geometry.Poles.Select((p,i)=>(Number("height"+i,p.Y*1.5),ParameterTarget.SketchCurve(s.Id,id,"PoleY",i))).ToArray();
            var book=new ParameterBook(pairs.Select(p=>p.Item1).ToImmutableArray(),pairs.Select(p=>Drive(p.Item1,p.Item2)).ToImmutableArray());
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(book));var after=session.Snapshot;var updated=after.Sketches[s.Id];
            Assert.Equal(ends,SketchFixedCurveEndpoints.All(updated).Single().Ends);Assert.NotEqual(s.Revision,updated.Revision);
            Assert.InRange(Math.Abs(after.Features[feature.Id].Result.VolumeMm3/feature.Result.VolumeMm3-1.5),0,1e-6);
            Assert.Equal(after.Features[feature.Id].Result.Revision,after.DrawingSheets[sheet.Id].Views.Single().Sources.Single().Revision);
            after.Validate();await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            string path=files.PathFor("curve-drivers.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                Assert.True(book.Bindings.ToHashSet().SetEquals(loaded.Snapshot.Parameters.Bindings));
                Assert.Equal(ends,SketchFixedCurveEndpoints.All(loaded.Snapshot.Sketches[s.Id]).Single().Ends);loaded.Snapshot.Validate();
            }
            var edit=new SketchDraft(updated);
            if(kind==0)edit.EditEllipse(id,updated.Ellipses.Single().Geometry with{Sine=new(0,4)});
            else edit.EditFreeform(id,updated.Freeforms.Single().Geometry with{Poles=updated.Freeforms.Single().Geometry.Poles.Select(p=>p with{Y=p.Y*1.1}).ToImmutableArray()});
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(edit.Value,new ManagedSketchConstraintSolver())));Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task AllIntervalAndKnotFieldsAreStagedBeforeValidatingTheWholeCurve()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var d=new SketchDraft(CadSketch.Create(part,"Translated parameter domain",RigidTransform3d.Identity));d.AddFreeform(SketchExactEndpointTests.Geometry(2));
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var s=session.Snapshot.Sketches[d.Value.Id];var curve=s.Freeforms.Single();var g=curve.Geometry;
            var fields=new[]{ParameterTarget.SketchCurve(s.Id,curve.Id,"FirstParameter"),ParameterTarget.SketchCurve(s.Id,curve.Id,"LastParameter")}
                .Concat(Enumerable.Range(0,g.Knots.Length).Select(i=>ParameterTarget.SketchCurve(s.Id,curve.Id,"Knot",i))).ToArray();
            var parameters=fields.Select((t,i)=>Number("domain"+i,ParameterTargets.Read(session.Snapshot,t).Value+10,ParameterDimension.Scalar)).ToImmutableArray();
            await session.ExecuteAsync(new SetParametersCommand(new(parameters,fields.Select((t,i)=>Drive(parameters[i],t)).ToImmutableArray())));
            var updated=session.Snapshot.Sketches[s.Id].Freeforms.Single().Geometry;
            foreach(double t in new[]{0d,.17,.5,.92,1})SketchFreeformGeometryTests.Near(g.At(t),updated.At(t));
            var before=session.Snapshot;int count=assets.Count;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(session.Snapshot.Parameters with
                {Parameters=parameters.SetItem(0,parameters[0] with{Expression="999"})})));Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ProjectionDefinitionsMustBeDetachedBeforeTheyCanBecomeParameterTargets()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var d=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));d.AddFreeform(SketchExactEndpointTests.Geometry(1));
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var source=session.Snapshot.Sketches[d.Value.Id];
            var target=new SketchDraft(CadSketch.Create(part,"Projected",RigidTransform3d.Translate(0,0,10)));target.Project(source,source.Freeforms.Single().Id);
            await session.ExecuteAsync(new UpsertSketchCommand(target.Value,new ManagedSketchConstraintSolver()));var s=session.Snapshot.Sketches[target.Value.Id];
            var t=ParameterTarget.SketchCurve(s.Id,s.Freeforms.Single().Id,"PoleX",0);Assert.DoesNotContain(t,ParameterTargets.All(session.Snapshot));
            Assert.Throws<CadValidationException>(()=>ParameterTargets.Read(session.Snapshot,t));
            var p=Number("projected",12);var before=session.Snapshot;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(new([p],[Drive(p,t)]))));Assert.Same(before,session.Snapshot);
            target=new(s);target.DetachProjection(s.Freeforms.Single().Id);await session.ExecuteAsync(new UpsertSketchCommand(target.Value,new ManagedSketchConstraintSolver()));
            await session.ExecuteAsync(new SetParametersCommand(new([p],[Drive(p,t)])));Assert.Equal(12,ParameterTargets.Read(session.Snapshot,t).Value);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExactTargetsCannotMasqueradeAsLegacyParameterSections(bool reference)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var(d,id,_)=SketchExactEndpointTests.Loop(part,0,false);
        var t=ParameterTarget.SketchCurve(d.Value.Id,id,"CosineX");var p=reference?new CadParameter(Guid.NewGuid(),"read_axis",ParameterDimension.Length,"",t):Number("axis",5);
        var doc=initial with{Sketches=initial.Sketches.Add(d.Value.Id,d.Value),Parameters=new([p],reference?[]:[Drive(p,t)])};
        var storage=new CadDocumentStorage();string path=files.PathFor("parameters.cadoryx");await storage.SaveAsync(doc,assets,path);
        using(var loaded=await storage.LoadAsync(path,assets))loaded.Snapshot.Validate();
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="parameters"?s with{SchemaVersion=1}:s).ToImmutableArray()});
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Contains("legacy parameter schema",error.ToString());Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ExistingParameterSectionsMigrateWithoutInventingCurveTargets()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();string path=files.PathFor("old-table.cadoryx");
        var doc=DocumentSnapshot.Create("Old parameters") with{Parameters=new([Number("length",5)],[])};await storage.SaveAsync(doc,assets,path);
        // The legacy document predates this required section; remove its newly written empty table.
        SketchExpressionSectionTests.Rewrite(path,"sketch-expressions",_=>null);
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="parameters"?s with{SchemaVersion=1}:s.Kind=="document"?s with{SchemaVersion=37}:s).ToImmutableArray(),
            RequiredCapabilities=m.RequiredCapabilities.Remove("cadoryx.exact-sketch-expressions.1")});
        using var loaded=await storage.LoadAsync(path,assets);Assert.Equal(5,ParameterGraph.Evaluate(loaded.Snapshot).Values.Single().Value);loaded.Snapshot.Validate();
    }
    [Fact] public async Task ParameterEditorOffersCurveTargetsAndKeepsPreviewCancelable()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var(d,id,_)=SketchExactEndpointTests.Loop(part,0,false);
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var editor=new ParameterEditorViewModel(doc,kernel);var target=ParameterTarget.SketchCurve(d.Value.Id,id,"SineY");
            var choice=Assert.Single(editor.Targets,t=>t.Target==target);Assert.Contains(Cadoryx.Lang.Strings.Strings.ExactEllipse,choice.Label);
            editor.ParameterName="height";editor.Expression="3 mm";editor.ApplyParameterCommand.Execute(null);editor.DrivingTarget=choice;editor.BindDimensionCommand.Execute(null);
            var before=session.Snapshot;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);
            editor.CancelCommand.Execute(null);Assert.False(editor.CanConfirm);Assert.Same(before,session.Snapshot);
        }
        finally{doc.Detach();}
    }
    [Fact] public async Task SeveralEllipseAxisFieldsCanCrossAnInvalidIntermediateDefinitionAtomically()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var(d,id,_)=SketchExactEndpointTests.Loop(part,0,false);
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var s=session.Snapshot.Sketches[d.Value.Id];
        var fields=new[]{("CosineX",0d),("SineY",0d),("CosineY",5d),("SineX",2d)};
        var ps=fields.Select((v,i)=>Number("axis"+i,v.Item2)).ToImmutableArray();
        await session.ExecuteAsync(new SetParametersCommand(new(ps,fields.Select((v,i)=>Drive(ps[i],ParameterTarget.SketchCurve(s.Id,id,v.Item1))).ToImmutableArray())));
        var g=session.Snapshot.Sketches[s.Id].Ellipses.Single().Geometry;Assert.Equal(new(0,5),g.Cosine);Assert.Equal(new(2,0),g.Sine);session.Snapshot.Validate();
    }
    [Fact] public async Task AngularIntervalsAndRationalWeightsUseTheirOwnDimensionsAndRejectInvalidValues()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=new SketchDraft(CadSketch.Create(part,"Units",RigidTransform3d.Identity));
        d.AddEllipse(new(new(20,0),new(5,0),new(0,2),0,Math.PI));d.AddFreeform(SketchExactEndpointTests.Geometry(1));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var s=session.Snapshot.Sketches[d.Value.Id];
        var angle=new CadParameter(Guid.NewGuid(),"sweep",ParameterDimension.Angle,"90 deg");var weight=Number("weight",2,ParameterDimension.Scalar);
        var t=ParameterTarget.SketchCurve(s.Id,s.Freeforms.Single().Id,"Weight",1);
        var book=new ParameterBook([angle,weight],[Drive(angle,ParameterTarget.SketchCurve(s.Id,s.Ellipses.Single().Id,"SweepAngle")),Drive(weight,t)]);
        await session.ExecuteAsync(new SetParametersCommand(book));Assert.Equal(Math.PI/2,session.Snapshot.Sketches[s.Id].Ellipses.Single().Geometry.SweepAngle,12);
        Assert.Equal(2,ParameterTargets.Read(session.Snapshot,t).Value);var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[angle,weight with{Expression="0"}]})));Assert.Same(before,session.Snapshot);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[angle,weight with{Dimension=ParameterDimension.Length,Expression="2 mm"}]})));Assert.Same(before,session.Snapshot);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RetainedTargetsRejectStructuralCurveChangesEvenWhenTheIndexedValueMatches(bool reference)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=new SketchDraft(CadSketch.Create(part,"Indexed table",RigidTransform3d.Identity));d.AddFreeform(SketchExactEndpointTests.Geometry(1));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var s=session.Snapshot.Sketches[d.Value.Id];var id=s.Freeforms.Single().Id;
        var t=ParameterTarget.SketchCurve(s.Id,id,"PoleX",0);var p=reference?new CadParameter(Guid.NewGuid(),"first_x",ParameterDimension.Length,"",t):Number("first_x",10);
        await session.ExecuteAsync(new SetParametersCommand(new([p],reference?[]:[Drive(p,t)])));var before=session.Snapshot;
        var edit=new SketchDraft(before.Sketches[s.Id]);edit.EditFreeform(id,s.Freeforms.Single().Geometry.Reverse());
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(edit.Value,new ManagedSketchConstraintSolver())));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new SetParametersCommand(ParameterBook.Empty));await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,new ManagedSketchConstraintSolver()));Assert.True(session.Snapshot.Sketches[s.Id].Freeforms.Single().Geometry.Reversed);
    }
    [Fact] public async Task SolverCannotAlterTheExactDefinitionDuringParameterRecompute()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=new SketchDraft(CadSketch.Create(part,"Intent",RigidTransform3d.Identity));d.AddFreeform(SketchExactEndpointTests.Geometry(1));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var s=session.Snapshot.Sketches[d.Value.Id];var p=Number("y",7);var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(new([p],[Drive(p,ParameterTarget.SketchCurve(s.Id,s.Freeforms.Single().Id,"PoleY",1))]),new ForgedCurveSolver())));Assert.Same(before,session.Snapshot);
    }
    private sealed class ForgedCurveSolver:ISketchConstraintSolver
    {
        public string Version=>"test";
        public async Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {
            var report=await new ManagedSketchConstraintSolver().SolveAsync(sketch,options,cancellationToken);var s=report.Solution!;var curve=s.Freeforms.Single();
            return report with{Solution=s with{Freeforms=s.Freeforms.Replace(curve,curve with{Geometry=curve.Geometry with{Weights=curve.Geometry.Weights.SetItem(1,2)}})}};
        }
    }
    [Fact] public async Task NativeProjectionTargetsRemainProtectedUntilExplicitDetach()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            using(var shape=SketchFreeformProjectionTests.NativeSource(0))using(var stored=OcctGeometryBridge.StoreShape(shape,assets))
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Native source",part));
            var evidence=await kernel.ProjectEdgeAsync(session.Snapshot,part,SketchEllipseProjectionTests.Pick(session.Snapshot,assets,OcctSharp.CurveGeometryType.BezierCurve),RigidTransform3d.Identity,assets);
            var d=new SketchDraft(CadSketch.Create(part,"Native driven",RigidTransform3d.Identity));var id=d.ProjectNative(evidence);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));
            var target=ParameterTarget.SketchCurve(d.Value.Id,id,"Weight",1);Assert.DoesNotContain(target,ParameterTargets.All(session.Snapshot));
            Assert.Throws<CadValidationException>(()=>ParameterTargets.Read(session.Snapshot,target));var p=Number("weight",2,ParameterDimension.Scalar);var before=session.Snapshot;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(new([p],[Drive(p,target)]))));Assert.Same(before,session.Snapshot);
            d=new(before.Sketches[d.Value.Id]);d.DetachProjection(id);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));
            await session.ExecuteAsync(new SetParametersCommand(new([p],[Drive(p,target)])));Assert.Equal(2,ParameterTargets.Read(session.Snapshot,target).Value);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task DownstreamNativeFailureAndCanceledCurveEditsLeaveTheWholeDocumentUnchanged()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new SketchAssociationTests.InspectingKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var(d,id,ids)=SketchExactEndpointTests.Loop(part,0,false);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var s=session.Snapshot.Sketches[d.Value.Id];
            var source=SketchProfileReference.CreateMixed(s,ids);await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane)),"Curve body",part,sketchSource:source));var body=session.Snapshot.Bodies.Keys.Single();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(19,0,0)),"Tool",part));var tool=session.Snapshot.Bodies.Keys.Single(b=>b!=body);
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[body,tool]));var before=session.Snapshot;int count=assets.Count;var p=Number("height",3);
            var command=new SetParametersCommand(new([p],[Drive(p,ParameterTarget.SketchCurve(s.Id,id,"SineY"))]));using var cancel=new CancellationTokenSource();cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(command,cancel.Token));Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
            kernel.FailBoolean=true;await Assert.ThrowsAsync<InvalidOperationException>(()=>session.ExecuteAsync(command));Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
}

public sealed partial class IndependentAuthoringGraphTests
{
    [Fact] public async Task IndependentExactCurveDriversRemapSketchIdentityAndCanDetachIndividually()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var(d,id,ids)=SketchExactEndpointTests.Loop(part,0,false);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));
            var s=session.Snapshot.Sketches[d.Value.Id];var profile=SketchProfileReference.CreateMixed(s,ids);
            await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane)),"Driven",part,sketchSource:profile));
            var p=SketchCurveParameterTests.Number("height",3);var target=ParameterTarget.SketchCurve(s.Id,id,"SineY");await session.ExecuteAsync(new SetParametersCommand(new([p],[SketchCurveParameterTests.Drive(p,target)])));
            var clone=await Clone(session,part);var copy=session.Snapshot.Sketches.Values.Single(s=>s.PartId==clone.ResultPartId);var copyTarget=target with{Owner=copy.Id.Value};
            Assert.Contains(session.Snapshot.Parameters.Bindings,b=>b.Target==copyTarget);Assert.Equal(3,ParameterTargets.Read(session.Snapshot,copyTarget).Value);
            var book=session.Snapshot.Parameters with{Parameters=[p with{Expression="4 mm"}]};await session.ExecuteAsync(new SetParametersCommand(book));
            Assert.Equal(4,ParameterTargets.Read(session.Snapshot,target).Value);Assert.Equal(4,ParameterTargets.Read(session.Snapshot,copyTarget).Value);
            book=session.Snapshot.Parameters with{Parameters=[p with{Expression="5 mm"}],Bindings=session.Snapshot.Parameters.Bindings.Where(b=>b.Target!=copyTarget).ToImmutableArray()};
            await session.ExecuteAsync(new SetParametersCommand(book));Assert.Equal(5,ParameterTargets.Read(session.Snapshot,target).Value);Assert.Equal(4,ParameterTargets.Read(session.Snapshot,copyTarget).Value);session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
}
