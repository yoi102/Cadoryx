using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.OffsetPrototype;

var output=Path.GetFullPath(args.Single());
if(Directory.Exists(output))throw new InvalidOperationException("Evidence already exists.");
Directory.CreateDirectory(output);
var cases=new List<object>();
int exit=0;
void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
void Near(double expected,double actual,string message,double tolerance=1e-7)=>Check(Math.Abs(expected-actual)<=tolerance,message+$" expected={expected:R}, actual={actual:R}");
void Run(string name,Action test)
{
    try{test();cases.Add(new{name,outcome="PASS"});}
    catch(Exception error){cases.Add(new{name,outcome="FAIL",error=error.ToString()});exit=1;}
}
SketchDraft New()=>new(CadSketch.Create(DefinitionId.New(),"Managed offset prototype",RigidTransform3d.Identity));
void Polygon(SketchDraft d,params Point2d[] positions)
{
    var points=positions.Select(p=>new SketchPoint(SketchEntityId.New(),p)).ToImmutableArray();
    var lines=Enumerable.Range(0,points.Length).Select(i=>new SketchLine(SketchEntityId.New(),points[i].Id,points[(i+1)%points.Length].Id)).ToImmutableArray();
    d.Change(s=>s with{Points=s.Points.AddRange(points),Lines=s.Lines.AddRange(lines)});
}
void Reverse(SketchDraft d)=>d.Change(s=>s with{Lines=s.Lines.Select(l=>l with{Start=l.End,End=l.Start}).ToImmutableArray(),
    Arcs=s.Arcs.Select(a=>a with{Start=a.End,End=a.Start}).ToImmutableArray()});
SketchProfile Profile(CadSketch sketch,SketchRegionReference reference)=>reference.CircleId is {} c?SketchProfileBuilder.Circle(sketch,c):
    !reference.MixedCurveIds.IsEmpty?SketchProfileBuilder.Mixed(sketch,reference.MixedCurveIds):SketchProfileBuilder.Polygon(sketch,reference.PolygonLineIds);
double Area(SketchProfile profile)=>profile.Circle is {} circle?Math.PI*circle.Radius*circle.Radius:
    Math.Abs(ProposedSketchLoopOffset.Area(profile.BoundaryCurves.IsEmpty?SketchMixedProfile.Polygon(profile.Points):profile.BoundaryCurves));
double RectangleArea(double w,double h,double d,OffsetJoin join)=>w*h+2*d*(w+h)+
    (d<0||join==OffsetJoin.Miter?4:join==OffsetJoin.Round?Math.PI:2)*d*d;
void Preserved(CadSketch before,CadSketch after)
{
    Check(before.Points.SequenceEqual(after.Points.Take(before.Points.Length)),"Source points changed");
    Check(before.Lines.SequenceEqual(after.Lines.Take(before.Lines.Length)),"Source lines changed");
    Check(before.Arcs.SequenceEqual(after.Arcs.Take(before.Arcs.Length)),"Source arcs changed");
    Check(before.Circles.SequenceEqual(after.Circles.Take(before.Circles.Length)),"Source circles changed");
    Check(before.Constraints.SequenceEqual(after.Constraints),"Source constraints changed");
}
void Rejected(SketchDraft d,Action action)
{
    var before=d.Value;bool undo=d.CanUndo,redo=d.CanRedo;
    try{action();throw new InvalidOperationException("Invalid offset was accepted.");}
    catch(CadValidationException){}
    Check(ReferenceEquals(before,d.Value)&&undo==d.CanUndo&&redo==d.CanRedo,"Rejected operation changed original draft/history");
}
try
{
    foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double distance in new[]{.25,1,2,-.25,-1,-2})foreach(bool reverse in new[]{false,true})
        Run($"rectangle-{join}-{distance}-{reverse}",()=>
        {
            var d=New();Polygon(d,new(0,0),new(20,0),new(20,12),new(0,12));if(reverse)Reverse(d);
            var before=d.Value;var result=ProposedSketchLoopOffset.Create(before,before.Lines[0].Id,distance,join);Preserved(before,result.Sketch);
            var profile=Profile(result.Sketch,result.Reference);profile.Validate();Near(RectangleArea(20,12,distance,join),Area(profile),"Rectangle area");
            var addedArcs=result.Sketch.Arcs.Skip(before.Arcs.Length).ToArray();Check(addedArcs.Length==(join==OffsetJoin.Round&&distance>0?4:0),"Wrong round corner count");
            foreach(var arc in addedArcs)
            {
                var g=SketchAnalyticalCurve.From(result.Sketch,arc.Id).Arc!.Value;Near(distance,g.Radius,"Explicit bridge radius");
                Check(before.Points.Any(p=>double.Hypot(p.Position.X-g.Center.X,p.Position.Y-g.Center.Y)<1e-7),"Bridge has no source corner center");
                Near(Math.PI/2,Math.Abs(g.SweepAngle),"Minor quarter-circle branch");
            }
        });
    foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double distance in new[]{.25,1,-.25,-1})foreach(bool reverse in new[]{false,true})
        Run($"concave-{join}-{distance}-{reverse}",()=>
        {
            var d=New();Polygon(d,new(0,0),new(20,0),new(20,8),new(8,8),new(8,20),new(0,20));if(reverse)Reverse(d);
            var before=d.Value;var result=ProposedSketchLoopOffset.Create(before,before.Lines[0].Id,distance,join);Preserved(before,result.Sketch);
            double coefficient=join==OffsetJoin.Miter?4:join==OffsetJoin.Round?(distance>0?5*Math.PI/4-1:5-Math.PI/4):(distance>0?1.5:4.5);
            Near(256+80*distance+coefficient*distance*distance,Area(Profile(result.Sketch,result.Reference)),"Independent L-region area");
            Check(result.Sketch.Arcs.Length==(join==OffsetJoin.Round?(distance>0?5:1):0),"Concave bridge side/count");
        });
    foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double distance in new[]{1d,-1d})foreach(bool reverse in new[]{false,true})
        Run($"mixed-arc-arc-{join}-{distance}-{reverse}",()=>
        {
            var d=New();d.AddLine(new(-10,0),new(10,0));var bottom=d.Value.Lines[0];
            d.AddArc(new(10,0),new(Math.Sqrt(50),Math.Sqrt(50)),new(0,10),existingStart:bottom.End);
            var first=d.Value.Arcs[0];d.AddArc(new(0,10),new(-8,8),new(-10,0),existingStart:first.End,existingEnd:bottom.Start);
            if(reverse)Reverse(d);var before=d.Value;
            var sourceRadii=before.Arcs.Select(a=>SketchAnalyticalCurve.From(before,a.Id).Arc!.Value.Radius+distance).Order().ToArray();
            var result=ProposedSketchLoopOffset.Create(before,before.Arcs[0].Id,distance,join);Preserved(before,result.Sketch);Profile(result.Sketch,result.Reference).Validate();
            var newRadii=result.Sketch.Arcs.Skip(before.Arcs.Length).Select(a=>SketchAnalyticalCurve.From(result.Sketch,a.Id).Arc!.Value.Radius)
                .Where(r=>Math.Abs(r-Math.Abs(distance))>1e-7).Order().ToArray();
            Check(newRadii.Length==2,"Source arcs were discarded");for(int i=0;i<2;i++)Near(sourceRadii[i],newRadii[i],"Analytical source radius");
        });
    foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double distance in new[]{1d,-1d})foreach(bool reverse in new[]{false,true})
        Run($"tangent-cap-{join}-{distance}-{reverse}",()=>
        {
            var d=New();d.AddRectangle(new(0,0),new(20,10));var top=d.Value.Lines[2];
            d.RemoveEntities(new[]{top.Id});d.AddArc(new(20,10),new(10,20),new(0,10),existingStart:top.Start,existingEnd:top.End);
            if(reverse)Reverse(d);var before=d.Value;var result=ProposedSketchLoopOffset.Create(before,before.Arcs[0].Id,distance,join);
            double expected=(20+2*distance)*(10+distance)+Math.PI*Math.Pow(10+distance,2)/2;
            if(distance>0)expected-=(join==OffsetJoin.Round?2*(1-Math.PI/4):join==OffsetJoin.Bevel?1:0)*distance*distance;
            Near(expected,Area(Profile(result.Sketch,result.Reference)),"Tangent capped profile area");
            Check(result.Sketch.Arcs.Length-before.Arcs.Length==(join==OffsetJoin.Round&&distance>0?3:1),"Tangent joints received spurious corner arcs");
            Preserved(before,result.Sketch);
        });
    foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double distance in new[]{1d,-1d})foreach(bool reverse in new[]{false,true})
        Run($"collinear-{join}-{distance}-{reverse}",()=>
        {
            var d=New();Polygon(d,new(0,0),new(10,0),new(20,0),new(20,12),new(0,12));if(reverse)Reverse(d);
            var result=ProposedSketchLoopOffset.Create(d.Value,d.Value.Lines[0].Id,distance,join);
            Near(RectangleArea(20,12,distance,join),Area(Profile(result.Sketch,result.Reference)),"Collinear profile area");
            Check(result.Sketch.Arcs.Length==(join==OffsetJoin.Round&&distance>0?4:0),"Collinear join received a spurious round arc");
        });
    foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double distance in new[]{1d,-1d})foreach(bool reverse in new[]{false,true})
        Run($"nested-region-{join}-{distance}-{reverse}",()=>
        {
            var d=New();d.AddRectangle(new(0,0),new(100,80));var outer=d.Value.Lines[0].Id;
            d.AddCircle(new(50,40),20);var hole=d.Value.Circles[0].Id;d.AddRectangle(new(40,30),new(60,50));var island=d.Value.Lines[^4].Id;
            d.AddCircle(new(50,40),4);var inner=d.Value.Circles[^1].Id;if(reverse)Reverse(d);
            var before=d.Value;var result=ProposedSketchRegionOffset.Create(before,new[]{outer,hole,island,inner},distance,join);Preserved(before,result.Sketch);
            Check(result.Roots.Length==1&&result.Boundaries.Select(b=>b.Depth).Order().SequenceEqual(new[]{0,1,2,3}),"Containment parity changed");
            double actual=result.Boundaries.Sum(b=>(b.Depth%2==0?1:-1)*Area(Profile(result.Sketch,b.Created)));
            double expected=RectangleArea(100,80,distance,join)-Math.PI*Math.Pow(20-distance,2)+RectangleArea(20,20,distance,join)-Math.PI*Math.Pow(4-distance,2);
            Near(expected,actual,"Nested material area",1e-6);
        });
    foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double distance in new[]{.25,1,-.25,-1})
        Run($"circle-{join}-{distance}",()=>
        {var d=New();d.AddCircle(new(2,3),5);var result=ProposedSketchLoopOffset.Create(d.Value,d.Value.Circles[0].Id,distance,join);Near(Math.PI*Math.Pow(5+distance,2),Area(Profile(result.Sketch,result.Reference)),"Circle offset");});
    foreach(var join in Enum.GetValues<OffsetJoin>())Run("explicit-selected-boundaries-"+join,()=>
    {
        var d=New();d.AddRectangle(new(0,0),new(40,40));var outer=d.Value.Lines[0].Id;d.AddCircle(new(20,20),5);var hole=d.Value.Circles[0].Id;
        var result=ProposedSketchRegionOffset.Create(d.Value,new[]{hole,hole},1,join);
        Check(result.Boundaries.Length==1&&result.Boundaries[0].Depth==0,"Unselected enclosing boundary was implicitly added");
        Near(6,result.Sketch.Circles[^1].Radius,"Explicitly selected hole treated as selected root");
        result=ProposedSketchRegionOffset.Create(d.Value,new[]{outer,d.Value.Lines[1].Id,outer},1,join);
        Check(result.Boundaries.Length==1&&result.Sketch.Circles.SequenceEqual(d.Value.Circles),"Duplicate selection or unselected hole changed authoring");
        d.AddRectangle(new(80,0),new(90,10));var independent=d.Value.Lines[^4].Id;
        result=ProposedSketchRegionOffset.Create(d.Value,new[]{outer,independent},1,join);
        Check(result.Roots.Length==2&&result.Boundaries.Length==2,"Independent roots merged");
    });
    foreach(var join in Enum.GetValues<OffsetJoin>())Run("rejected-region-topology-"+join,()=>
    {
        var d=New();d.AddCircle(new(0,0),5);d.AddCircle(new(12,0),5);
        Rejected(d,()=>ProposedSketchRegionOffset.Create(d.Value,d.Value.Circles.Select(c=>c.Id),2,join));
        Rejected(d,()=>ProposedSketchRegionOffset.Create(d.Value,Array.Empty<SketchEntityId>(),1,join));
        Rejected(d,()=>ProposedSketchRegionOffset.Create(d.Value,new[]{SketchEntityId.New()},1,join));
    });
    foreach(var join in Enum.GetValues<OffsetJoin>())Run("rejected-source-branch-"+join,()=>
    {
        var d=New();d.AddRectangle(new(0,0),new(20,12));d.AddLine(new(0,0),new(-1,-1),start:d.Value.Lines[0].Start);
        Rejected(d,()=>ProposedSketchLoopOffset.Create(d.Value,d.Value.Lines[0].Id,1,join));
    });
    foreach(int count in new[]{128,129})Run("mixed-output-budget-"+count,()=>
    {
        var d=New();Polygon(d,Enumerable.Range(0,count).Select(i=>new Point2d(100*Math.Cos(Math.Tau*i/count),100*Math.Sin(Math.Tau*i/count))).ToArray());
        if(count==129)Rejected(d,()=>ProposedSketchLoopOffset.Create(d.Value,d.Value.Lines[0].Id,1,OffsetJoin.Round));
        else Check(ProposedSketchLoopOffset.Create(d.Value,d.Value.Lines[0].Id,1,OffsetJoin.Round).Reference.MixedCurveIds.Length==256,"Exact mixed output boundary budget");
    });
    foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double distance in new[]{0,double.NaN,double.PositiveInfinity,double.NegativeInfinity,-6d})
        Run($"invalid-distance-{join}-{distance}",()=>{var d=New();d.AddRectangle(new(0,0),new(10,10));Rejected(d,()=>ProposedSketchLoopOffset.Create(d.Value,d.Value.Lines[0].Id,distance,join));});
    foreach(var join in Enum.GetValues<OffsetJoin>())Run("explicit-acute-"+join,()=>
    {
        var d=New();Polygon(d,new(0,0),new(100,0),new(100,.1));
        if(join==OffsetJoin.Miter)Rejected(d,()=>ProposedSketchLoopOffset.Create(d.Value,d.Value.Lines[0].Id,1,join));
        else
        {
            var result=ProposedSketchLoopOffset.Create(d.Value,d.Value.Lines[0].Id,1,join);
            Profile(result.Sketch,result.Reference).Validate();
        }
    });
    Run("invalid-enum",()=>{var d=New();d.AddCircle(new(0,0),2);Rejected(d,()=>ProposedSketchLoopOffset.Create(d.Value,d.Value.Circles[0].Id,1,(OffsetJoin)999));});
    foreach(string kind in new[]{"rectangle","concave","cap","arcs"})foreach(double distance in new[]{1d,-1d})foreach(bool reverse in new[]{false,true})
        Run($"legacy-miter-geometry-{kind}-{distance}-{reverse}",()=>
        {
            var d=New();
            if(kind=="rectangle")d.AddRectangle(new(0,0),new(20,12));
            else if(kind=="concave")Polygon(d,new(0,0),new(20,0),new(20,8),new(8,8),new(8,20),new(0,20));
            else if(kind=="cap")
            {
                d.AddRectangle(new(0,0),new(20,10));var top=d.Value.Lines[2];d.RemoveEntities(new[]{top.Id});
                d.AddArc(new(20,10),new(10,20),new(0,10),existingStart:top.Start,existingEnd:top.End);
            }
            else
            {
                d.AddLine(new(-10,0),new(10,0));var bottom=d.Value.Lines[0];
                d.AddArc(new(10,0),new(Math.Sqrt(50),Math.Sqrt(50)),new(0,10),existingStart:bottom.End);
                var first=d.Value.Arcs[0];d.AddArc(new(0,10),new(-8,8),new(-10,0),existingStart:first.End,existingEnd:bottom.Start);
            }
            if(reverse)Reverse(d);var before=d.Value;var seed=before.Arcs.IsEmpty?before.Lines[0].Id:before.Arcs[0].Id;
            var legacy=SketchLoopOffset.Create(before,seed,distance);var result=ProposedSketchLoopOffset.Create(before,seed,distance,OffsetJoin.Miter);
            var a=Profile(legacy.Sketch,legacy.Reference);var b=Profile(result.Sketch,result.Reference);
            var ca=a.BoundaryCurves.IsEmpty?SketchMixedProfile.Polygon(a.Points):a.BoundaryCurves;
            var cb=b.BoundaryCurves.IsEmpty?SketchMixedProfile.Polygon(b.Points):b.BoundaryCurves;
            Check(ca.SequenceEqual(cb),"Default miter geometry differs from frozen original implementation");
            Preserved(before,result.Sketch);
        });
    foreach(var join in Enum.GetValues<OffsetJoin>())foreach(bool reverse in new[]{false,true})
        Run($"tiny-step-large-coordinate-{join}-{reverse}",()=>
        {
            double scale=.01,angle=.73,translation=1_000_000;
            Point2d Map(Point2d p)=>new(translation+scale*(p.X*Math.Cos(angle)-p.Y*Math.Sin(angle)),
                -translation+scale*(p.X*Math.Sin(angle)+p.Y*Math.Cos(angle)));
            var d=New();Polygon(d,new[]{new Point2d(0,0),new(20,0),new(20,12),new(0,12)}.Select(Map).ToArray());if(reverse)Reverse(d);
            var result=ProposedSketchLoopOffset.Create(d.Value,d.Value.Lines[0].Id,scale*.001,join);
            Near(RectangleArea(20,12,.001,join)*scale*scale,Area(Profile(result.Sketch,result.Reference)),"Small measurable offset area",1e-7);
        });
    foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double scale in new[]{.01,1,1000})foreach(bool reverse in new[]{false,true})
        Run($"large-translated-rotated-{join}-{scale}-{reverse}",()=>
        {
            double angle=.73,translation=1_000_000;
            Point2d Map(Point2d p)=>new(translation+scale*(p.X*Math.Cos(angle)-p.Y*Math.Sin(angle)),
                -translation+scale*(p.X*Math.Sin(angle)+p.Y*Math.Cos(angle)));
            var d=New();Polygon(d,new[]{new Point2d(0,0),new(20,0),new(20,12),new(0,12)}.Select(Map).ToArray());if(reverse)Reverse(d);
            var before=d.Value;var result=ProposedSketchLoopOffset.Create(before,before.Lines[0].Id,scale,join);Preserved(before,result.Sketch);
            Near(RectangleArea(20,12,1,join)*scale*scale,Area(Profile(result.Sketch,result.Reference)),"Translated/rotated material area",1e-7*Math.Max(1,scale*scale));
        });
}
catch(Exception error){Console.Error.WriteLine(error);exit=1;}
var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m=>m.ModuleName).Order().ToArray();
if(modules.Any(m=>m.Contains("OcctSharp",StringComparison.OrdinalIgnoreCase)||m.StartsWith("TK",StringComparison.Ordinal)))exit=1;
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{recordedAt=DateTimeOffset.UtcNow,
    status=exit==0?"PASS":"FAIL",scope="Pure managed analytical prototype only; no native BRep, production UI/tool/storage or full B17 acceptance claimed.",
    cases,loadedModules=modules},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{(exit==0?"PASS":"FAIL")}: {cases.Count} managed cases; no native acceptance claimed.");
return exit;
