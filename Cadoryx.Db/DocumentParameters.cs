using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum ParameterTargetKind { Feature=0, SketchConstraint=1, Datum=2, AssemblyPattern=3, ExactSketchCurve=4, SketchRegion=5 }
public sealed record ParameterTarget(ParameterTargetKind Kind,Guid Owner,Guid? Constraint,string Property,Guid? Curve=null,int? Index=null)
{
    public static ParameterTarget Feature(FeatureId id,string property)=>new(ParameterTargetKind.Feature,id.Value,null,property);
    public static ParameterTarget Sketch(SketchId id,SketchConstraintId constraint,string property="Value")=>new(ParameterTargetKind.SketchConstraint,id.Value,constraint.Value,property);
    public static ParameterTarget Datum(Guid id,string property)=>new(ParameterTargetKind.Datum,id,null,property);
    public static ParameterTarget AssemblyPattern(Guid id,string property)=>new(ParameterTargetKind.AssemblyPattern,id,null,property);
    public static ParameterTarget SketchRegion(SketchId sketch,Guid group)=>new(ParameterTargetKind.SketchRegion,sketch.Value,null,"Distance",group);
    public static ParameterTarget SketchCurve(SketchId id,SketchEntityId curve,string property,int? index=null)=>new(ParameterTargetKind.ExactSketchCurve,id.Value,null,property,curve.Value,index);
}
public sealed record CadParameter(Guid Id,string Name,ParameterDimension Dimension,string Expression,ParameterTarget? Reference=null);
public sealed record ParameterBinding(Guid Id,Guid Parameter,ParameterTarget Target);
public sealed record ParameterBook(ImmutableArray<CadParameter> Parameters,ImmutableArray<ParameterBinding> Bindings)
{
    public static ParameterBook Empty {get;}=new([],[]);
}

/// <summary>Explicit numeric fields only. References read authored dimensions, not approximate scene measurements.</summary>
public static class ParameterTargets
{
    public static ParameterQuantity Read(DocumentSnapshot doc,ParameterTarget target)
    {
        if(target is null||!Enum.IsDefined(target.Kind)||target.Owner==Guid.Empty)throw new CadValidationException("Invalid parameter target identity.");
        if(target.Kind==ParameterTargetKind.SketchRegion)return SketchRegionParameterTargets.Read(doc,target);
        if(target.Kind==ParameterTargetKind.ExactSketchCurve)return SketchCurveParameterTargets.Read(doc,target);
        if(target.Curve is not null||target.Index is not null)throw new CadValidationException("Unexpected curve identity on a parameter target.");
        double value;var dimension=ParameterDimension.Length;
        if(target.Kind==ParameterTargetKind.AssemblyPattern)
        {
            if(target.Constraint is not null||!doc.AssemblyPatterns.TryGetValue(target.Owner,out var pattern))throw new CadValidationException("Assembly array parameter identity is missing.");
            value=target.Property switch
            {"Count" when pattern.Kind!=RepetitionKind.Mirror=>pattern.Count,"Spacing" when pattern.Kind==RepetitionKind.Linear=>pattern.Step,
                "Angle" when pattern.Kind==RepetitionKind.Circular&&!pattern.FullCircle=>pattern.Step,_=>throw new CadValidationException("Unsupported assembly array field.")};
            dimension=target.Property=="Count"?ParameterDimension.Scalar:target.Property=="Angle"?ParameterDimension.Angle:ParameterDimension.Length;
        }
        else if(target.Kind==ParameterTargetKind.Datum)
        {
            if(target.Constraint is not null)throw new CadValidationException("Parameter datum has a hidden constraint.");
            if(doc.Datums.Planes.TryGetValue(target.Owner,out var plane)&&target.Property==(plane.Kind==DatumPlaneKind.Offset?"Offset":plane.Kind==DatumPlaneKind.Angle?"Angle":""))
            {value=plane.Value;if(plane.Kind==DatumPlaneKind.Angle)dimension=ParameterDimension.Angle;}
            else if(doc.Datums.Axes.TryGetValue(target.Owner,out var axis)&&AxisFields(axis).Contains(target.Property))
                value=target.Property=="Offset"?axis.Offset:Component(axis.Origin,target.Property);
            else if(doc.Datums.Points.TryGetValue(target.Owner,out var point)&&point.Kind is DatumPointKind.Independent or DatumPointKind.Offset&&target.Property is "X" or "Y" or "Z")
                value=Component(point.Value,target.Property);
            else throw new CadValidationException("Parameter datum identity or numeric field is invalid.");
        }
        else if(target.Kind==ParameterTargetKind.Feature)
        {
            // These are authored numeric fields, not measurements of a potentially frozen BRep.
            // Existing drivers remain readable while a feature is suppressed or awaits reselection.
            if(target.Constraint is not null||!doc.Features.TryGetValue(new(target.Owner),out var feature))
                throw new CadValidationException("Parameter feature identity is missing.");
            value=(feature.Recipe,target.Property) switch
            {
                (BoxRecipe r,"X")=>r.X,(BoxRecipe r,"Y")=>r.Y,(BoxRecipe r,"Z")=>r.Z,
                (CylinderRecipe r,"Radius")=>r.Radius,(CylinderRecipe r,"Height")=>r.Height,
                (ExtrudeRecipe r,"Distance") when r.Options is null or {Termination:ExtrusionTermination.Distance}=>r.Distance,
                (ExtrudeRecipe {Options:{Side:ExtrusionSide.TwoSides}} r,"Backward")=>r.Options.Backward,
                (RevolveRecipe r,"Angle")=>r.AngleRadians,
                (RepetitionRecipe r,"Count") when r.Kind!=RepetitionKind.Mirror=>r.Count,
                (RepetitionRecipe r,"Spacing") when r.Kind==RepetitionKind.Linear=>r.Step,
                (RepetitionRecipe r,"Angle") when r.Kind==RepetitionKind.Circular&&!r.FullCircle=>r.Step,
                (HolePatternRecipe r,"Count") when r.Layout.Kind!=RepetitionKind.Mirror=>r.Layout.Count,
                (HolePatternRecipe r,"Spacing") when r.Layout.Kind==RepetitionKind.Linear=>r.Layout.Step,
                (HolePatternRecipe r,"Angle") when r.Layout.Kind==RepetitionKind.Circular&&!r.Layout.FullCircle=>r.Layout.Step,
                (ExtrusionPatternRecipe r,"Count") when r.Layout.Kind!=RepetitionKind.Mirror=>r.Layout.Count,
                (ExtrusionPatternRecipe r,"Spacing") when r.Layout.Kind==RepetitionKind.Linear=>r.Layout.Step,
                (ExtrusionPatternRecipe r,"Angle") when r.Layout.Kind==RepetitionKind.Circular&&!r.Layout.FullCircle=>r.Layout.Step,
                (HoleRecipe r,"Diameter")=>r.Diameter,(HoleRecipe r,"Depth") when !r.Through=>r.Depth,
                (HoleRecipe {Thread:{Through:false} t},"ThreadDepth")=>t.DepthMm,
                (HoleRecipe r,"U")=>r.Position.X,(HoleRecipe r,"V")=>r.Position.Y,
                (HoleRecipe r,"HeadDiameter") when r.Head!=HoleHead.Plain=>r.HeadDiameter,
                (HoleRecipe r,"HeadDepth") when r.Head==HoleHead.Counterbore=>r.HeadDepth,
                (HoleRecipe r,"HeadAngle") when r.Head==HoleHead.Countersink=>r.HeadAngle,
                (ShellRecipe r,"Thickness")=>r.Thickness,
                (SolidOffsetRecipe r,"Distance")=>r.Distance,
                (PlanarPushPullRecipe r,"Distance")=>r.Distance,
                (ContourLocalRecipe r,var property)=>ContourParameterTargets.Read(r,property),
                (DraftRecipe r,var property) when r.Programs.Any(p=>p.AngleProperty==property)=>r.Programs.Single(p=>p.AngleProperty==property).Angle,
                (LocalFeatureRecipe r,"Size")=>r.Size,(LocalFeatureRecipe r,"SecondDistance") when r.SecondDistance.HasValue=>r.SecondDistance.Value,
                (LocalFeatureRecipe r,"EndRadius") when r.EndRadius.HasValue=>r.EndRadius.Value,
                (HistoryFilletRecipe r,"Radius")=>r.Radius,(HistoryFilletRecipe r,"EndRadius") when r.EndRadius.HasValue=>r.EndRadius.Value,
                (HistoryChamferRecipe r,"Distance")=>r.Distance,(HistoryChamferRecipe r,"SecondDistance") when r.SecondDistance.HasValue=>r.SecondDistance.Value,
                _=>throw new CadValidationException("Unsupported parameter feature field.")
            };
            if(target.Property is "Angle" or "HeadAngle")dimension=ParameterDimension.Angle;
            if(feature.Recipe is DraftRecipe)dimension=ParameterDimension.Angle;
            if(target.Property=="Count")dimension=ParameterDimension.Scalar;
        }
        else
        {
            var constraint=Constraint(doc,target);
            value=(constraint,target.Property) switch
            {
                (DistanceConstraint r,"Value")=>r.Distance,(OffsetXConstraint r,"Value")=>r.Offset,(OffsetYConstraint r,"Value")=>r.Offset,
                (LengthConstraint r,"Value")=>r.Length,(RadiusConstraint r,"Value")=>r.Radius,(AngleConstraint r,"Value")=>r.AngleRadians,
                (FixPointConstraint r,"X")=>r.Position.X,(FixPointConstraint r,"Y")=>r.Position.Y,
                (FiniteParametricContactConstraint r,"ParameterA")=>r.ParameterA,(FiniteParametricContactConstraint r,"ParameterB")=>r.ParameterB,
                (FiniteParametricContactConstraint {SlidingA:{} r},"SeedA")=>r.Seed,
                (FiniteParametricContactConstraint {SlidingA:{} r},"LowerA")=>r.Lower,
                (FiniteParametricContactConstraint {SlidingA:{} r},"UpperA")=>r.Upper,
                (FiniteParametricContactConstraint {SlidingB:{} r},"SeedB")=>r.Seed,
                (FiniteParametricContactConstraint {SlidingB:{} r},"LowerB")=>r.Lower,
                (FiniteParametricContactConstraint {SlidingB:{} r},"UpperB")=>r.Upper,
                _=>throw new CadValidationException("Unsupported parameter sketch dimension.")
            };
            if(constraint is AngleConstraint)dimension=ParameterDimension.Angle;
            if(constraint is FiniteParametricContactConstraint)dimension=ParameterDimension.Scalar;
        }
        CadGuard.Finite(value);return ParameterQuantity.Of(value,dimension);
    }
    public static IEnumerable<ParameterTarget> All(DocumentSnapshot doc)
    {
        foreach(var target in SketchCurveParameterTargets.All(doc))yield return target;
        foreach(var pattern in doc.AssemblyPatterns.Values.OrderBy(p=>p.Id))
        {
            if(pattern.Kind==RepetitionKind.Mirror)continue;
            yield return ParameterTarget.AssemblyPattern(pattern.Id,"Count");
            if(pattern.Kind==RepetitionKind.Linear)yield return ParameterTarget.AssemblyPattern(pattern.Id,"Spacing");
            else if(!pattern.FullCircle)yield return ParameterTarget.AssemblyPattern(pattern.Id,"Angle");
        }
        foreach(var plane in doc.Datums.Planes.Values.OrderBy(p=>p.Id))
            if(plane.Kind!=DatumPlaneKind.Midplane)yield return ParameterTarget.Datum(plane.Id,plane.Kind==DatumPlaneKind.Offset?"Offset":"Angle");
        foreach(var axis in doc.Datums.Axes.Values.OrderBy(a=>a.Id))foreach(var key in AxisFields(axis))yield return ParameterTarget.Datum(axis.Id,key);
        foreach(var point in doc.Datums.Points.Values.Where(p=>p.Kind is DatumPointKind.Independent or DatumPointKind.Offset).OrderBy(p=>p.Id))
            foreach(var key in new[]{"X","Y","Z"})yield return ParameterTarget.Datum(point.Id,key);
        foreach(var f in doc.Features.Values.Where(f=>!f.IsStale&&!f.IsSuppressed).OrderBy(f=>f.Id.Value))
        {
            string[] keys=f.Recipe switch
            {BoxRecipe=>["X","Y","Z"],CylinderRecipe=>["Radius","Height"],
                ExtrudeRecipe {Options:{Termination:not ExtrusionTermination.Distance}}=>[],
                ExtrudeRecipe {Options:{Side:ExtrusionSide.TwoSides}}=>["Distance","Backward"],ExtrudeRecipe=>["Distance"],RevolveRecipe=>["Angle"],
                LocalFeatureRecipe r=>new[]{"Size",r.SecondDistance is null?null:"SecondDistance",r.EndRadius is null?null:"EndRadius"}.OfType<string>().ToArray(),
                HistoryFilletRecipe r=>r.EndRadius is null?["Radius"]:["Radius","EndRadius"],
                HistoryChamferRecipe r=>r.SecondDistance is null?["Distance"]:["Distance","SecondDistance"],
                SolidOffsetRecipe or PlanarPushPullRecipe=>["Distance"],ContourLocalRecipe r=>ContourParameterTargets.Properties(r).ToArray(),ShellRecipe=>["Thickness"],DraftRecipe r=>r.Programs.Select(p=>p.AngleProperty).ToArray(),
                RepetitionRecipe r when r.Kind==RepetitionKind.Linear=>["Count","Spacing"],
                HolePatternRecipe {Layout:{Kind:RepetitionKind.Linear}}=>["Count","Spacing"],
                HolePatternRecipe {Layout:{Kind:RepetitionKind.Circular} r}=>r.FullCircle?["Count"]:["Count","Angle"],
                ExtrusionPatternRecipe {Layout:{Kind:RepetitionKind.Linear}}=>["Count","Spacing"],
                ExtrusionPatternRecipe {Layout:{Kind:RepetitionKind.Circular} r}=>r.FullCircle?["Count"]:["Count","Angle"],
                HoleRecipe r=>new[]{"Diameter","U","V",r.Through?null:"Depth",r.Head==HoleHead.Plain?null:"HeadDiameter",
                    r.Head==HoleHead.Counterbore?"HeadDepth":r.Head==HoleHead.Countersink?"HeadAngle":null,
                    r.Thread is {Through:false}?"ThreadDepth":null}.OfType<string>().ToArray(),
                RepetitionRecipe r when r.Kind==RepetitionKind.Circular=>r.FullCircle?["Count"]:["Count","Angle"],_=>[]};
            foreach(string key in keys)yield return ParameterTarget.Feature(f.Id,key);
        }
        foreach(var target in SketchRegionParameterTargets.All(doc))yield return target;
        foreach(var sketch in doc.Sketches.Values.OrderBy(s=>s.Id.Value))foreach(var c in sketch.Constraints.Where(c=>c.IsEnabled&&!IsSourceDriven(sketch,c.Id)))
        {
            if(c is FixPointConstraint){yield return ParameterTarget.Sketch(sketch.Id,c.Id,"X");yield return ParameterTarget.Sketch(sketch.Id,c.Id,"Y");}
            if(c is FiniteParametricContactConstraint contact)
                foreach(string field in (contact.SolvesA?new[]{"SeedA","LowerA","UpperA"}:new[]{"ParameterA"})
                    .Concat(contact.SolvesB?new[]{"SeedB","LowerB","UpperB"}:new[]{"ParameterB"}))
                    yield return ParameterTarget.Sketch(sketch.Id,c.Id,field);
            else if(c is DistanceConstraint or OffsetXConstraint or OffsetYConstraint or LengthConstraint or RadiusConstraint or AngleConstraint)
                yield return ParameterTarget.Sketch(sketch.Id,c.Id);
        }
    }
    public static DocumentSnapshot WriteAssemblyPattern(DocumentSnapshot doc,ParameterTarget target,double value)
    {
        _=Read(doc,target);if(target.Kind!=ParameterTargetKind.AssemblyPattern)throw new CadValidationException("Expected assembly array parameter.");
        var pattern=doc.AssemblyPatterns[target.Owner];
        if(target.Property=="Count"&&(value!=Math.Truncate(value)||value<2||value>256))throw new CadValidationException("Array count must be an integer between 2 and 256.");
        pattern=target.Property=="Count"?pattern.Resize((int)value):pattern with{Step=value};
        var owner=(AssemblyDefinition)doc.Definitions[pattern.Owner];var removed=doc.AssemblyPatterns[target.Owner].CopySlots.Except(pattern.CopySlots).ToArray();
        return doc with{AssemblyPatterns=doc.AssemblyPatterns.SetItem(pattern.Id,pattern),
            Definitions=doc.Definitions.SetItem(owner.Id,owner with{Children=owner.Children.Where(s=>!removed.Contains(s.Id)).ToImmutableArray()})};
    }
    private static string[] AxisFields(CadDatumAxis axis)=>axis.Kind==DatumAxisKind.TwoPoints?["Offset"]:["Offset","X","Y","Z"];
    private static double Component(Vector3d p,string key)=>key switch{"X"=>p.X,"Y"=>p.Y,"Z"=>p.Z,_=>throw new CadValidationException("Unknown coordinate field.")};
    private static Vector3d Component(Vector3d p,string key,double value)=>key switch{"X"=>p with{X=value},"Y"=>p with{Y=value},"Z"=>p with{Z=value},_=>throw new CadValidationException("Unknown coordinate field.")};
    public static DocumentSnapshot WriteDatum(DocumentSnapshot doc,ParameterTarget target,double value)
    {
        _=Read(doc,target);if(target.Kind!=ParameterTargetKind.Datum)throw new CadValidationException("Expected datum parameter.");
        if(doc.Datums.Planes.TryGetValue(target.Owner,out var plane))
        {plane=plane with{Value=value};plane.Validate();return doc with{Datums=doc.Datums with{Planes=doc.Datums.Planes.SetItem(plane.Id,plane)}};}
        if(doc.Datums.Axes.TryGetValue(target.Owner,out var axis))
        {axis=target.Property=="Offset"?axis with{Offset=value}:axis with{Origin=Component(axis.Origin,target.Property,value)};axis.Validate();return doc with{Datums=doc.Datums with{Axes=doc.Datums.Axes.SetItem(axis.Id,axis)}};}
        var point=doc.Datums.Points[target.Owner] with{Value=Component(doc.Datums.Points[target.Owner].Value,target.Property,value)};point.Validate();return doc with{Datums=doc.Datums with{Points=doc.Datums.Points.SetItem(point.Id,point)}};
    }
    public static GeometryRecipe WriteRecipe(DocumentSnapshot doc,ParameterTarget target,double value,bool validate=true)
    {
        _=Read(doc,target);
        var recipe=doc.Features[new(target.Owner)].Recipe;
        GeometryRecipe result=(recipe,target.Property) switch
        {
            (BoxRecipe r,"X")=>r with{X=value},(BoxRecipe r,"Y")=>r with{Y=value},(BoxRecipe r,"Z")=>r with{Z=value},
            (CylinderRecipe r,"Radius")=>r with{Radius=value},(CylinderRecipe r,"Height")=>r with{Height=value},
            (ExtrudeRecipe r,"Distance")=>r with{Distance=value},(RevolveRecipe r,"Angle")=>r with{AngleRadians=value},
            (ExtrudeRecipe {Options:{} options} r,"Backward")=>r with{Options=options with{Backward=value}},
            (RepetitionRecipe r,"Spacing" or "Angle")=>r with{Step=value},
            (RepetitionRecipe r,"Count") when value==Math.Truncate(value)&&value>=2&&value<=256=>r.Resize((int)value),
            (HolePatternRecipe r,"Spacing" or "Angle")=>r with{Layout=r.Layout with{Step=value}},
            (HolePatternRecipe r,"Count") when value==Math.Truncate(value)&&value>=2&&value<=256=>r with{Layout=r.Layout.Resize((int)value)},
            (ExtrusionPatternRecipe r,"Spacing" or "Angle")=>r with{Layout=r.Layout with{Step=value}},
            (ExtrusionPatternRecipe r,"Count") when value==Math.Truncate(value)&&value>=2&&value<=256=>r with{Layout=r.Layout.Resize((int)value)},
            (HoleRecipe r,"Diameter")=>r with{Diameter=value},(HoleRecipe r,"Depth")=>r with{Depth=value},
            (HoleRecipe {Thread:{Through:false} t} r,"ThreadDepth")=>r with{Thread=t with{DepthMm=value}},
            (HoleRecipe r,"U")=>r with{Position=r.Position with{X=value}},(HoleRecipe r,"V")=>r with{Position=r.Position with{Y=value}},
            (HoleRecipe r,"HeadDiameter")=>r with{HeadDiameter=value},(HoleRecipe r,"HeadDepth")=>r with{HeadDepth=value},
            (HoleRecipe r,"HeadAngle")=>r with{HeadAngle=value},
            (ShellRecipe r,"Thickness")=>r with{Thickness=value},
            (SolidOffsetRecipe r,"Distance")=>r with{Distance=value},
            (PlanarPushPullRecipe r,"Distance")=>r with{Distance=value},
            (ContourLocalRecipe r,var property)=>ContourParameterTargets.Write(r,property,value),
            (DraftRecipe r,var property) when r.Programs.Any(p=>p.AngleProperty==property)=>r with{Programs=r.Programs.Select(p=>p.AngleProperty==property?p with{Angle=value}:p).ToImmutableArray()},
            (LocalFeatureRecipe r,"Size")=>r with{Size=value},(LocalFeatureRecipe r,"SecondDistance")=>r with{SecondDistance=value},
            (LocalFeatureRecipe r,"EndRadius")=>r with{EndRadius=value},(HistoryFilletRecipe r,"Radius")=>r with{Radius=value},
            (HistoryFilletRecipe r,"EndRadius")=>r with{EndRadius=value},(HistoryChamferRecipe r,"Distance")=>r with{Distance=value},
            (HistoryChamferRecipe r,"SecondDistance")=>r with{SecondDistance=value},_=>throw new CadValidationException("Unsupported parameter field.")
        };
        if(validate)result.Validate();return result;
    }
    public static SketchConstraint WriteConstraint(DocumentSnapshot doc,ParameterTarget target,double value)
    {
        RequireWritable(doc,target);_=Read(doc,target);var constraint=Constraint(doc,target);
        SketchConstraint result=(constraint,target.Property) switch
        {
            (DistanceConstraint r,"Value")=>r with{Distance=value},(OffsetXConstraint r,"Value")=>r with{Offset=value},(OffsetYConstraint r,"Value")=>r with{Offset=value},
            (LengthConstraint r,"Value")=>r with{Length=value},(RadiusConstraint r,"Value")=>r with{Radius=value},(AngleConstraint r,"Value")=>r with{AngleRadians=value},
            (FixPointConstraint r,"X")=>r with{Position=r.Position with{X=value}},(FixPointConstraint r,"Y")=>r with{Position=r.Position with{Y=value}},
            (FiniteParametricContactConstraint r,"ParameterA")=>r with{ParameterA=value},(FiniteParametricContactConstraint r,"ParameterB")=>r with{ParameterB=value},
            (FiniteParametricContactConstraint {SlidingA:{} s} r,"SeedA")=>r with{SlidingA=s with{Seed=value}},
            (FiniteParametricContactConstraint {SlidingA:{} s} r,"LowerA")=>r with{SlidingA=s with{Lower=value}},
            (FiniteParametricContactConstraint {SlidingA:{} s} r,"UpperA")=>r with{SlidingA=s with{Upper=value}},
            (FiniteParametricContactConstraint {SlidingB:{} s} r,"SeedB")=>r with{SlidingB=s with{Seed=value}},
            (FiniteParametricContactConstraint {SlidingB:{} s} r,"LowerB")=>r with{SlidingB=s with{Lower=value}},
            (FiniteParametricContactConstraint {SlidingB:{} s} r,"UpperB")=>r with{SlidingB=s with{Upper=value}},
            _=>throw new CadValidationException("Unsupported parameter field.")
        };
        return result is FiniteParametricContactConstraint contact?
            contact with{ParameterA=contact.SlidingA?.Seed??contact.ParameterA,ParameterB=contact.SlidingB?.Seed??contact.ParameterB}:result;
    }
    public static void RequireWritable(DocumentSnapshot doc,ParameterTarget target)
    {
        if(target.Kind==ParameterTargetKind.SketchRegion)SketchRegionParameterTargets.RequireWritable(doc,target);
        if(target.Kind==ParameterTargetKind.SketchConstraint&&Constraint(doc,target) is FiniteParametricContactConstraint c&&
           (target.Property=="ParameterA"&&c.SolvesA||target.Property=="ParameterB"&&c.SolvesB))
        {
            var error=new CadValidationException("An automatically solved contact parameter is read-only; bind its explicit seed or interval instead.");
            error.Data["SketchContact.Message"]="SlidingContactDriverConflict";throw error;
        }
    }
    private static SketchConstraint Constraint(DocumentSnapshot doc,ParameterTarget target)
    {
        if(target.Kind!=ParameterTargetKind.SketchConstraint||target.Constraint is not {} id||!doc.Sketches.TryGetValue(new(target.Owner),out var sketch))
            throw new CadValidationException("Parameter sketch identity is missing.");
        if(IsSourceDriven(sketch,new(id)))throw new CadValidationException("This anchor follows a projection or an exact numeric curve; select an authored dimension.");
        return sketch.Constraints.FirstOrDefault(c=>c.Id.Value==id&&c.IsEnabled)??throw new CadValidationException("Parameter constraint is missing or disabled.");
    }
    private static bool IsSourceDriven(CadSketch sketch,SketchConstraintId id)=>
        sketch.Projections.Any(p=>p.Anchors.Contains(id))||sketch.NativeProjections.Any(p=>p.Anchors.Contains(id))||
        SketchFixedCurveEndpoints.All(sketch).Any(e=>e.Ends.StartAnchor==id||e.Ends.EndAnchor==id);
}

public static class ParameterGraph
{
    public static ImmutableDictionary<Guid,ParameterQuantity> Evaluate(DocumentSnapshot doc,CancellationToken token=default)
    {
        var book=doc.Parameters;
        if(book is null||book.Parameters.IsDefault||book.Bindings.IsDefault||book.Parameters.Length>256||book.Bindings.Length>1024)
            throw new CadValidationException("Uninitialized or oversized parameter table.");
        if(book.Parameters.IsEmpty&&book.Bindings.IsEmpty)return ImmutableDictionary<Guid,ParameterQuantity>.Empty;
        var byName=new Dictionary<string,CadParameter>(StringComparer.OrdinalIgnoreCase);var byId=new Dictionary<Guid,CadParameter>();
        foreach(var p in book.Parameters)
        {
            if(p is null||p.Id==Guid.Empty||!ParameterExpressions.IsName(p.Name)||!Enum.IsDefined(p.Dimension)||!byName.TryAdd(p.Name,p)||!byId.TryAdd(p.Id,p))
                throw new CadValidationException("Invalid or duplicate parameter name/identity.");
            if(p.Reference is not null&&p.Expression!="")throw new CadValidationException($"Reference parameter {p.Name} cannot have an expression.");
        }
        var drivers=new Dictionary<ParameterTarget,CadParameter>();var bindingIds=new HashSet<Guid>();
        foreach(var b in book.Bindings)
        {
            if(b is null||b.Target is null||b.Id==Guid.Empty||!bindingIds.Add(b.Id)||!byId.TryGetValue(b.Parameter,out var p)||p.Reference is not null||!drivers.TryAdd(b.Target,p))
                throw new CadValidationException("Invalid/duplicate driving binding or reference parameter used as a driver.");
            ParameterTargets.RequireWritable(doc,b.Target);
            if(!ParameterTargets.Read(doc,b.Target).HasDimension(p.Dimension))throw new CadValidationException($"Binding dimension mismatch: {p.Name}.");
            if(b.Target.Kind==ParameterTargetKind.SketchConstraint&&doc.Sketches[new(b.Target.Owner)].Projections.Any(projection=>projection.Anchors.Any(id=>id.Value==b.Target.Constraint)))
                throw new CadValidationException("Projected dimensions cannot be driven by parameters; detach the projection first.");
        }
        var values=ImmutableDictionary.CreateBuilder<Guid,ParameterQuantity>();var path=new List<CadParameter>();
        ParameterQuantity Resolve(CadParameter p)
        {
            token.ThrowIfCancellationRequested();if(values.TryGetValue(p.Id,out var existing))return existing;
            int cycle=path.FindIndex(item=>item.Id==p.Id);
            if(cycle>=0)throw new CadValidationException("Parameter cycle: "+string.Join(" -> ",path.Skip(cycle).Select(item=>item.Name).Append(p.Name)));
            if(path.Count>=64)throw new CadValidationException("Parameter dependency depth exceeds 64.");
            path.Add(p);
            try
            {
                ParameterQuantity value;
                if(p.Reference is {} target)
                {
                    var actual=ParameterTargets.Read(doc,target);
                    value=drivers.TryGetValue(target,out var driver)?Resolve(driver):actual;
                }
                else
                {
                    // Resolve graph nodes before entering the parser so graph depth and expression nesting do not multiply on the call stack.
                    foreach(string name in ParameterExpressions.Dependencies(p.Expression))
                        Resolve(byName.GetValueOrDefault(name)??throw new CadValidationException($"Parameter {p.Name}: unknown dependency {name}."));
                    value=ParameterExpressions.Evaluate(p.Expression,name=>values[byName[name].Id]);
                }
                if(!value.HasDimension(p.Dimension)||!double.IsFinite(value.Value)||Math.Abs(value.Value)>1e12)throw new CadValidationException($"Parameter dimension or value mismatch: {p.Name}.");
                values.Add(p.Id,value);return value;
            }
            finally{path.RemoveAt(path.Count-1);}
        }
        foreach(var p in book.Parameters.OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase))Resolve(p);
        return values.ToImmutable();
    }
    public static void ValidateApplied(DocumentSnapshot doc)
    {
        var values=Evaluate(doc);
        foreach(var binding in doc.Parameters.Bindings)
        {
            double expected=values[binding.Parameter].Value,actual=ParameterTargets.Read(doc,binding.Target).Value;
            if(Math.Abs(actual-expected)>1e-10*Math.Max(1,Math.Abs(expected)))throw new CadValidationException("A driven dimension differs from its parameter; edit the parameter or detach the binding.");
        }
    }
}
