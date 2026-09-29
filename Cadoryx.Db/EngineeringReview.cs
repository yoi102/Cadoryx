using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum SectionOutput { Curves, Faces }
public sealed record ReviewCamera(Vector3d Eye,Vector3d Target,Vector3d Up,double Aspect,double Scale,
    double FieldOfViewY,double NearPlane,double FarPlane,bool Perspective,bool AutoFitDepth)
{
    public void Validate()
    {
        Eye.Validate();Target.Validate();Up.Validate();
        if((Eye-Target).Length<1e-9||Math.Abs(Up.Length-1)>1e-7||(Eye-Target).Cross(Up).Length<1e-9||
            !double.IsFinite(Aspect)||Aspect<=0||!double.IsFinite(Scale)||Scale<=0||
            !double.IsFinite(FieldOfViewY)||FieldOfViewY is <=0 or >=180||!double.IsFinite(NearPlane)||
            !double.IsFinite(FarPlane)||FarPlane<=NearPlane)throw new CadValidationException("Invalid saved camera.");
    }
}
public sealed record ReviewInstance(OccurrencePath Path,BodyId BodyId);
public sealed record ReviewBookmark(Guid Id,string Name,ReviewCamera Primary,ReviewCamera? Secondary,bool SplitView,
    bool SectionEnabled,int SectionAxis,double SectionOffsetMm,bool SectionReverse,double? SlabThicknessMm,
    ImmutableArray<ReviewInstance> Hidden,ImmutableArray<ReviewInstance>? Isolated);
public sealed record SectionSource(OccurrencePath Path,BodyId BodyId,FeatureId? FeatureId,
    GeometryRevisionId Revision,AssetId Asset,RigidTransform3d Transform);
/// <summary>Whole-body association, never a guessed subshape reference. Output coordinates are world coordinates.</summary>
public sealed record AssociatedSection(FeatureId FeatureId,Vector3d Normal,double OffsetMm,SectionOutput Output,
    ImmutableArray<SectionSource> Sources,string? StaleReason=null);
public enum EngineeringDimensionKind { Length, Angle }
/// <summary>Explicit frozen world points. A dimension does not claim a live topology association.</summary>
public sealed record EngineeringDimension(Guid Id,string Name,EngineeringDimensionKind Kind,
    Vector3d First,Vector3d Second,Vector3d Third,Vector3d Normal,double FlyoutMm=10,
    uint Argb=0xFFFFC247,bool IsVisible=true)
{
    public void Validate()
    {
        if(Id==Guid.Empty||!Enum.IsDefined(Kind))throw new CadValidationException("Invalid dimension identity or kind.");
        CadGuard.Name(Name);First.Validate();Second.Validate();Third.Validate();Normal.Validate();
        if(new[]{First,Second,Third}.Any(p=>!double.IsFinite(p.Length)||p.Length>1e9))throw new CadValidationException("Dimension points must be within 1e9 mm of the origin.");
        var a=First-Second;var b=Third-Second;
        if(a.Length<1e-8||Math.Abs(Normal.Length-1)>1e-10||!double.IsFinite(FlyoutMm)||Math.Abs(FlyoutMm)>1e6||
            Math.Abs(a.Dot(Normal))>1e-7*a.Length||Kind==EngineeringDimensionKind.Angle&&
            (b.Length<1e-8||a.Cross(b).Length<1e-8*a.Length*b.Length))
            throw new CadValidationException("Dimension requires distinct points, a valid plane and a finite flyout; angle rays must not be collinear.");
    }
}

public static class EngineeringReviewValidation
{
    public static void ValidateBindings(DocumentSnapshot doc)
    {
        if(doc.AssociatedSections.Count==0)return;
        var paths=doc.EnumerateOccurrences().ToDictionary(o=>o.Path);
        foreach(var s in doc.AssociatedSections.Values)
        {
            var result=doc.Features[s.FeatureId];
            if(result.IsStale!=(s.StaleReason is not null))throw new CadValidationException("Section cache and stale state disagree.");
            if(s.StaleReason is not null)continue;
            foreach(var source in s.Sources)
            {
                if(!paths.TryGetValue(source.Path,out var occurrence)||occurrence.WorldTransform!=source.Transform)
                    throw new CadValidationException("Section cache placement is stale.");
                GeometryAssetRef? geometry=null;
                if(source.FeatureId is {} id&&doc.Features.TryGetValue(id,out var f)&&!f.IsStale&&f.OutputBodyId==source.BodyId&&f.PartId==occurrence.DefinitionId)geometry=f.Result;
                if(source.FeatureId is null&&doc.Bodies.TryGetValue(source.BodyId,out var b)&&b.Producer is null&&b.PartId==occurrence.DefinitionId)geometry=b.Geometry;
                if(geometry is null||geometry.AssetId!=source.Asset||geometry.Revision!=source.Revision)throw new CadValidationException("Section cache source identity is stale.");
            }
        }
    }
    public static void Validate(DocumentSnapshot doc)
    {
        if(doc.ReviewBookmarks.Count>128)throw new CadValidationException("Too many review bookmarks.");
        foreach(var (id,b) in doc.ReviewBookmarks)
        {
            if(id==Guid.Empty||id!=b.Id||b.SectionAxis is <0 or >2||!double.IsFinite(b.SectionOffsetMm)||Math.Abs(b.SectionOffsetMm)>1e9||
                b.SlabThicknessMm is {} t&&(!double.IsFinite(t)||t<=0||t>1e9)||b.Hidden.IsDefault||b.Hidden.Length>100000||
                b.Isolated is {} isolated&&(isolated.IsDefault||isolated.Length>100000))throw new CadValidationException("Invalid review bookmark.");
            CadGuard.Name(b.Name);b.Primary.Validate();b.Secondary?.Validate();
            foreach(var key in b.Hidden.Concat(b.Isolated??[]))
            {CadGuard.Id(key.BodyId);if(key.Path.DocumentId!=doc.Id||key.Path.Slots.Length is <1 or >128)throw new CadValidationException("Invalid review visibility path.");}
        }
        if(doc.AssociatedSections.Count>1024||doc.Dimensions.Count>10000)throw new CadValidationException("Review table exceeds its limit.");
        foreach(var (id,d) in doc.Dimensions){if(id!=d.Id)throw new CadValidationException("Dimension key mismatch.");d.Validate();}
        foreach(var (id,s) in doc.AssociatedSections)
        {
            if(id!=s.FeatureId||!doc.Features.TryGetValue(id,out var feature)||feature.Recipe is not ImportedRecipe||
                !Enum.IsDefined(s.Output)||s.Sources.IsDefaultOrEmpty||s.Sources.Length>256||
                s.Sources.Select(x=>(x.Path,x.BodyId)).Distinct().Count()!=s.Sources.Length||
                Math.Abs(s.Normal.Length-1)>1e-10||!double.IsFinite(s.OffsetMm)||Math.Abs(s.OffsetMm)>1e9)
                throw new CadValidationException("Invalid associated section.");
            s.Normal.Validate();
            foreach(var source in s.Sources)
            {
                if(source.Path.DocumentId!=doc.Id||source.Path.Slots.Length is <1 or >128||source.FeatureId==id)
                    throw new CadValidationException("Invalid section source identity.");
                CadGuard.Id(source.BodyId);CadGuard.Id(source.Revision);source.Asset.Validate();source.Transform.Validate();
                if(source.FeatureId is {} f)CadGuard.Id(f);
            }
        }
        // Section outputs are review artifacts. Detach before modeling them or using them in another association.
        foreach(var s in doc.AssociatedSections.Values)
            if(doc.Features.Values.Any(f=>f.Inputs.Contains(s.FeatureId))||
                doc.AssociatedSections.Values.Any(other=>other.Sources.Any(x=>x.FeatureId==s.FeatureId)))
                throw new CadValidationException("Detach the associated section before using its result as a modeling or section input.");
    }
}
