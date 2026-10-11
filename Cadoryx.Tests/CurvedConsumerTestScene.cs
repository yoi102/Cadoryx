using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

// Independent author fixtures and analytic volume references. Existing80/104
// regression sources remain untouched; these exercise asynchronous consumers.
internal sealed class CurvedConsumerTestScene:IAsyncDisposable
{
    internal static readonly string[] Kinds=["bezier-tube","spline-tube","circle-sweep","bezier-sweep","spline-sweep","circle-loft","bezier-loft","spline-loft"];
    internal MemoryAssetStore Assets{get;}=new();
    internal OcctGeometryKernel Kernel{get;}=new();
    internal CadDocumentSession Session{get;}
    internal CadDocumentViewModel Document{get;}
    internal DefinitionId Part{get;}
    internal GeometryRecipe Recipe{get;private set;}=null!;
    internal SketchId FirstSketch{get;private set;}
    internal SketchEntityId FirstExpression{get;private set;}
    internal FeatureId? Feature{get;private set;}
    internal double OriginalVolume{get;private set;}
    internal double EditedVolume{get;private set;}
    internal double EditedOffset{get;private set;}
    internal bool IsLoft=>Recipe is LoftRecipe;
    private CurvedConsumerTestScene()
    {
        var(initial,part)=SketchCommandTests.Document();Part=part;
        Session=new(initial,Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());
    }
    internal static async Task<CurvedConsumerTestScene> Create(string kind,bool reverse,bool tilted,bool withFeature)
    {
        Assert.Contains(kind,Kinds);var f=new CurvedConsumerTestScene();
        try
        {
            var world=tilted?new RigidTransform3d(new(11,-7,3),Quaterniond.FromEulerDegrees(23,41,17)):RigidTransform3d.Identity;
            Author first,last;Author? path=null;
            if(kind.EndsWith("-tube",StringComparison.Ordinal))
            {
                path=Polynomial(f.Part,kind=="spline-tube",1,world,.03,reverse);var p=Curve(path.Sketch,path.Expression).Geometry.Evaluate(0);
                var tangent=new Vector3d(p.First.X,p.First.Y,0).Normalized();
                var rotation=Quaterniond.FromAxisAngle(Vector3d.UnitZ.Cross(tangent),Math.Acos(Vector3d.UnitZ.Dot(tangent)));
                first=Circle(f.Part,.03,world*new RigidTransform3d(new(p.Point.X,p.Point.Y,0),rotation),.01,false);last=first;
                double length=Math.Sqrt(5)/2+Math.Asinh(2)/4-2*.03*Math.Atan(2);
                f.OriginalVolume=Math.PI*.04*.04*length;f.EditedVolume=Math.PI*.045*.045*length;f.EditedOffset=.015;
            }
            else if(kind.EndsWith("-sweep",StringComparison.Ordinal))
            {
                string family=kind.Split('-')[0];path=Circle(f.Part,12,world,.1,reverse,true);var p=Curve(path.Sketch,path.Expression).Geometry.At(0);
                var rotation=reverse?Quaterniond.FromAxisAngle(new(1,1,1),Math.Tau/3):Quaterniond.FromEulerDegrees(-90,0,0);
                first=Section(f.Part,family,1,world*new RigidTransform3d(new(p.X,p.Y,0),rotation),.03,reverse);last=first;
                f.OriginalVolume=Area(family,1,.03)*12.1*Math.PI/2;f.EditedVolume=Area(family,1,.04)*12.1*Math.PI/2;f.EditedOffset=.04;
            }
            else
            {
                string family=kind.Split('-')[0];const double scale=1.3;
                first=Section(f.Part,family,1,world,.03,reverse);last=Section(f.Part,family,scale,world*RigidTransform3d.Translate(tilted?1:0,tilted?.4:0,5),.03*scale,reverse);
                f.OriginalVolume=5*Area(family,1,.03)*(1+scale+scale*scale)/3;
                double d0=.04,d1=.03*scale;f.EditedOffset=d0;
                f.EditedVolume=family=="circle"?5*Math.PI*(Math.Pow(.5+d0,2)+(.5+d0)*(.5*scale+d1)+Math.Pow(.5*scale+d1,2))/3:
                    5*(Pair(1,scale,1,scale)/3-L*Pair(1,scale,d0,d1)+Q*Pair(d0,d1,d0,d1));
            }
            await f.Store(first.Sketch);if(last!=first)await f.Store(last.Sketch);if(path is not null)await f.Store(path.Sketch);
            f.FirstSketch=first.Sketch.Id;f.FirstExpression=first.Expression;
            var s0=f.Session.Snapshot.Sketches[first.Sketch.Id];
            if(path is not null)
            {
                var p=f.Session.Snapshot.Sketches[path.Sketch.Id];
                f.Recipe=new SweepRecipe(new(SketchProfileBuilder.Mixed(s0,[first.Expression]),s0.Plane){Source=SketchProfileReference.CreateMixed(s0,[first.Expression])},
                    new SketchPathReference(p.Id,p.Revision,[new(path.Expression)]).Resolve(f.Session.Snapshot,f.Part));
            }
            else
            {
                var s1=f.Session.Snapshot.Sketches[last.Sketch.Id];
                f.Recipe=new LoftRecipe([s0.Plane,s1.Plane],[new(Guid.NewGuid(),null,[SketchProfileBuilder.Mixed(s0,[first.Expression]),SketchProfileBuilder.Mixed(s1,[last.Expression])])
                    {Sources=[SketchProfileReference.CreateMixed(s0,[first.Expression]),SketchProfileReference.CreateMixed(s1,[last.Expression])]}],Ruled:true);
            }
            if(withFeature)
            {
                await f.Session.ExecuteAsync(new AddBodyCommand(f.Recipe,kind,f.Part));f.Feature=f.Session.Snapshot.Features.Keys.Single();
                Verify(f.Assets,f.Session.Snapshot.Features[f.Feature.Value].Result,f.OriginalVolume);
            }
            f.Document.SelectedTargetPart=f.Part;return f;
        }
        catch{await f.DisposeAsync();throw;}
    }
    internal GuidedEditorViewModel Guided(IGeometryKernel kernel)
    {
        var editor=new GuidedEditorViewModel(Document,kernel,IsLoft?GuidedEditorKind.Loft:GuidedEditorKind.Sweep,Feature);
        if(Feature is not null)return editor;
        if(Recipe is SweepRecipe sweep)
        {
            editor.SelectedSweepProfile=editor.Profiles.Single(p=>p.Section.Source?.SketchId==sweep.Section.Source!.SketchId&&p.Section.Source.MixedBoundaryIds.SequenceEqual(sweep.Section.Source.MixedBoundaryIds));
            editor.SelectedPathSketch=editor.PathSketches.Single(p=>p.Value==sweep.Path.Source!.Sketch);
            editor.SelectedAvailableEdge=editor.AvailableEdges.Single(p=>p.Entity==sweep.Path.Source!.Edges[0].Entity);editor.AddEdgeCommand.Execute(null);
        }
        else
        {
            var loft=(LoftRecipe)Recipe;
            foreach(var source in loft.Channels[0].Sources)
            {
                editor.AddStationCommand.Execute(null);editor.SelectedAssignmentProfile=editor.Profiles.Single(p=>p.Section.Source?.SketchId==source!.SketchId&&p.Section.Source.MixedBoundaryIds.SequenceEqual(source.MixedBoundaryIds));
                editor.AssignProfileCommand.Execute(null);
            }
            editor.Ruled=true;
        }
        return editor;
    }
    internal ParameterEditorViewModel Parameters(IGeometryKernel kernel)
    {
        Assert.NotNull(Feature);var editor=new ParameterEditorViewModel(Document,kernel);
        editor.ParameterName="section_offset";editor.Expression=EditedOffset.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" mm";
        editor.ApplyParameterCommand.Execute(null);var target=ParameterTarget.SketchCurve(FirstSketch,FirstExpression,"OffsetDistance",0);
        editor.DrivingTarget=editor.Targets.Single(t=>t.Target==target);editor.BindDimensionCommand.Execute(null);
        Assert.Single(editor.Bindings);Assert.False(editor.HasPendingInput);return editor;
    }
    internal static void Verify(IAssetStore assets,GeometryAssetRef geometry,double volume)
    {
        using var shape=OcctGeometryBridge.ReadShape(geometry,assets);Assert.Equal(BodyKind.Solid,geometry.Kind);
        Assert.Equal(1,shape.GetTopologySummary().UniqueCounts.SolidCount);Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);
        Assert.InRange(Math.Abs(geometry.VolumeMm3-volume),0,1e-7);
    }
    internal static void SameAuthors(DocumentSnapshot expected,DocumentSnapshot actual)
    {
        foreach(var s in expected.Sketches.Values)
        {
            var a=actual.Sketches[s.Id];Assert.Equal(s.Revision,a.Revision);Assert.True(SketchExpressionEntities.SameTable(s,a));
            Assert.Equal(s.Points.ToArray(),a.Points.ToArray());Assert.Equal(s.Constraints.ToArray(),a.Constraints.ToArray());
        }
        Assert.Equal(expected.Features.Keys.OrderBy(i=>i.Value),actual.Features.Keys.OrderBy(i=>i.Value));
        foreach(var feature in expected.Features.Values)Assert.Equal(feature,actual.Features[feature.Id]);
        Assert.Equal(expected.Parameters,actual.Parameters);
    }
    private Task Store(CadSketch s)=>Session.ExecuteAsync(new UpsertSketchCommand(s,new ManagedSketchConstraintSolver()));
    public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    private sealed record Author(CadSketch Sketch,SketchEntityId Expression);
    private static Author Circle(DefinitionId part,double radius,RigidTransform3d pose,double offset,bool reverse,bool quarter=false)
    {
        var d=new SketchDraft(CadSketch.Create(part,"Circle author",pose));d.AddCircle(new(0,0),radius,construction:true);var id=d.AddExactOffset(d.Value.Circles[0].Id,offset);
        if(quarter)d.EditExpression(id,Curve(d.Value,id).Geometry.Trim(0,.25));if(reverse)d.EditExpression(id,Curve(d.Value,id).Geometry.Reverse());d.SetConstruction([id],false);d.Change(FixAuthors);return new(d.Value,id);
    }
    private static Author Polynomial(DefinitionId part,bool spline,double scale,RigidTransform3d pose,double offset,bool reverse)
    {
        var d=new SketchDraft(CadSketch.Create(part,"Polynomial author",pose));
        if(spline)d.AddSpline([new(-.5*scale,0),new(-scale/6,2*scale/3),new(scale/6,2*scale/3),new(.5*scale,0)],construction:true);
        else d.AddBezier(new(-.5*scale,0),new(0,scale),new(.5*scale,0),construction:true);
        var id=d.AddExactOffset(spline?d.Value.Splines[0].Id:d.Value.Beziers[0].Id,offset);
        if(reverse)d.EditExpression(id,Curve(d.Value,id).Geometry.Reverse());d.SetConstruction([id],false);d.Change(FixAuthors);return new(d.Value,id);
    }
    private static Author Section(DefinitionId part,string family,double scale,RigidTransform3d pose,double offset,bool reverse)=>family=="circle"?Circle(part,.5*scale,pose,offset,reverse):Polynomial(part,family=="spline",scale,pose,offset,reverse);
    private static SketchExpressionCurve Curve(CadSketch s,SketchEntityId id)=>s.ExpressionCurves.Single(c=>c.Id==id);
    private static CadSketch FixAuthors(CadSketch s)
    {
        var generated=s.ExpressionCurves.SelectMany(c=>new[]{c.Endpoints!.Start,c.Endpoints.End}).ToHashSet();
        return s with{Constraints=[..s.Constraints,..s.Points.Where(p=>!generated.Contains(p.Id)).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position))]};
    }
    private static double L=>3/(2*Math.Sqrt(5))+Math.Asinh(2)/4;
    private static double Q=>Math.Atan(2)-.4;
    private static double Area(string f,double s,double d)=>f=="circle"?Math.PI*Math.Pow(.5*s+d,2):s*s/3-s*d*L+d*d*Q;
    private static double Pair(double a,double b,double c,double d)=>(2*a*c+a*d+b*c+2*b*d)/6;
}
