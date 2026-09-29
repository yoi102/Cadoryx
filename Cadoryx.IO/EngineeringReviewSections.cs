using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject] public sealed record PackEngineeringReview(
    [property:Key(0)] PackAssociatedSection[] Sections,[property:Key(1)] PackDimension[] Dimensions,[property:Key(2)] PackBookmark[] Bookmarks);
[MessagePackObject] public sealed record PackReviewCamera([property:Key(0)] double[] Eye,[property:Key(1)] double[] Target,
    [property:Key(2)] double[] Up,[property:Key(3)] double Aspect,[property:Key(4)] double Scale,[property:Key(5)] double Fov,
    [property:Key(6)] double Near,[property:Key(7)] double Far,[property:Key(8)] bool Perspective,[property:Key(9)] bool AutoDepth);
[MessagePackObject] public sealed record PackReviewInstance([property:Key(0)] Guid[] Path,[property:Key(1)] Guid Body);
[MessagePackObject] public sealed record PackBookmark([property:Key(0)] Guid Id,[property:Key(1)] string Name,
    [property:Key(2)] PackReviewCamera Primary,[property:Key(3)] PackReviewCamera? Secondary,[property:Key(4)] bool Split,
    [property:Key(5)] bool Section,[property:Key(6)] int Axis,[property:Key(7)] double Offset,[property:Key(8)] bool Reverse,
    [property:Key(9)] double? Thickness,[property:Key(10)] PackReviewInstance[] Hidden,[property:Key(11)] PackReviewInstance[]? Isolated);
[MessagePackObject] public sealed record PackAssociatedSection([property:Key(0)] Guid Feature,
    [property:Key(1)] double[] Normal,[property:Key(2)] double Offset,[property:Key(3)] int Output,
    [property:Key(4)] PackSectionSource[] Sources,[property:Key(5)] string? StaleReason);
[MessagePackObject] public sealed record PackSectionSource([property:Key(0)] Guid[] Path,
    [property:Key(1)] Guid Body,[property:Key(2)] Guid? Feature,[property:Key(3)] Guid Revision,
    [property:Key(4)] string Asset,[property:Key(5)] PackTransform Transform);
[MessagePackObject] public sealed record PackDimension([property:Key(0)] Guid Id,[property:Key(1)] string Name,
    [property:Key(2)] int Kind,[property:Key(3)] double[] First,[property:Key(4)] double[] Second,
    [property:Key(5)] double[] Third,[property:Key(6)] double[] Normal,[property:Key(7)] double Flyout,
    [property:Key(8)] uint Argb,[property:Key(9)] bool Visible);

internal static partial class MessagePackSections
{
    private static PackReviewCamera ReviewCam(ReviewCamera c)=>new(ReviewPoint(c.Eye),ReviewPoint(c.Target),ReviewPoint(c.Up),c.Aspect,c.Scale,c.FieldOfViewY,c.NearPlane,c.FarPlane,c.Perspective,c.AutoFitDepth);
    private static ReviewCamera ReviewCam(PackReviewCamera c)=>new(ReviewPoint(c.Eye),ReviewPoint(c.Target),ReviewPoint(c.Up),c.Aspect,c.Scale,c.Fov,c.Near,c.Far,c.Perspective,c.AutoDepth);
    private static PackReviewInstance[] ReviewKeys(IEnumerable<ReviewInstance> keys)=>keys.Select(k=>new PackReviewInstance(k.Path.Slots.Select(s=>s.Value).ToArray(),k.BodyId.Value)).ToArray();
    private static ImmutableArray<ReviewInstance> ReviewKeys(PackReviewInstance[] keys,DocumentId doc)
    {
        if(keys is null||keys.Length>100000)throw new InvalidDataException("Malformed review visibility.");
        return [..keys.Select(k=>k.Path is {Length:>0 and <=128}?new ReviewInstance(new(doc,k.Path.Select(p=>new ComponentSlotId(p))),new(k.Body)):
            throw new InvalidDataException("Malformed review visibility path."))];
    }
    private static double[] ReviewPoint(Vector3d p)=>[p.X,p.Y,p.Z];
    private static Vector3d ReviewPoint(double[] p)=>p is {Length:3}?new(p[0],p[1],p[2]):throw new InvalidDataException("Malformed review point.");
    public static byte[] EncodeEngineeringReview(DocumentSnapshot doc)=>Serialize(new PackEngineeringReview(
        doc.AssociatedSections.Values.OrderBy(s=>s.FeatureId.Value).Select(s=>new PackAssociatedSection(s.FeatureId.Value,
            ReviewPoint(s.Normal),s.OffsetMm,(int)s.Output,s.Sources.Select(x=>new PackSectionSource(x.Path.Slots.Select(p=>p.Value).ToArray(),
                x.BodyId.Value,x.FeatureId?.Value,x.Revision.Value,x.Asset.Sha256,T(x.Transform))).ToArray(),s.StaleReason)).ToArray(),
        doc.Dimensions.Values.OrderBy(d=>d.Id).Select(d=>new PackDimension(d.Id,d.Name,(int)d.Kind,ReviewPoint(d.First),ReviewPoint(d.Second),
            ReviewPoint(d.Third),ReviewPoint(d.Normal),d.FlyoutMm,d.Argb,d.IsVisible)).ToArray(),
        doc.ReviewBookmarks.Values.OrderBy(b=>b.Id).Select(b=>new PackBookmark(b.Id,b.Name,ReviewCam(b.Primary),b.Secondary is {} c?ReviewCam(c):null,
            b.SplitView,b.SectionEnabled,b.SectionAxis,b.SectionOffsetMm,b.SectionReverse,b.SlabThicknessMm,ReviewKeys(b.Hidden),b.Isolated is {} keys?ReviewKeys(keys):null)).ToArray()));
    public static DocumentSnapshot AttachEngineeringReview(ReadOnlyMemory<byte> bytes,DocumentSnapshot doc)
    {
        var packed=Read<PackEngineeringReview>(bytes);
        if(packed.Sections is null||packed.Dimensions is null||packed.Sections.Length>1024||packed.Dimensions.Length>10000||packed.Bookmarks is null||packed.Bookmarks.Length>128)
            throw new InvalidDataException("Malformed engineering review table.");
        return doc with{
            ReviewBookmarks=packed.Bookmarks.Select(b=>new ReviewBookmark(b.Id,b.Name,ReviewCam(b.Primary),b.Secondary is {} c?ReviewCam(c):null,
                b.Split,b.Section,b.Axis,b.Offset,b.Reverse,b.Thickness,ReviewKeys(b.Hidden,doc.Id),b.Isolated is {} keys?ReviewKeys(keys,doc.Id):null)).ToImmutableDictionary(b=>b.Id),
            AssociatedSections=packed.Sections.Select(s=>{
                if(s.Sources is null||s.Sources.Length is <1 or >256)throw new InvalidDataException("Malformed section sources.");
                return new AssociatedSection(new(s.Feature),ReviewPoint(s.Normal),s.Offset,(SectionOutput)s.Output,
                    [..s.Sources.Select(x=>{
                        if(x.Path is null||x.Path.Length is <1 or >128)throw new InvalidDataException("Malformed section occurrence path.");
                        return new SectionSource(new(doc.Id,x.Path.Select(p=>new ComponentSlotId(p))),new(x.Body),
                            x.Feature is {} f?new FeatureId(f):null,new(x.Revision),new(x.Asset),T(x.Transform));})],s.StaleReason);
            }).ToImmutableDictionary(s=>s.FeatureId),
            Dimensions=packed.Dimensions.Select(d=>new EngineeringDimension(d.Id,d.Name,(EngineeringDimensionKind)d.Kind,
                ReviewPoint(d.First),ReviewPoint(d.Second),ReviewPoint(d.Third),ReviewPoint(d.Normal),d.Flyout,d.Argb,d.Visible)).ToImmutableDictionary(d=>d.Id)};
    }
}
