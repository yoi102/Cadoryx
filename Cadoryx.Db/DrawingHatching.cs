using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>A single straight-line family, with spacing measured on paper.</summary>
public sealed record DrawingHatchStyle(double AngleDegrees=45,double SpacingMm=2.5,bool Enabled=true)
{
    public void Validate()
    {
        if(!double.IsFinite(AngleDegrees)||AngleDegrees is <0 or >=180||
           !double.IsFinite(SpacingMm)||SpacingMm is <.1 or >100)
            throw new CadValidationException("Hatch angle must be 0 to less than 180 degrees, and paper spacing 0.1 to 100 mm.");
    }
}

/// <summary>Material identity is explicit; material names and geometry never select a rule.</summary>
public sealed record DrawingMaterialHatch(MaterialId Material,DrawingHatchStyle Style);

public sealed record DrawingSectionHatching(DrawingHatchStyle Default,bool AlternateSources=true)
{
    public ImmutableArray<DrawingMaterialHatch> Materials {get;init;}=[];
    public static DrawingSectionHatching Automatic {get;}=new(new DrawingHatchStyle());
    public void Validate()
    {
        if(Default is null||Materials.IsDefault||Materials.Length>256)
            throw new CadValidationException("Invalid section hatch settings.");
        Default.Validate();var ids=new HashSet<MaterialId>();
        foreach(var rule in Materials)
        {
            if(rule is null||rule.Style is null||!ids.Add(rule.Material))
                throw new CadValidationException("Duplicate or missing material hatch rule.");
            CadGuard.Id(rule.Material);rule.Style.Validate();
        }
    }
    public DrawingHatchStyle ForSource(MaterialId? material,int sourceIndex)
    {
        var rule=Materials.FirstOrDefault(r=>r.Material==material);
        if(rule is not null)return rule.Style;
        return AlternateSources&&sourceIndex%2!=0?
            Default with{AngleDegrees=(180-Default.AngleDegrees)%180}:Default;
    }
}
