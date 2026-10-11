using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Authored numeric definitions with persistent curve identity and explicit table indices.</summary>
public static class SketchCurveParameterTargets
{
    private static readonly string[] EllipseFields=["CenterX","CenterY","CosineX","CosineY","SineX","SineY","StartAngle","SweepAngle"];
    public static bool SourceDriven(CadSketch sketch,SketchEntityId curve)=>
        sketch.Projections.Any(p=>p.TargetEntity==curve)||sketch.NativeProjections.Any(p=>p.TargetEntity==curve)||
        sketch.RegionOffsets.Any(g=>g.Loops.Any(l=>l.Outputs.Any(o=>o.Curve==curve)));

    public static IEnumerable<ParameterTarget> All(DocumentSnapshot document)
    {
        foreach(var sketch in document.Sketches.Values.OrderBy(s=>s.Id.Value))
        {
            foreach(var target in SketchExpressionParameters.All(sketch))yield return target;
            foreach(var ellipse in sketch.Ellipses.Where(e=>!SourceDriven(sketch,e.Id)).OrderBy(e=>e.Id.Value))
                foreach(string field in EllipseFields)yield return ParameterTarget.SketchCurve(sketch.Id,ellipse.Id,field);
            foreach(var curve in sketch.Freeforms.Where(e=>!SourceDriven(sketch,e.Id)).OrderBy(e=>e.Id.Value))
            {
                foreach(string field in new[]{"FirstParameter","LastParameter"})yield return ParameterTarget.SketchCurve(sketch.Id,curve.Id,field);
                for(int i=0;i<curve.Geometry.Poles.Length;i++)foreach(string field in new[]{"PoleX","PoleY"})
                    yield return ParameterTarget.SketchCurve(sketch.Id,curve.Id,field,i);
                for(int i=0;i<curve.Geometry.Weights.Length;i++)yield return ParameterTarget.SketchCurve(sketch.Id,curve.Id,"Weight",i);
                for(int i=0;i<curve.Geometry.Knots.Length;i++)yield return ParameterTarget.SketchCurve(sketch.Id,curve.Id,"Knot",i);
            }
        }
    }
    private static (CadSketch Sketch,SketchEntityId Id) Resolve(DocumentSnapshot document,ParameterTarget target)
    {
        if(target.Kind!=ParameterTargetKind.ExactSketchCurve||target.Constraint is not null||target.Curve is not {} curve||curve==Guid.Empty||
           !document.Sketches.TryGetValue(new(target.Owner),out var sketch))
            throw new CadValidationException("Exact curve parameter identity is missing or malformed.");
        var id=new SketchEntityId(curve);
        if(SourceDriven(sketch,id))throw new CadValidationException("Detach the projection before driving its exact curve definition.");
        return(sketch,id);
    }
    public static ParameterQuantity Read(DocumentSnapshot document,ParameterTarget target)
    {
        var(sketch,id)=Resolve(document,target);double value;var dimension=ParameterDimension.Length;
        if(sketch.ExpressionCurves.SingleOrDefault(e=>e.Id==id) is {} expression)return SketchExpressionParameters.Read(expression,target);
        if(sketch.Ellipses.SingleOrDefault(e=>e.Id==id) is {} ellipse)
        {
            if(target.Index is not null)throw new CadValidationException("An ellipse field cannot have a table index.");
            var g=ellipse.Geometry;
            value=target.Property switch
            {
                "CenterX"=>g.Center.X,"CenterY"=>g.Center.Y,"CosineX"=>g.Cosine.X,"CosineY"=>g.Cosine.Y,
                "SineX"=>g.Sine.X,"SineY"=>g.Sine.Y,"StartAngle"=>g.StartAngle,"SweepAngle"=>g.SweepAngle,
                _=>throw new CadValidationException("Unsupported ellipse parameter field.")
            };
            if(target.Property is "StartAngle" or "SweepAngle")dimension=ParameterDimension.Angle;
        }
        else if(sketch.Freeforms.SingleOrDefault(e=>e.Id==id) is {} freeform)
        {
            var g=freeform.Geometry;int TableIndex(int count)=>target.Index is {} index&&index>=0&&index<count?index:
                throw new CadValidationException("Exact curve parameter table index is missing or outside its authored table.");
            value=target.Property switch
            {
                "PoleX"=>g.Poles[TableIndex(g.Poles.Length)].X,"PoleY"=>g.Poles[TableIndex(g.Poles.Length)].Y,
                "Weight"=>g.Weights[TableIndex(g.Weights.Length)],"Knot"=>g.Knots[TableIndex(g.Knots.Length)],
                "FirstParameter" when target.Index is null=>g.FirstParameter,"LastParameter" when target.Index is null=>g.LastParameter,
                _=>throw new CadValidationException("Unsupported freeform parameter field or unexpected table index.")
            };
            if(target.Property is not ("PoleX" or "PoleY"))dimension=ParameterDimension.Scalar;
        }
        else throw new CadValidationException("The explicitly identified exact curve is missing.");
        CadGuard.Finite(value);return ParameterQuantity.Of(value,dimension);
    }

    /// <summary>Stages one field. The whole curve is validated only after all evaluated drivers have been staged.</summary>
    public static DocumentSnapshot Stage(DocumentSnapshot document,ParameterTarget target,double value)
    {
        _=Read(document,target);CadGuard.Finite(value);var(sketch,id)=Resolve(document,target);
        if(sketch.ExpressionCurves.SingleOrDefault(e=>e.Id==id) is {} expression)
        {
            sketch=SketchExpressionParameters.Stage(sketch,expression,target,value);
            return document with{Sketches=document.Sketches.SetItem(sketch.Id,sketch)};
        }
        if(sketch.Ellipses.SingleOrDefault(e=>e.Id==id) is {} ellipse)
        {
            var g=ellipse.Geometry;g=target.Property switch
            {
                "CenterX"=>g with{Center=g.Center with{X=value}},"CenterY"=>g with{Center=g.Center with{Y=value}},
                "CosineX"=>g with{Cosine=g.Cosine with{X=value}},"CosineY"=>g with{Cosine=g.Cosine with{Y=value}},
                "SineX"=>g with{Sine=g.Sine with{X=value}},"SineY"=>g with{Sine=g.Sine with{Y=value}},
                "StartAngle"=>g with{StartAngle=value},"SweepAngle"=>g with{SweepAngle=value},_=>throw new CadValidationException("Unsupported ellipse parameter field.")
            };
            sketch=sketch with{Ellipses=sketch.Ellipses.Replace(ellipse,ellipse with{Geometry=g})};
        }
        else
        {
            var freeform=sketch.Freeforms.Single(e=>e.Id==id);var g=freeform.Geometry;int index=target.Index??0;
            g=target.Property switch
            {
                "PoleX"=>g with{Poles=g.Poles.SetItem(index,g.Poles[index] with{X=value})},
                "PoleY"=>g with{Poles=g.Poles.SetItem(index,g.Poles[index] with{Y=value})},
                "Weight"=>g with{Weights=g.Weights.SetItem(index,value)},"Knot"=>g with{Knots=g.Knots.SetItem(index,value)},
                "FirstParameter"=>g with{FirstParameter=value},"LastParameter"=>g with{LastParameter=value},
                _=>throw new CadValidationException("Unsupported freeform parameter field.")
            };
            sketch=sketch with{Freeforms=sketch.Freeforms.Replace(freeform,freeform with{Geometry=g})};
        }
        return document with{Sketches=document.Sketches.SetItem(sketch.Id,sketch)};
    }
    public static CadSketch PrepareNumericSources(CadSketch sketch)
    {
        foreach(var ellipse in sketch.Ellipses)ellipse.Geometry.Validate();
        foreach(var curve in sketch.Freeforms)curve.Geometry.Validate();
        foreach(var id in SketchFixedCurveEndpoints.All(sketch).Select(e=>e.Curve).ToArray())
            sketch=SketchFixedCurveEndpoints.Synchronize(sketch,id);
        return sketch;
    }
    public static CadSketch ValidateAndSynchronize(CadSketch sketch)
    {
        sketch=SketchExpressionEntities.Refresh(PrepareNumericSources(sketch));sketch.Validate();return sketch;
    }
    public static void GuardStructure(DocumentSnapshot before,DocumentSnapshot after)
    {
        var retained=after.Parameters.Bindings.Select(b=>b.Target).Concat(after.Parameters.Parameters.Select(p=>p.Reference).OfType<ParameterTarget>()).ToHashSet();
        var previous=before.Parameters.Bindings.Select(b=>b.Target).Concat(before.Parameters.Parameters.Select(p=>p.Reference).OfType<ParameterTarget>());
        foreach(var target in previous.Where(t=>t.Kind==ParameterTargetKind.ExactSketchCurve&&retained.Contains(t)).Distinct())
        {
            var(a,id)=Resolve(before,target);var(b,_)=Resolve(after,target);
            if(a.ExpressionCurves.SingleOrDefault(e=>e.Id==id) is {} expression)
            {
                SketchExpressionParameters.GuardStructure(expression,b.ExpressionCurves.SingleOrDefault(e=>e.Id==id)??
                    throw new CadValidationException("Detach expression distance targets before removing the curve."),target);continue;
            }
            var x=a.Freeforms.SingleOrDefault(e=>e.Id==id)?.Geometry;var y=b.Freeforms.SingleOrDefault(e=>e.Id==id)?.Geometry;
            if(x is null?y is not null||b.Ellipses.All(e=>e.Id!=id):y is null||x.Kind!=y.Kind||x.Degree!=y.Degree||x.Periodic!=y.Periodic||x.Reversed!=y.Reversed||
               x.Poles.Length!=y.Poles.Length||x.Weights.Length!=y.Weights.Length||x.Knots.Length!=y.Knots.Length||!x.Multiplicities.SequenceEqual(y.Multiplicities))
                throw new CadValidationException("Detach exact curve parameter targets before changing the curve family, table structure or direction.");
        }
    }
}
