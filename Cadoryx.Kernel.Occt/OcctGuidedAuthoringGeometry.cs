using System.Collections.Immutable;
using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private static SketchCurve2d DirectedCurve(SketchBoundaryCurve edge)
    {
        if(edge.Expression is not null)throw new CadValidationException("Use the expression chain for an expression path.");
        if(edge.Freeform is {} freeform)return OcctFreeformSketchGeometry.Create(freeform);
        if(edge.Ellipse is {} ellipse)
        {
            var p=ellipse.Principal();return SketchCurve2d.EllipticArc(new(ellipse.Center.X,ellipse.Center.Y),
                p.MajorRadius,p.MinorRadius,p.StartAngle,p.SweepAngle,p.AxisAngle);
        }
        if(edge.BezierControl is {} control)return SketchCurve2d.Bezier([new(edge.Start.X,edge.Start.Y),new(control.X,control.Y),new(edge.End.X,edge.End.Y)]);
        if(!edge.SplineControls.IsEmpty)
        {
            var definition=edge.SplineDefinition;definition.Validate();
            return SketchCurve2d.BSpline(edge.SplineControls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),
                definition.Knots,definition.Multiplicities,SketchSplineGeometry.Degree);
        }
        if(edge.Middle is {} middle)
        {
            var a=SketchArcGeometry.Through(edge.Start,middle,edge.End);
            return SketchCurve2d.CircularArc(new(a.Center.X,a.Center.Y),a.Radius,a.StartAngle,a.SweepAngle);
        }
        return SketchCurve2d.Segment(new(edge.Start.X,edge.Start.Y),new(edge.End.X,edge.End.Y));
    }
    private static Shape AuthoringPlaced(Shape source,RigidTransform3d frame)
    {using(source)using(var transform=OcctGeometryBridge.ToNative(frame))return source.Transformed(transform);}
    private static Shape CopyAuthoringShape(Shape source)
    {using var transform=OcctGeometryBridge.ToNative(RigidTransform3d.Identity);return source.Transformed(transform);}
    private const double GuidedCurveTolerance=1e-10;
    private static Shape PrepareGuidedWire(Shape source,CancellationToken token,GuidedModelingRecorder? recorder=null,string scope="wire",Guid? channel=null,int? station=null)
    {
        token.ThrowIfCancellationRequested();
        // This is a bounded conversion of the native author curve, not a fit of
        // display samples. The source recipe and original topology stay intact.
        using var prepared=GuidedSweepPlan.PrepareOffsetCurves(source,GuidedCurveTolerance,64,11);
        if(prepared.Diagnostics.ApproximationError is not {} error||!double.IsFinite(error)||error<0||error>GuidedCurveTolerance)
            throw new CadValidationException("Guided curve preparation did not meet its declared approximation bound.");
        var wire=prepared.RequireShape();RequireClear(wire,token);
        recorder?.Record(GuidedModelingOperation.CurvePreparation,scope,channel,station,[source],wire,prepared.Diagnostics,GuidedCurveTolerance,64,11,
            nativeOptions:new{Tolerance=GuidedCurveTolerance,MaximumSegments=64,MaximumDegree=11});
        return CopyAuthoringShape(wire);
    }
    private static double AuthoringMeasure(Shape shape,InspectionPropertyKind property)
    {
        var parts=shape.GetSubShapes(property==InspectionPropertyKind.Area?ShapeKind.Face:ShapeKind.Solid);
        try{return parts.Sum(p=>Math.Abs(p.InspectProperties(property).Mass));}
        finally{foreach(var part in parts)part.Dispose();}
    }
    private static void RequireClear(Shape shape,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!shape.GetValidationReport(geometryChecks:true,exact:true).IsValid)
            throw new CadValidationException("Guided authoring produced an invalid BRep.");
        if(!shape.GetSelfInterferenceReport().IsClear)
            throw new CadValidationException("Guided authoring has native self-interference or an unclassified fault.");
        token.ThrowIfCancellationRequested();
    }
    private static void RequireOutput(Shape shape,GuidedOutput output,CancellationToken token)
    {
        RequireClear(shape,token);var counts=shape.GetTopologySummary().UniqueCounts;
        if(output==GuidedOutput.Solid?(counts.SolidCount<1||AuthoringMeasure(shape,InspectionPropertyKind.Volume)<=1e-9):
            (counts.SolidCount!=0||counts.FaceCount==0||AuthoringMeasure(shape,InspectionPropertyKind.Area)<=1e-9))
            throw new CadValidationException("Guided authoring output differs from the explicitly requested solid or sheet.");
    }
    private static Shape SectionWire(SketchProfile profile,RigidTransform3d frame,CancellationToken token)
    {
        AuthoringSection.RequireSingleBoundary(profile);
        using var face=OcctSketchProfileGeometry.CreateFace(profile,false);RequireClear(face,token);
        if(profile.BoundaryCurves.Any(c=>c.Expression is not null))
            return AuthoringPlaced(GuidedExpressionChain(profile.BoundaryCurves,true).BuildWire(SketchPlane.XY),frame);
        var wires=face.GetSubShapes(ShapeKind.Wire);
        try
        {
            if(wires.Length!=1)throw new CadValidationException("A channel requires exactly one section boundary.");
            using var transform=OcctGeometryBridge.ToNative(frame);return wires[0].Transformed(transform);
        }
        finally{foreach(var wire in wires)wire.Dispose();}
    }
    private static SketchProfile OuterBoundary(SketchProfile p)=>p with{Holes=[],PolygonHoles=[],MixedHoles=[],SplineHoles=[],Islands=[]};
    private static IEnumerable<SketchProfile> HoleBoundaries(SketchProfile p)=>p.Holes.Select(c=>SketchProfile.FromCircle(c.Center,c.Radius))
        .Concat(p.PolygonHoles.Select(points=>new SketchProfile(points)))
        .Concat(p.MixedHoles.Select(curves=>new SketchProfile([]){BoundaryCurves=curves}))
        .Concat(p.SplineHoles.Select(spline=>new SketchProfile([]){Spline=spline}));
    private static bool HasLinePrimitive(SketchBoundaryCurve edge)
    {
        if(edge.Expression is {} expression)
        {
            // Offset, reversal and finite continuation of a line remain a line.
            // A canonical circle or a sampled straight-looking spline is not this proof.
            while(expression.Basis is {} basis)expression=basis;
            edge=expression.Primitive!;
        }
        return edge.Middle is null&&edge.BezierControl is null&&edge.SplineControls.IsEmpty&&
            edge.Ellipse is null&&edge.Freeform is null;
    }
    private static bool IsPrismSweep(SweepRecipe recipe)=>recipe.Path.Curves.Length==1&&HasLinePrimitive(recipe.Path.Curves[0]);
    private static bool IsPrismLoftChannel(LoftRecipe recipe,LoftChannel channel)=>recipe.Ruled&&recipe.Stations.Length==2&&
        recipe.Stations[0].Rotation==recipe.Stations[1].Rotation&&SketchProfileReference.SameProfile(channel.Profiles[0],channel.Profiles[1]);
    private static Shape BuildSweep(SweepRecipe recipe,CancellationToken token,GuidedModelingRecorder? recorder=null)
    {
        SketchEvaluation first,last;Shape pathWire;
        if(recipe.Path.Curves.Any(c=>c.Expression is not null))
        {
            var expressions=GuidedExpressionChain(recipe.Path.Curves,false);
            if(expressions.IsClosed)throw new CadValidationException("A sweep expression path must remain open.");
            first=expressions.Curves[0].Evaluate(0);last=expressions.Curves[^1].Evaluate(1);pathWire=expressions.BuildWire(SketchPlane.XY);
        }
        else
        {
            var curves=recipe.Path.Curves.Select(DirectedCurve).ToArray();var chain=SketchCurveChain2d.Create(curves,requireClosed:false);
            if(chain.IsClosed||!chain.Curves.Select((c,i)=>ReferenceEquals(c,curves[i])).All(x=>x))
                throw new CadValidationException("Native path preparation attempted to change the authored direction/order.");
            first=curves[0].Evaluate(0);last=curves[^1].Evaluate(1);pathWire=chain.BuildWire(SketchPlane.XY);
        }
        using var localPath=pathWire;var start=recipe.Path.Placement.Apply(new(first.Point.X,first.Point.Y,0));
        var tangent=recipe.Path.Placement.Rotation.Rotate(new(first.FirstDerivative.X,first.FirstDerivative.Y,0)).Normalized();
        if((recipe.Section.Placement.Translation-start).Length>1e-6||
            recipe.Section.Placement.Rotation.Rotate(Vector3d.UnitZ).Dot(tangent)<1-1e-7)
            throw new CadValidationException("Place the section origin at the authored path start with its normal in the forward tangent direction.");
        using var spine=AuthoringPlaced(CopyAuthoringShape(localPath),recipe.Path.Placement);RequireClear(spine,token);
        Vector3d? straightDelta=IsPrismSweep(recipe)?
            recipe.Path.Placement.Rotation.Rotate(new(last.Point.X-first.Point.X,last.Point.Y-first.Point.Y,0)):null;
        using var preparedSpine=straightDelta is null?PrepareGuidedWire(spine,token,recorder,"sweep-spine"):null;
        Shape CheckedBoundary(Shape result)
        {
            RequireOutput(result,recipe.Output,token);
            if(recipe.Output==GuidedOutput.Solid&&result.GetTopologySummary().UniqueCounts.SolidCount!=1)
                throw new CadValidationException("A single sweep boundary unexpectedly split into multiple solids.");
            return CopyAuthoringShape(result);
        }
        int boundary=0;
        Shape SweepBoundary(SketchProfile p)
        {
            string scope=$"sweep-boundary[{boundary++}]";
            using var wire=SectionWire(p,recipe.Section.Placement,token);
            if(straightDelta is {} delta)
            {
                // A fixed section swept along one proven line is exactly a prism.
                // Use the authored world displacement, including any permitted obliqueness.
                using var direction=GpVec.Create(delta.X,delta.Y,delta.Z);
                if(recipe.Output==GuidedOutput.Sheet)
                {using var sheet=wire.Extrude(direction);recorder?.Record(GuidedModelingOperation.Prism,scope,null,null,[wire],sheet);return CheckedBoundary(sheet);}
                using var face=AuthoringPlaced(OcctSketchProfileGeometry.CreateFace(p,false,canonicalExpressions:true),recipe.Section.Placement);
                RequireClear(face,token);using var solid=face.Extrude(direction);
                recorder?.Record(GuidedModelingOperation.Prism,scope,null,null,[face],solid);return CheckedBoundary(solid);
            }
            using var preparedSection=PrepareGuidedWire(wire,token,recorder,scope+"/section");
            using var plan=GuidedSweepPlan.Create(preparedSpine!,[new(preparedSection,WithContact:false,WithCorrection:false)],new()
            {
                Frame=GuidedSweepFrame.CorrectedFrenet,Transition=(GuidedSweepTransition)recipe.Corner,
                SolidPolicy=recipe.Output==GuidedOutput.Solid?SweepSolidPolicy.RequireSolid:SweepSolidPolicy.Shell,
                Tolerance3d=GuidedCurveTolerance,ToleranceBoundary=GuidedCurveTolerance
            });
            using var built=plan.Build();
            if(built.Diagnostics.ApproximationError is not {} error||!double.IsFinite(error)||error<0||error>1e-7)
                throw new CadValidationException("Guided sweep exceeded its native surface approximation bound.");
            recorder?.Record(GuidedModelingOperation.Sweep,scope,null,null,[preparedSpine!,preparedSection],built.RequireShape(),built.Diagnostics,
                plan.Options.Tolerance3d,plan.Options.MaximumSegments,plan.Options.MaximumDegree,plan.Options.ToleranceBoundary,plan.Options.ToleranceAngular,plan.Options);
            return CheckedBoundary(built.RequireShape());
        }
        Shape Region(SketchProfile profile)
        {
            var holes=HoleBoundaries(profile).ToArray();ValidatePlanarRegion(OuterBoundary(profile),holes,profile.Islands.Select(i=>OuterBoundary(i.Profile)).ToArray(),token);
            Shape current=SweepBoundary(OuterBoundary(profile));
            try
            {
                foreach(var p in holes)
                {
                    using var hole=SweepBoundary(p);RequireContained(current,hole,token);
                    var next=current.Cut(hole);
                    try{recorder?.Record(GuidedModelingOperation.BooleanCut,"sweep-material",null,null,[current,hole],next);}catch{next.Dispose();throw;}
                    current.Dispose();current=next;
                }
                if(profile.Islands.IsEmpty){RequireOutput(current,recipe.Output,token);return current;}
                var pieces=new List<Shape>{current};
                try
                {
                    foreach(var island in profile.Islands)pieces.Add(Region(island.Profile));
                    var composed=ShapeFactory.CreateCompound(pieces);
                    try{RequireOutput(composed,recipe.Output,token);recorder?.Record(GuidedModelingOperation.Compound,"sweep-islands",null,null,pieces,composed);return composed;}catch{composed.Dispose();throw;}
                }
                finally{foreach(var piece in pieces.Skip(1))piece.Dispose();current.Dispose();}
            }
            catch{current.Dispose();throw;}
        }
        return Region(recipe.Section.Profile);
    }
    private static void RequireContained(Shape parent,Shape child,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();using var common=parent.Common(child);
        double childVolume=AuthoringMeasure(child,InspectionPropertyKind.Volume);
        double commonVolume=AuthoringMeasure(common,InspectionPropertyKind.Volume);
        if(childVolume<=1e-9||Math.Abs(childVolume-commonVolume)>Math.Max(1e-6,childVolume*1e-7))
            throw new CadValidationException("A guided hole/island is not contained along the entire authored volume.");
    }
    private static void ValidatePlanarRegion(SketchProfile root,IReadOnlyList<SketchProfile> holes,IReadOnlyList<SketchProfile> islands,CancellationToken token)
    {
        var faces=new List<Shape>();var wires=new List<Shape>();
        try
        {
            foreach(var p in new[]{root}.Concat(holes).Concat(islands))
            {
                faces.Add(OcctSketchProfileGeometry.CreateFace(p,false));RequireClear(faces[^1],token);
                wires.Add(SectionWire(p,RigidTransform3d.Identity,token));
            }
            void Inside(int parent,int child)
            {
                using var common=faces[parent].Common(faces[child]);
                double area=AuthoringMeasure(faces[child],InspectionPropertyKind.Area);
                if(wires[parent].DistanceTo(wires[child]).Distance<=1e-7||
                    Math.Abs(AuthoringMeasure(common,InspectionPropertyKind.Area)-area)>Math.Max(1e-7,area*1e-8))
                    throw new CadValidationException("A section child must be strictly inside its author-selected parent boundary.");
            }
            for(int i=1;i<=holes.Count;i++)
            {
                Inside(0,i);
                for(int j=1;j<i;j++)
                {
                    using var common=faces[i].Common(faces[j]);
                    if(wires[i].DistanceTo(wires[j]).Distance<=1e-7||AuthoringMeasure(common,InspectionPropertyKind.Area)>1e-8)
                        throw new CadValidationException("Section holes touch or overlap.");
                }
            }
            for(int i=holes.Count+1;i<faces.Count;i++)
            {
                for(int j=holes.Count+1;j<i;j++)
                {
                    using var common=faces[i].Common(faces[j]);
                    if(wires[i].DistanceTo(wires[j]).Distance<=1e-7||AuthoringMeasure(common,InspectionPropertyKind.Area)>1e-8)
                        throw new CadValidationException("Section islands touch or overlap.");
                }
                int parents=0;
                for(int j=1;j<=holes.Count;j++)
                {
                    using var common=faces[j].Common(faces[i]);double area=AuthoringMeasure(faces[i],InspectionPropertyKind.Area);
                    if(wires[j].DistanceTo(wires[i]).Distance>1e-7&&Math.Abs(AuthoringMeasure(common,InspectionPropertyKind.Area)-area)<Math.Max(1e-7,area*1e-8))parents++;
                }
                if(parents!=1)throw new CadValidationException("A fixed-profile island must belong strictly to exactly one hole.");
            }
        }
        finally{foreach(var face in faces)face.Dispose();foreach(var wire in wires)wire.Dispose();}
    }
    private static Shape BuildLoft(LoftRecipe recipe,CancellationToken token,GuidedModelingRecorder? recorder=null)
    {
        var built=new Dictionary<Guid,Shape>();
        try
        {
            for(int station=0;station<recipe.Stations.Length;station++)
            {
                foreach(var parent in recipe.Channels)
                {
                    var children=recipe.Channels.Where(c=>c.Parent==parent.Id).Select(c=>c.Profiles[station]).ToArray();
                    ValidatePlanarRegion(parent.Profiles[station],children,[],token);
                }
            }
            foreach(var channel in recipe.Channels)
            {
                var wires=new List<Shape>();
                var faces=new List<Shape>();
                try
                {
                    for(int i=0;i<recipe.Stations.Length;i++)
                    {
                        wires.Add(SectionWire(channel.Profiles[i],recipe.Stations[i],token));
                        faces.Add(AuthoringPlaced(OcctSketchProfileGeometry.CreateFace(channel.Profiles[i],false),recipe.Stations[i]));
                    }
                    using var filledSections=ShapeFactory.CreateCompound(faces);RequireClear(filledSections,token);
                    using var input=ShapeFactory.CreateCompound(wires);RequireClear(input,token);
                    if(IsPrismLoftChannel(recipe,channel))
                    {
                        // Equal directed profiles in equally oriented planes have exact
                        // straight rulings. Avoid rebuilding their offset curves as splines.
                        var delta=recipe.Stations[1].Translation-recipe.Stations[0].Translation;
                        using var direction=GpVec.Create(delta.X,delta.Y,delta.Z);
                        using var face=recipe.Output==GuidedOutput.Solid?
                            AuthoringPlaced(OcctSketchProfileGeometry.CreateFace(channel.Profiles[0],false,canonicalExpressions:true),recipe.Stations[0]):null;
                        using var prism=(face??wires[0]).Extrude(direction);RequireOutput(prism,recipe.Output,token);
                        if(recipe.Output==GuidedOutput.Solid&&prism.GetTopologySummary().UniqueCounts.SolidCount!=1)
                            throw new CadValidationException("A single loft channel unexpectedly split into multiple solids.");
                        recorder?.Record(GuidedModelingOperation.Prism,"loft-channel",channel.Id,null,[face??wires[0]],prism);
                        built.Add(channel.Id,CopyAuthoringShape(prism));continue;
                    }
                    var preparedWires=new List<Shape>();
                    try
                    {
                        for(int i=0;i<wires.Count;i++)preparedWires.Add(PrepareGuidedWire(wires[i],token,recorder,"loft-section",channel.Id,i));
                        var options=new GuidedLoftOptions{Solid=recipe.Output==GuidedOutput.Solid,
                            Ruled=recipe.Ruled,CorrectCompatibility=true,Tolerance=GuidedCurveTolerance};
                        using var result=GuidedLoft.Build(preparedWires,options);
                        var shape=result.RequireShape();RequireOutput(shape,recipe.Output,token);
                        if(recipe.Output==GuidedOutput.Solid&&shape.GetTopologySummary().UniqueCounts.SolidCount!=1)
                            throw new CadValidationException("A single loft channel unexpectedly split into multiple solids.");
                        recorder?.Record(GuidedModelingOperation.Loft,"loft-channel",channel.Id,null,preparedWires,shape,result.Diagnostics,
                            options.Tolerance,degree:options.MaximumDegree,nativeOptions:options);
                        built.Add(channel.Id,CopyAuthoringShape(shape));
                    }
                    finally{foreach(var wire in preparedWires)wire.Dispose();}
                }
                finally{foreach(var wire in wires)wire.Dispose();foreach(var face in faces)face.Dispose();}
            }
            foreach(var channel in recipe.Channels.Where(c=>c.Parent is not null))RequireContained(built[channel.Parent!.Value],built[channel.Id],token);
            foreach(var group in recipe.Channels.Where(c=>c.Parent is not null).GroupBy(c=>c.Parent))
            {
                var siblings=group.ToArray();
                for(int i=0;i<siblings.Length;i++)for(int j=0;j<i;j++)
                {
                    using var pair=ShapeFactory.CreateCompound([built[siblings[i].Id],built[siblings[j].Id]]);RequireClear(pair,token);
                }
            }
            Shape Compose(Guid id)
            {
                Shape current=CopyAuthoringShape(built[id]);
                try
                {
                    foreach(var child in recipe.Channels.Where(c=>c.Parent==id))
                    {
                        using var remove=Compose(child.Id);token.ThrowIfCancellationRequested();
                        var next=current.Cut(remove);
                        try{recorder?.Record(GuidedModelingOperation.BooleanCut,"loft-material",id,null,[current,remove],next);}catch{next.Dispose();throw;}
                        current.Dispose();current=next;
                    }
                    RequireOutput(current,recipe.Output,token);return current;
                }
                catch{current.Dispose();throw;}
            }
            return Compose(recipe.Channels.Single(c=>c.Parent is null).Id);
        }
        finally{foreach(var shape in built.Values)shape.Dispose();}
    }
}
