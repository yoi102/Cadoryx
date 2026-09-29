using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Rendering.Occt;

public sealed partial class OcctViewport
{
    private readonly Dictionary<Guid,(EngineeringDimension Data,ViewerDimension Native)> dimensions=[];
    private (LengthUnit Unit,int Decimals)? dimensionFormat;
    public int DimensionCount=>dimensions.Count;
    private void SetDimensions(CadScene scene)
    {
        var format=(scene.DimensionUnit,scene.DimensionDecimalPlaces);
        var staged=new Dictionary<Guid,(EngineeringDimension Data,ViewerDimension Native)>();
        var created=new List<ViewerDimension>();
        try
        {
            foreach(var d in scene.Dimensions)
            {
                d.Validate();
                if(dimensions.TryGetValue(d.Id,out var previous)&&previous.Data==d&&dimensionFormat==format){staged.Add(d.Id,previous);continue;}
                var unit=scene.DimensionUnit switch{LengthUnit.Centimeter=>"cm",LengthUnit.Meter=>"m",LengthUnit.Inch=>"in",_=>"mm"};
                var style=new ViewerDimensionStyle{Flyout=d.FlyoutMm,Units=new(displayLengthUnit:unit,decimalPlaces:scene.DimensionDecimalPlaces),
                    Color=new(((d.Argb>>16)&255)/255d,((d.Argb>>8)&255)/255d,(d.Argb&255)/255d)};
                static GpPoint P(Vector3d p)=>new(p.X,p.Y,p.Z);
                var native=d.Kind==EngineeringDimensionKind.Length?
                    viewer.DisplayLengthDimension(P(d.First),P(d.Second),new(d.Normal.X,d.Normal.Y,d.Normal.Z,-d.Normal.Dot(d.First)),style):
                    viewer.DisplayAngleDimension(P(d.First),P(d.Second),P(d.Third),style);
                created.Add(native);if(!d.IsVisible)native.Hide();staged.Add(d.Id,(d,native));
            }
        }
        catch{foreach(var n in created)n.Dispose();throw;}
        foreach(var (id,d) in dimensions)if(!staged.TryGetValue(id,out var next)||!ReferenceEquals(d.Native,next.Native))d.Native.Dispose();
        dimensions.Clear();foreach(var pair in staged)dimensions.Add(pair.Key,pair.Value);dimensionFormat=format;
    }
    private void ClearDimensions(){foreach(var d in dimensions.Values)d.Native.Dispose();dimensions.Clear();dimensionFormat=null;}
}
