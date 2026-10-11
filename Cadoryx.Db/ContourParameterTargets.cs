using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Stable program/sample/pole identities; native chain evidence is never a driving field.</summary>
public static class ContourParameterTargets
{
    public static string Property(Guid program,string field,Guid? row=null)=>
        $"Contour:{program:N}:{field}"+(row is {} id?$":{id:N}":"");

    public static IEnumerable<string> Properties(ContourLocalRecipe recipe)
    {
        foreach(var p in recipe.Programs)
        {
            if(recipe.Operation==LocalFeatureOperation.Chamfer)
            {
                yield return Property(p.Id,"Distance");
                if(p.SecondDistance is not null)yield return Property(p.Id,"SecondDistance");
                continue;
            }
            switch(p.Radius)
            {
                case ConstantContourRadius:yield return Property(p.Id,"Radius");break;
                case LinearContourRadius:
                    yield return Property(p.Id,"First");yield return Property(p.Id,"Last");break;
                case SmoothContourRadius:
                    foreach(string field in new[]{"First","Last","FirstDerivative","LastDerivative"})yield return Property(p.Id,field);
                    break;
                case SampledContourRadius law:
                    foreach(var row in law.Samples)yield return Property(p.Id,"SampleRadius",row.Id);
                    break;
                case SplineContourRadius law:
                    foreach(var row in law.Poles)yield return Property(p.Id,"PoleRadius",row.Id);
                    break;
            }
        }
    }
    private static (ContourLocalProgram Program,string Field,Guid? Row) Resolve(ContourLocalRecipe recipe,string property)
    {
        if(property is null||!Properties(recipe).Contains(property,StringComparer.Ordinal))
            throw new CadValidationException("The contour program or authored radius field is missing. Inspect parameters again.");
        var parts=property.Split(':');var program=recipe.Programs.Single(p=>p.Id==Guid.ParseExact(parts[1],"N"));
        return(program,parts[2],parts.Length==4?Guid.ParseExact(parts[3],"N"):null);
    }
    public static double Read(ContourLocalRecipe recipe,string property)
    {
        var(p,field,row)=Resolve(recipe,property);
        return (p.Radius,field) switch
        {
            (ConstantContourRadius r,"Radius")=>r.Value,
            (LinearContourRadius r,"First")=>r.First,(LinearContourRadius r,"Last")=>r.Last,
            (SmoothContourRadius r,"First")=>r.First,(SmoothContourRadius r,"Last")=>r.Last,
            (SmoothContourRadius r,"FirstDerivative")=>r.FirstDerivative,(SmoothContourRadius r,"LastDerivative")=>r.LastDerivative,
            (SampledContourRadius r,"SampleRadius")=>r.Samples.Single(s=>s.Id==row).Radius,
            (SplineContourRadius r,"PoleRadius")=>r.Poles.Single(s=>s.Id==row).Radius,
            (_,"Distance")=>p.Distance!.Value,(_,"SecondDistance")=>p.SecondDistance!.Value,
            _=>throw new CadValidationException("Unsupported contour field.")
        };
    }
    public static ContourLocalRecipe Write(ContourLocalRecipe recipe,string property,double value)
    {
        CadGuard.Finite(value);var(p,field,row)=Resolve(recipe,property);
        ContourRadiusLaw? law=(p.Radius,field) switch
        {
            (ConstantContourRadius r,"Radius")=>r with{Value=value},
            (LinearContourRadius r,"First")=>r with{First=value},(LinearContourRadius r,"Last")=>r with{Last=value},
            (SmoothContourRadius r,"First")=>r with{First=value},(SmoothContourRadius r,"Last")=>r with{Last=value},
            (SmoothContourRadius r,"FirstDerivative")=>r with{FirstDerivative=value},(SmoothContourRadius r,"LastDerivative")=>r with{LastDerivative=value},
            (SampledContourRadius r,"SampleRadius")=>r with{Samples=r.Samples.Select(s=>s.Id==row?s with{Radius=value}:s).ToImmutableArray()},
            (SplineContourRadius r,"PoleRadius")=>r with{Poles=r.Poles.Select(s=>s.Id==row?s with{Radius=value}:s).ToImmutableArray()},
            (_,"Distance" or "SecondDistance")=>p.Radius,
            _=>throw new CadValidationException("Unsupported contour field.")
        };
        var updated=p with{Radius=law,Distance=field=="Distance"?value:p.Distance,SecondDistance=field=="SecondDistance"?value:p.SecondDistance};
        return recipe with{Programs=recipe.Programs.Replace(p,updated)};
    }
    public static void GuardStructure(DocumentSnapshot before,DocumentSnapshot after)
    {
        var retained=after.Parameters.Bindings.Select(b=>b.Target).Concat(after.Parameters.Parameters.Select(p=>p.Reference).OfType<ParameterTarget>()).ToHashSet();
        var previous=before.Parameters.Bindings.Select(b=>b.Target).Concat(before.Parameters.Parameters.Select(p=>p.Reference).OfType<ParameterTarget>());
        foreach(var target in previous.Where(t=>t.Kind==ParameterTargetKind.Feature&&retained.Contains(t)).Distinct())
        {
            if(!before.Features.TryGetValue(new(target.Owner),out var feature)||feature.Recipe is not ContourLocalRecipe old)continue;
            var(p,_,_)=Resolve(old,target.Property);
            if(!after.Features.TryGetValue(feature.Id,out var current)||current.Recipe is not ContourLocalRecipe recipe)
                throw new CadValidationException("Detach contour parameter targets before deleting the feature.");
            var(next,_,_)=Resolve(recipe,target.Property);
            if(recipe.Operation!=old.Operation||next.Id!=p.Id||next.Radius?.GetType()!=p.Radius?.GetType())
                throw new CadValidationException("Detach contour parameter targets before changing the radius law family.");
        }
    }
}
