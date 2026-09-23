namespace Cadoryx.Db;

public enum LocalFeatureOperation { Fillet=0, Chamfer=1 }
/// <summary>One semantic box edge. The source recipe is a validated cache of the upstream feature.</summary>
public sealed record LocalFeatureRecipe(GeometryAssetRef Source,BoxRecipe Box,BoxBoundary First,BoxBoundary Second,
    LocalFeatureOperation Operation,double Size,double? SecondDistance=null,int AdditionalEdges=0,double? EndRadius=null) : GeometryRecipe
{
    public static int EdgeBit(BoxBoundary first,BoxBoundary second)
    {
        if(!Enum.IsDefined(first)||!Enum.IsDefined(second)||(int)first/2>=(int)second/2)
            throw new CadValidationException("An edge requires two ordered boundaries on distinct axes.");
        var a=(int)first/2;var b=(int)second/2;
        var group=(a,b) switch{(0,1)=>0,(0,2)=>1,(1,2)=>2,_=>throw new CadValidationException("Invalid box edge.")};
        return 1<<(group*4+((int)first%2)*2+(int)second%2);
    }
    public IEnumerable<(BoxBoundary First,BoxBoundary Second)> Edges()
    {
        yield return(First,Second);
        foreach(var first in Enum.GetValues<BoxBoundary>())
            foreach(var second in Enum.GetValues<BoxBoundary>())
                if((int)first/2<(int)second/2&&(AdditionalEdges&EdgeBit(first,second))!=0)
                    yield return(first,second);
    }
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        Source.Validate();Box.Validate();CadGuard.Positive(Size);
        if(SecondDistance is {} second)
        {
            if(Operation!=LocalFeatureOperation.Chamfer)throw new CadValidationException("A second distance is only valid for chamfer.");
            CadGuard.Positive(second);
        }
        if(AdditionalEdges<0||AdditionalEdges>0xfff)throw new CadValidationException("Invalid additional box edges.");
        if(EndRadius is {} end)
        {
            if(Operation!=LocalFeatureOperation.Fillet||AdditionalEdges!=0)
                throw new CadValidationException("A variable radius requires one fillet edge.");
            CadGuard.Positive(end);
        }
        if(!Enum.IsDefined(Operation)||!Enum.IsDefined(First)||!Enum.IsDefined(Second)||(int)First/2>=(int)Second/2)
            throw new CadValidationException("Invalid local edge operation.");
        if((AdditionalEdges&EdgeBit(First,Second))!=0)throw new CadValidationException("Primary edge is duplicated.");
    }
}
