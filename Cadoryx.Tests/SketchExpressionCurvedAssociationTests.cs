using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using OcctSharp;
using Xunit;
using Xunit.Abstractions;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

// Actual C1 author commands and archive payloads. This draft is not evidence
// until compiled and exercised against the frozen SDK22 application.
public sealed class SketchExpressionCurvedAssociationTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> PolynomialCases()=>from spline in new[]{false,true}
        from reverse in new[]{false,true} from tilted in new[]{false,true}
        from distance in new[]{-.03,.03} from drivePath in new[]{false,true}
        select new object[]{spline,reverse,tilted,distance,drivePath};

    [Theory,MemberData(nameof(PolynomialCases))]
    public async Task PolynomialSpineAndCircleSectionEditsRecomputeAtomicCurvedPipeAndBothAuthorEncodings(
        bool spline,bool reverse,bool tilted,double distance,bool drivePath)
    {
        await using var f=new Fixture();var world=World(tilted);
        var path=Polynomial(f.Part,spline,1,world,distance,reverse);
        var curve=Curve(path.Draft.Value,path.Expression);var first=curve.Geometry.Evaluate(0);
        var tangent=new Vector3d(first.First.X,first.First.Y,0).Normalized();
        var rotation=Quaterniond.FromAxisAngle(Vector3d.UnitZ.Cross(tangent),Math.Acos(Vector3d.UnitZ.Dot(tangent)));
        var pose=world*new RigidTransform3d(new(first.Point.X,first.Point.Y,0),rotation);
        var section=Circle(f.Part,.03,pose,.01,false);
        await f.Store(path.Draft.Value);await f.Store(section.Draft.Value);
        var s=f.Session.Snapshot.Sketches[section.Draft.Value.Id];var p=f.Session.Snapshot.Sketches[path.Draft.Value.Id];
        var recipe=Sweep(f,s,section.Expression,p,path.Expression);
        await f.Session.ExecuteAsync(new AddBodyCommand(recipe,"C1 polynomial pipe",f.Part));
        var before=f.Session.Snapshot;var feature=Assert.Single(before.Features.Values);
        var originalPath=Assert.IsType<SweepRecipe>(feature.Recipe);var p0=new Point2d(-.5,0);var p2=new Point2d(.5,0);var q=new Point2d(0,1);double radius=.04;
        if(drivePath)
        {
            // An explicit author edit along the original traversal-start ray
            // preserves that start position and unit tangent. No section frame
            // is silently moved to rescue an incompatible author edit.
            var start=reverse?p2:p0;q=new(start.X+1.2*(q.X-start.X),start.Y+1.2*(q.Y-start.Y));
            var edit=new SketchDraft(p);EditPolynomialControl(edit,spline,q);await f.Store(FixAuthors(edit.Value));
        }
        else
        {
            var c=s.Constraints.OfType<RadiusConstraint>().Single();
            await f.Store(s with{Constraints=s.Constraints.Replace(c,c with{Radius=.04})});radius=.05;
        }
        var after=f.Session.Snapshot;var current=Assert.IsType<SweepRecipe>(after.Features[feature.Id].Recipe);
        double expected=Math.PI*radius*radius*PolynomialOffsetLength(p0,q,p2,distance);
        await Verify(f,after.Features[feature.Id].Result,expected);
        Assert.Equal(originalPath.Section.Placement,current.Section.Placement);
        Assert.Equal(originalPath.Path.Placement,current.Path.Placement);
        Assert.Equal(originalPath.Path.Source!.Edges.ToArray(),current.Path.Source!.Edges.ToArray());
        Assert.Equal(originalPath.Section.Source!.MixedBoundaryIds.ToArray(),current.Section.Source!.MixedBoundaryIds.ToArray());
        AssertCurrentSources(after,current);AssertEndpoints(before,after);
        await UndoRedoAndArchives(f,before,after,feature.Id,expected);
        await UnavailableSourceFreezesLastResult(f,after,p.Id,path.Expression,feature.Id);
        output.WriteLine($"spline={spline};reverse={reverse};tilted={tilted};distance={distance:R};drivePath={drivePath};expected={expected:R}");
    }

    public static IEnumerable<object[]> CircularCases()=>from family in new[]{"circle","bezier","spline"}
        from reverse in new[]{false,true} from tilted in new[]{false,true} from rejectMove in new[]{false,true}
        select new object[]{family,reverse,tilted,rejectMove};

    [Theory,MemberData(nameof(CircularCases))]
    public async Task CircularSpineSectionOffsetDrivesCurvedSweepAndMisalignedPathEditIsAtomic(
        string family,bool reverse,bool tilted,bool rejectMove)
    {
        await using var f=new Fixture();var world=World(tilted);
        var path=Circle(f.Part,12,world,.1,reverse,quarter:true);var e=Curve(path.Draft.Value,path.Expression).Geometry;var first=e.At(0);
        var rotation=reverse?Quaterniond.FromAxisAngle(new(1,1,1),Math.Tau/3):Quaterniond.FromEulerDegrees(-90,0,0);
        var pose=world*new RigidTransform3d(new(first.X,first.Y,0),rotation);
        var section=Section(f.Part,family,1,pose,.03,reverse);
        await f.Store(path.Draft.Value);await f.Store(section.Draft.Value);
        var p=f.Session.Snapshot.Sketches[path.Draft.Value.Id];var s=f.Session.Snapshot.Sketches[section.Draft.Value.Id];
        await f.Session.ExecuteAsync(new AddBodyCommand(Sweep(f,s,section.Expression,p,path.Expression),"C1 circular sweep",f.Part));
        var before=f.Session.Snapshot;var feature=Assert.Single(before.Features.Values);int assets=f.Assets.Count;
        if(rejectMove)
        {
            var c=p.Constraints.OfType<RadiusConstraint>().Single();
            await Assert.ThrowsAsync<CadValidationException>(()=>f.Store(p with{Constraints=p.Constraints.Replace(c,c with{Radius=12.2})}));
            Assert.Same(before,f.Session.Snapshot);Assert.Equal(assets,f.Assets.Count);Assert.Equal(feature.Result,f.Session.Snapshot.Features[feature.Id].Result);
            AssertEndpoints(before,f.Session.Snapshot);return;
        }
        var parameter=new CadParameter(Guid.NewGuid(),"section_offset",ParameterDimension.Length,"0.04 mm");
        await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[new(Guid.NewGuid(),parameter.Id,
            ParameterTarget.SketchCurve(s.Id,section.Expression,"OffsetDistance",0))])));
        var after=f.Session.Snapshot;var current=Assert.IsType<SweepRecipe>(after.Features[feature.Id].Recipe);
        double expected=Area(family,1,.04)*12.1*Math.PI/2;
        await Verify(f,after.Features[feature.Id].Result,expected);AssertCurrentSources(after,current);AssertEndpoints(before,after);
        Assert.Equal(Assert.IsType<SweepRecipe>(feature.Recipe).Path.Source!.Edges.ToArray(),current.Path.Source!.Edges.ToArray());
        await UndoRedoAndArchives(f,before,after,feature.Id,expected);
        await UnavailableSourceFreezesLastResult(f,after,s.Id,section.Expression,feature.Id);
        output.WriteLine($"family={family};reverse={reverse};tilted={tilted};expected={expected:R}");
    }

    public static IEnumerable<object[]> LoftCases()=>from family in new[]{"circle","bezier","spline"}
        from factor in new[]{.8,1.3} from reverse in new[]{false,true} from tilted in new[]{false,true}
        from driveFirst in new[]{false,true} select new object[]{family,factor,reverse,tilted,driveFirst};

    [Theory,MemberData(nameof(LoftCases))]
    public async Task UnequalSectionsWithIndependentlyDrivenOffsetsKeepC1ChannelsAndIndependentRuledVolume(
        string family,double factor,bool reverse,bool tilted,bool driveFirst)
    {
        await using var f=new Fixture();var world=World(tilted);var delta=tilted?new Vector3d(1,.4,5):new(0,0,5);
        var first=Section(f.Part,family,1,world,.03,reverse);var last=Section(f.Part,family,factor,
            world*new RigidTransform3d(delta,Quaterniond.Identity),.03*factor,reverse);
        await f.Store(first.Draft.Value);await f.Store(last.Draft.Value);
        var s0=f.Session.Snapshot.Sketches[first.Draft.Value.Id];var s1=f.Session.Snapshot.Sketches[last.Draft.Value.Id];
        var sources=ImmutableArray.Create<SketchProfileReference?>(SketchProfileReference.CreateMixed(s0,[first.Expression]),SketchProfileReference.CreateMixed(s1,[last.Expression]));
        var channel=Guid.NewGuid();var recipe=new LoftRecipe([s0.Plane,s1.Plane],[new(channel,null,
            [SketchProfileBuilder.Mixed(s0,[first.Expression]),SketchProfileBuilder.Mixed(s1,[last.Expression])]){Sources=sources}],Ruled:true);
        Assert.False(SketchProfileReference.SameProfile(recipe.Channels[0].Profiles[0],recipe.Channels[0].Profiles[1]));
        await f.Session.ExecuteAsync(new AddBodyCommand(recipe,"C1 unequal loft",f.Part));
        var before=f.Session.Snapshot;var feature=Assert.Single(before.Features.Values);var driven=driveFirst?s0:s1;var id=driveFirst?first.Expression:last.Expression;
        double offset=driveFirst?.04:.04*factor;var parameter=new CadParameter(Guid.NewGuid(),"loft_offset",ParameterDimension.Length,offset.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" mm");
        await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[new(Guid.NewGuid(),parameter.Id,ParameterTarget.SketchCurve(driven.Id,id,"OffsetDistance",0))])));
        var after=f.Session.Snapshot;var current=Assert.IsType<LoftRecipe>(after.Features[feature.Id].Recipe);
        double d0=driveFirst?.04:.03,d1=driveFirst?.03*factor:.04*factor;
        double expected=family=="circle"?5*Math.PI*(Math.Pow(.5+d0,2)+(.5+d0)*(.5*factor+d1)+Math.Pow(.5*factor+d1,2))/3:
            5*(PairIntegral(1,factor,1,factor)/3-PolynomialAreaLinearCoefficient*PairIntegral(1,factor,d0,d1)+
                PolynomialAreaQuadraticCoefficient*PairIntegral(d0,d1,d0,d1));
        await Verify(f,after.Features[feature.Id].Result,expected);
        Assert.Equal(channel,current.Channels[0].Id);Assert.Equal(recipe.Stations.ToArray(),current.Stations.ToArray());
        Assert.Equal(sources.Select(s=>s!.SketchId),current.Channels[0].Sources.Select(s=>s!.SketchId));
        foreach(var source in current.Channels[0].Sources)Assert.Equal(after.Sketches[source!.SketchId].Revision,source.Revision);
        AssertEndpoints(before,after);await UndoRedoAndArchives(f,before,after,feature.Id,expected);
        await UnavailableSourceFreezesLastResult(f,after,driven.Id,id,feature.Id);
        output.WriteLine($"family={family};factor={factor:R};reverse={reverse};tilted={tilted};driveFirst={driveFirst};expected={expected:R}");
    }

    private static double PolynomialAreaLinearCoefficient=>3/(2*Math.Sqrt(5))+Math.Asinh(2)/4;
    private static double PolynomialAreaQuadraticCoefficient=>Math.Atan(2)-2d/5;
    private static double Area(string family,double scale,double distance)=>family=="circle"?Math.PI*Math.Pow(.5*scale+distance,2):
        scale*scale/3-scale*distance*PolynomialAreaLinearCoefficient+distance*distance*PolynomialAreaQuadraticCoefficient;
    private static double PairIntegral(double a,double b,double c,double d)=>(2*a*c+a*d+b*c+2*b*d)/6;
    private static double PolynomialOffsetLength(Point2d a,Point2d control,Point2d b,double distance)=>Integral(t=>
    {
        double dx=2*((1-t)*(control.X-a.X)+t*(b.X-control.X)),dy=2*((1-t)*(control.Y-a.Y)+t*(b.Y-control.Y));
        double ddx=2*(b.X-2*control.X+a.X),ddy=2*(b.Y-2*control.Y+a.Y),s=double.Hypot(dx,dy),dot=dx*ddx+dy*ddy;
        double x=dx+distance*(ddy/s-dy*dot/(s*s*s)),y=dy+distance*(-ddx/s+dx*dot/(s*s*s));return double.Hypot(x,y);
    });
    private static double Integral(Func<double,double> f)
    {const int steps=16384;double sum=f(0)+f(1);for(int i=1;i<steps;i++)sum+=(i%2==0?2:4)*f(i/(double)steps);return sum/(3*steps);}
    private static RigidTransform3d World(bool tilted)=>tilted?new(new(11,-7,3),Quaterniond.FromEulerDegrees(23,41,17)):RigidTransform3d.Identity;
    private sealed record Author(SketchDraft Draft,SketchEntityId Primitive,SketchEntityId Expression);
    private static Author Circle(DefinitionId part,double radius,RigidTransform3d pose,double offset,bool reverse,bool quarter=false)
    {
        var draft=new SketchDraft(CadSketch.Create(part,"Circle author",pose));draft.AddCircle(new(0,0),radius,construction:true);var primitive=draft.Value.Circles[0].Id;
        var expression=draft.AddExactOffset(primitive,offset);if(quarter)draft.EditExpression(expression,Curve(draft.Value,expression).Geometry.Trim(0,.25));
        if(reverse)draft.EditExpression(expression,Curve(draft.Value,expression).Geometry.Reverse());draft.SetConstruction([expression],false);
        draft.Change(FixAuthors);return new(draft,primitive,expression);
    }
    private static Author Polynomial(DefinitionId part,bool spline,double scale,RigidTransform3d pose,double offset,bool reverse)
    {
        var draft=new SketchDraft(CadSketch.Create(part,"Polynomial author",pose));
        if(spline)draft.AddSpline([new(-.5*scale,0),new(-scale/6,2*scale/3),new(scale/6,2*scale/3),new(.5*scale,0)],construction:true);
        else draft.AddBezier(new(-.5*scale,0),new(0,scale),new(.5*scale,0),construction:true);
        var primitive=spline?draft.Value.Splines[0].Id:draft.Value.Beziers[0].Id;var expression=draft.AddExactOffset(primitive,offset);
        if(reverse)draft.EditExpression(expression,Curve(draft.Value,expression).Geometry.Reverse());draft.SetConstruction([expression],false);draft.Change(FixAuthors);
        return new(draft,primitive,expression);
    }
    private static Author Section(DefinitionId part,string family,double scale,RigidTransform3d pose,double offset,bool reverse)=>family=="circle"?
        Circle(part,.5*scale,pose,offset,reverse):Polynomial(part,family=="spline",scale,pose,offset,reverse);
    private static SketchExpressionCurve Curve(CadSketch sketch,SketchEntityId id)=>sketch.ExpressionCurves.Single(c=>c.Id==id);
    private static CadSketch FixAuthors(CadSketch sketch)
    {
        var generated=sketch.ExpressionCurves.SelectMany(c=>new[]{c.Endpoints!.Start,c.Endpoints.End}).ToHashSet();
        var old=sketch.Constraints.OfType<FixPointConstraint>().ToDictionary(c=>c.Point);
        return sketch with{Constraints=[..sketch.Constraints.Where(c=>c is not FixPointConstraint),..sketch.Points.Where(p=>!generated.Contains(p.Id))
            .Select(p=>(SketchConstraint)new FixPointConstraint(old.TryGetValue(p.Id,out var previous)?previous.Id:SketchConstraintId.New(),p.Id,p.Position))]};
    }
    private static void EditPolynomialControl(SketchDraft draft,bool spline,Point2d q)
    {
        if(!spline){draft.MovePoint(draft.Value.Beziers[0].Control,q);return;}
        var s=draft.Value.Splines[0];var a=draft.Value.Points.Single(p=>p.Id==s.Controls[0]).Position;var b=draft.Value.Points.Single(p=>p.Id==s.Controls[^1]).Position;
        draft.MovePoint(s.Controls[1],new(a.X+2*(q.X-a.X)/3,a.Y+2*(q.Y-a.Y)/3));
        draft.MovePoint(s.Controls[2],new(b.X+2*(q.X-b.X)/3,b.Y+2*(q.Y-b.Y)/3));
    }
    private static SweepRecipe Sweep(Fixture f,CadSketch section,SketchEntityId sectionCurve,CadSketch path,SketchEntityId pathCurve)=>
        new(new(SketchProfileBuilder.Mixed(section,[sectionCurve]),section.Plane){Source=SketchProfileReference.CreateMixed(section,[sectionCurve])},
            new SketchPathReference(path.Id,path.Revision,[new(pathCurve)]).Resolve(f.Session.Snapshot,f.Part));
    private static void AssertCurrentSources(DocumentSnapshot doc,SweepRecipe recipe)
    {Assert.Equal(doc.Sketches[recipe.Section.Source!.SketchId].Revision,recipe.Section.Source.Revision);Assert.Equal(doc.Sketches[recipe.Path.Source!.Sketch].Revision,recipe.Path.Source.Revision);}
    private static void AssertEndpoints(DocumentSnapshot before,DocumentSnapshot after)
    {
        foreach(var sketch in before.Sketches.Values)foreach(var expression in sketch.ExpressionCurves)
            Assert.Equal(expression.Endpoints,after.Sketches[sketch.Id].ExpressionCurves.Single(c=>c.Id==expression.Id).Endpoints);
    }
    private static async Task Verify(Fixture f,GeometryAssetRef geometry,double expected)
    {
        using var shape=OcctGeometryBridge.ReadShape(geometry,f.Assets);Assert.Equal(BodyKind.Solid,geometry.Kind);
        Assert.Equal(1,shape.GetTopologySummary().UniqueCounts.SolidCount);Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);
        Assert.InRange(Math.Abs(geometry.VolumeMm3-expected),0,1e-7);await Task.CompletedTask;
    }
    private static async Task UndoRedoAndArchives(Fixture f,DocumentSnapshot before,DocumentSnapshot after,FeatureId feature,double expected)
    {
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
        using var files=new TestFiles();var storage=new CadDocumentStorage();
        foreach(bool json in new[]{false,true})
        {
            var path=files.PathFor(json?"authors-json.cadoryx":"authors-messagepack.cadoryx");await f.Session.SaveAsync(storage,path);
            if(json)SketchExpressionSectionTests.Rewrite(path,"sketch-expressions",_=>SketchExpressionSectionStorage.EncodeJson(after.Sketches.Values),"json",3);
            using var loaded=await storage.LoadAsync(path,f.Assets);loaded.Snapshot.Validate();var current=after.Features[feature];var restored=loaded.Snapshot.Features[feature];
            Assert.Equal(current.Id,restored.Id);Assert.Equal(current.OutputBodyId,restored.OutputBodyId);Assert.Equal(current.Result,restored.Result);
            foreach(var sketch in after.Sketches.Values)
            {var actual=loaded.Snapshot.Sketches[sketch.Id];Assert.True(SketchExpressionEntities.SameTable(sketch,actual));Assert.Equal(sketch.Points.ToArray(),actual.Points.ToArray());Assert.Equal(sketch.Constraints.ToArray(),actual.Constraints.ToArray());}
            if(current.Recipe is SweepRecipe sweep)
            {var r=Assert.IsType<SweepRecipe>(restored.Recipe);Assert.True(SketchMixedProfile.SameLoop(sweep.Path.Curves,r.Path.Curves));Assert.True(SketchProfileReference.SameProfile(sweep.Section.Profile,r.Section.Profile));Assert.Equal(sweep.Path.Source!.Edges.ToArray(),r.Path.Source!.Edges.ToArray());AssertCurrentSources(loaded.Snapshot,r);}
            else
            {var loft=Assert.IsType<LoftRecipe>(current.Recipe);var r=Assert.IsType<LoftRecipe>(restored.Recipe);Assert.Equal(loft.Stations.ToArray(),r.Stations.ToArray());Assert.Equal(loft.Channels.Select(c=>c.Id),r.Channels.Select(c=>c.Id));foreach(var(a,b)in loft.Channels.Zip(r.Channels)){Assert.Equal(JsonSerializer.Serialize(a.Sources,CadJson.Options),JsonSerializer.Serialize(b.Sources,CadJson.Options));foreach(var(x,y)in a.Profiles.Zip(b.Profiles))Assert.True(SketchProfileReference.SameProfile(x,y));}}
            await Verify(f,restored.Result,expected);
            var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal(json?"json":"messagepack",manifest.Sections.Single(s=>s.Kind=="sketch-expressions").Encoding);
            Assert.Equal(json?3:4,manifest.Sections.Single(s=>s.Kind=="sketch-expressions").SchemaVersion);Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
        }
        RecoveryKey key;using(var recovery=new CadRecoveryStore(files.PathFor("recovery"),storage)){var id=Guid.NewGuid();key=new(recovery.RunId,id);await recovery.WriteAsync(id,after,f.Assets,null);}
        using var reader=new CadRecoveryStore(files.PathFor("recovery"),storage);using var recovered=await reader.OpenAsync(key,f.Assets);
        Assert.Equal(after.StateId,recovered.Document.Snapshot.StateId);AssertEndpoints(after,recovered.Document.Snapshot);await Verify(f,recovered.Document.Snapshot.Features[feature].Result,expected);
    }
    private static async Task UnavailableSourceFreezesLastResult(Fixture f,DocumentSnapshot before,SketchId sketchId,SketchEntityId entity,FeatureId feature)
    {
        var sketch=before.Sketches[sketchId];var draft=new SketchDraft(sketch);draft.SetConstruction([entity],true);await f.Store(draft.Value);
        Assert.True(f.Session.Snapshot.Features[feature].IsStale);Assert.Equal(before.Features[feature].Result,f.Session.Snapshot.Features[feature].Result);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);draft=new(sketch);draft.RemoveEntities([entity]);
        if(before.Parameters.Bindings.Any(b=>b.Target==ParameterTarget.SketchCurve(sketchId,entity,"OffsetDistance",0)))
        {
            int assets=f.Assets.Count;await Assert.ThrowsAsync<CadValidationException>(()=>f.Store(draft.Value));
            Assert.Same(before,f.Session.Snapshot);Assert.Equal(assets,f.Assets.Count);return;
        }
        await f.Store(draft.Value);
        Assert.True(f.Session.Snapshot.Features[feature].IsStale);Assert.Equal(before.Features[feature].Result,f.Session.Snapshot.Features[feature].Result);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets{get;}=new();public OcctGeometryKernel Kernel{get;}=new();public CadDocumentSession Session{get;}public DefinitionId Part{get;}
        public Fixture(){var(doc,part)=SketchCommandTests.Document();Part=part;Session=new(doc,Assets,Kernel,new InlineSessionDispatcher());}
        public Task Store(CadSketch sketch)=>Session.ExecuteAsync(new UpsertSketchCommand(sketch,new ManagedSketchConstraintSolver()));
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
