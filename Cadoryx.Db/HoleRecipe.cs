namespace Cadoryx.Db;

public enum HoleHead { Plain=0, Counterbore=1, Countersink=2 }
public enum ClearanceFit { Close=0, Normal=1, Loose=2 }
public sealed record MetricClearanceSpec(int NominalMm,ClearanceFit Fit);
/// <summary>Explicit, versioned common metric clearance catalog. Does not claim all standard sizes or threaded geometry.</summary>
public static class MetricClearanceHoles
{
    public const string Catalog="metric-clearance-common-v1";
    private static readonly Dictionary<int,double[]> Sizes=new()
    {[3]=[3.2,3.4,3.6],[4]=[4.3,4.5,4.8],[5]=[5.3,5.5,5.8],[6]=[6.4,6.6,7],[8]=[8.4,9,10],[10]=[10.5,11,12],[12]=[13,13.5,14.5],[16]=[17,17.5,18.5]};
    public static IReadOnlyList<int> Nominals=>Sizes.Keys.Order().ToArray();
    public static double Diameter(MetricClearanceSpec spec)=>Enum.IsDefined(spec.Fit)&&Sizes.TryGetValue(spec.NominalMm,out var sizes)
        ?sizes[(int)spec.Fit]:throw new CadValidationException("Unsupported common metric clearance designation.");
}
/// <summary>Drills along negative frame Z from the authored entry plane. U/V locate the hole on that plane.</summary>
public sealed record HoleRecipe(GeometryAssetRef Source,RigidTransform3d Frame,Point2d Position,double Diameter,
    bool Through=true,double Depth=0,HoleHead Head=HoleHead.Plain,double HeadDiameter=0,double HeadDepth=0,
    double HeadAngle=0,MetricClearanceSpec? Standard=null,Guid? Datum=null,Guid? DatumRevision=null,
    MetricThreadSpec? Thread=null):GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public double RecessDepth=>Head==HoleHead.Countersink?(HeadDiameter-Diameter)/(2*Math.Tan(HeadAngle/2)):HeadDepth;
    public override void Validate()
    {
        Source.Validate();Frame.Validate();CadGuard.Finite(Position.X,Position.Y,Diameter,Depth,HeadDiameter,HeadDepth,HeadAngle);
        if(!Enum.IsDefined(Head)||Diameter<=1e-6||Diameter>10000||Math.Abs(Position.X)>1_000_000||Math.Abs(Position.Y)>1_000_000||
            (Datum is null)!=(DatumRevision is null)||Datum==Guid.Empty||DatumRevision==Guid.Empty||
            (Through?Depth!=0:Depth<=1e-6||Depth>1_000_000))
            throw new CadValidationException("Invalid hole dimensions, location or identity.");
        if(Head==HoleHead.Plain&&(HeadDiameter!=0||HeadDepth!=0||HeadAngle!=0)||
            Head!=HoleHead.Plain&&(HeadDiameter<=Diameter+1e-6||HeadDiameter>10000)||
            Head==HoleHead.Counterbore&&(HeadDepth<=1e-6||HeadDepth>1_000_000||HeadAngle!=0)||
            Head==HoleHead.Countersink&&(HeadDepth!=0||HeadAngle<Math.PI/3||HeadAngle>5*Math.PI/6)||
            !Through&&Head!=HoleHead.Plain&&RecessDepth>=Depth-1e-6)
            throw new CadValidationException("Invalid counterbore/countersink dimensions or blind-hole remaining depth.");
        if(Standard is {} spec&&Math.Abs(Diameter-MetricClearanceHoles.Diameter(spec))>1e-10)
            throw new CadValidationException("Clearance diameter differs from its catalog. Detach the standard to edit a custom diameter.");
        Thread?.ValidateForHole(Diameter,Through,Depth,Head,Standard);
    }
    public HoleRecipe Refresh(DocumentSnapshot doc,DefinitionId owner)
    {
        if(Datum is not {} id)return this;
        if(!doc.Datums.Planes.TryGetValue(id,out var plane)||plane.PartId!=owner)
            throw new CadValidationException("Hole datum is missing or belongs to another part. Reselect or detach explicitly.");
        if(plane.IsStale)throw new DatumSourceUnavailableException();
        return this with{Frame=plane.Frame,DatumRevision=plane.Revision};
    }
    public void ValidateApplied(DocumentSnapshot doc,FeatureDefinition feature)
    {
        if(feature.Inputs.Length!=1||!doc.Features.TryGetValue(feature.Inputs[0],out var source)||source.PartId!=feature.PartId||
            !feature.IsStale&&(source.Result!=Source||source.IsStale||source.IsSuppressed))throw new CadValidationException("Hole source feature identity is not current.");
        var current=feature.IsStale?this:Refresh(doc,feature.PartId);
        if(!feature.IsStale&&(current.DatumRevision!=DatumRevision||!DatumPlanes.SameFrame(current.Frame,Frame)))throw new CadValidationException("Hole datum cache is stale.");
    }
}
