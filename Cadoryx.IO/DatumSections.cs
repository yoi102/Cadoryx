using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackDatumBook([property:Key(0)] PackDatumPlane[] Planes,[property:Key(1)] PackSketchDatum[] SketchBindings,[property:Key(2)] PackWorkPlaneDatum? WorkPlane,
    [property:Key(3)] PackDatumAxis[]? Axes=null,[property:Key(4)] PackDatumPoint[]? Points=null);
[MessagePackObject]
public sealed record PackDatumPlane([property:Key(0)] Guid Id,[property:Key(1)] Guid Part,[property:Key(2)] string Name,[property:Key(3)] int Kind,
    [property:Key(4)] PackDatumSource Primary,[property:Key(5)] PackDatumSource? Secondary,[property:Key(6)] double Value,[property:Key(7)] int Hinge,
    [property:Key(8)] Guid Revision,[property:Key(9)] PackTransform Frame,[property:Key(10)] Guid[] SourceRevisions,
    [property:Key(11)] bool Stale=false,[property:Key(12)] Guid? HingeAxis=null);
[MessagePackObject]
public sealed record PackDatumSource([property:Key(0)] int Kind,[property:Key(1)] int Basis,[property:Key(2)] Guid? Datum,[property:Key(3)] Guid? Feature,[property:Key(4)] int? Boundary,
    [property:Key(5)] PackExactDatum? Exact=null,[property:Key(6)] PackTransform? Authored=null,[property:Key(7)] Guid? Point=null);
[MessagePackObject]
public sealed record PackDatumVector([property:Key(0)] double X,[property:Key(1)] double Y,[property:Key(2)] double Z);
[MessagePackObject]
public sealed record PackExactDatum([property:Key(0)] PackExactTopologySelection Selection,[property:Key(1)] PackGeometry Source,
    [property:Key(2)] int Kind,[property:Key(3)] PackDatumVector Point,[property:Key(4)] PackDatumVector Direction,[property:Key(5)] double Radius);
[MessagePackObject]
public sealed record PackDatumAxis([property:Key(0)] Guid Id,[property:Key(1)] Guid Part,[property:Key(2)] string Name,[property:Key(3)] int Kind,
    [property:Key(4)] Guid? First,[property:Key(5)] Guid? Second,[property:Key(6)] PackExactDatum? Exact,[property:Key(7)] int Basis,[property:Key(8)] bool Reverse,
    [property:Key(9)] PackDatumVector Origin,[property:Key(10)] PackDatumVector Direction,[property:Key(11)] double Offset,
    [property:Key(12)] Guid Revision,[property:Key(13)] PackTransform Frame,[property:Key(14)] Guid[] SourceRevisions,[property:Key(15)] bool Stale);
[MessagePackObject]
public sealed record PackDatumPoint([property:Key(0)] Guid Id,[property:Key(1)] Guid Part,[property:Key(2)] string Name,[property:Key(3)] int Kind,
    [property:Key(4)] PackDatumVector Value,[property:Key(5)] Guid? First,[property:Key(6)] Guid? Second,[property:Key(7)] Guid? Third,[property:Key(8)] PackExactDatum? Exact,
    [property:Key(9)] Guid Revision,[property:Key(10)] PackDatumVector Position,[property:Key(11)] Guid[] SourceRevisions,[property:Key(12)] bool Stale);
[MessagePackObject]
public sealed record PackSketchDatum([property:Key(0)] Guid Sketch,[property:Key(1)] Guid Datum,[property:Key(2)] Guid Revision);
[MessagePackObject]
public sealed record PackWorkPlaneDatum([property:Key(0)] Guid Datum,[property:Key(1)] Guid[] Slots,[property:Key(2)] Guid Revision,[property:Key(3)] PackTransform Transform);

internal static partial class MessagePackSections
{
    private static PackDatumVector DV(Vector3d p)=>new(p.X,p.Y,p.Z);
    private static Vector3d DV(PackDatumVector p)=>p is null?throw new InvalidDataException("Missing datum coordinate."):new(p.X,p.Y,p.Z);
    private static PackExactDatum DE(ExactDatumGeometry p)=>new(PackExact(p.Selection),G(p.Source),(int)p.Kind,DV(p.Point),DV(p.Direction),p.Radius);
    private static ExactDatumGeometry DE(PackExactDatum p)=>new(UnpackExact(p.Selection),G(p.Source),(ExactDatumKind)p.Kind,DV(p.Point),DV(p.Direction),p.Radius);
    private static PackDatumSource DS(DatumPlaneSource s)=>new((int)s.Kind,(int)s.Basis,s.Datum,s.Feature?.Value,s.Boundary is {} b?(int)b:null,s.Exact is {} e?DE(e):null,s.AuthoredFrame is {} a?T(a):null,s.Point);
    private static DatumPlaneSource DS(PackDatumSource s)=>new((DatumSourceKind)s.Kind,(DocumentWorkPlaneKind)s.Basis,s.Datum,s.Feature is {} f?new FeatureId(f):null,s.Boundary is {} b?(BoxBoundary)b:null,s.Exact is {} e?DE(e):null,s.Authored is {} a?T(a):null,s.Point);
    public static byte[] EncodeDatums(DatumBook book)=>Serialize(new PackDatumBook(
        book.Planes.Values.OrderBy(p=>p.Id).Select(p=>new PackDatumPlane(p.Id,p.PartId.Value,p.Name,(int)p.Kind,DS(p.Primary),p.Secondary is {} s?DS(s):null,
            p.Value,(int)p.Hinge,p.Revision,T(p.Frame),p.SourceRevisions.ToArray(),p.IsStale,p.HingeAxis)).ToArray(),
        book.SketchBindings.Values.OrderBy(b=>b.Sketch.Value).Select(b=>new PackSketchDatum(b.Sketch.Value,b.Datum,b.Revision)).ToArray(),
        book.WorkPlane is {} w?new(w.Datum,w.Occurrence.Slots.Select(s=>s.Value).ToArray(),w.Revision,T(w.OccurrenceTransform)):null,
        book.Axes.Values.OrderBy(a=>a.Id).Select(a=>new PackDatumAxis(a.Id,a.PartId.Value,a.Name,(int)a.Kind,a.First,a.Second,a.Exact is {} e?DE(e):null,(int)a.Basis,a.Reverse,
            DV(a.Origin),DV(a.Direction),a.Offset,a.Revision,T(a.Frame),a.SourceRevisions.ToArray(),a.IsStale)).ToArray(),
        book.Points.Values.OrderBy(p=>p.Id).Select(p=>new PackDatumPoint(p.Id,p.PartId.Value,p.Name,(int)p.Kind,DV(p.Value),p.First,p.Second,p.Third,p.Exact is {} e?DE(e):null,
            p.Revision,DV(p.Position),p.SourceRevisions.ToArray(),p.IsStale)).ToArray()));
    public static DatumBook DecodeDatums(ReadOnlyMemory<byte> bytes,DocumentId document,IReadOnlyDictionary<AssetId,AssetFormat>? formats=null)
    {
        var book=Read<PackDatumBook>(bytes);
        if(book.Planes is null||book.SketchBindings is null||book.Planes.Length+(book.Axes?.Length??0)+(book.Points?.Length??0)>256||book.SketchBindings.Length>4096||book.WorkPlane?.Slots is {Length:>128})
            throw new InvalidDataException("Invalid or oversized datum book.");
        ExactDatumGeometry? Evidence(ExactDatumGeometry? e)
        {
            if(e is null||formats is null)return e;
            if(!formats.TryGetValue(e.Source.AssetId,out var format))throw new InvalidDataException("Missing exact datum asset.");
            var source=e.Source.Source;
            if(source is not null){if(!formats.TryGetValue(source.ContextAssetId,out var context))throw new InvalidDataException("Missing exact datum XDE context.");source=source with{Format=context};}
            return e with{Source=AttachReferenceFormat(e.Source with{Format=format,Source=source},formats)};
        }
        DatumPlaneSource Source(PackDatumSource s){var value=DS(s);return value with{Exact=Evidence(value.Exact)};}
        return new DatumBook(book.Planes.Select(p=>new CadDatumPlane(p.Id,new(p.Part),p.Name,(DatumPlaneKind)p.Kind,Source(p.Primary),p.Secondary is {} s?Source(s):null,p.Value,(DatumHinge)p.Hinge)
            {Revision=p.Revision,Frame=T(p.Frame),SourceRevisions=p.SourceRevisions.ToImmutableArray(),IsStale=p.Stale,HingeAxis=p.HingeAxis}).ToImmutableDictionary(p=>p.Id),
            book.SketchBindings.Select(b=>new SketchDatumBinding(new(b.Sketch),b.Datum,b.Revision)).ToImmutableDictionary(b=>b.Sketch),
            book.WorkPlane is {} w?new(w.Datum,new(document,w.Slots.Select(s=>new ComponentSlotId(s))),w.Revision,T(w.Transform)):null)
        {Axes=(book.Axes??[]).Select(a=>new CadDatumAxis(a.Id,new(a.Part),a.Name,(DatumAxisKind)a.Kind,a.First,a.Second,a.Exact is {} e?Evidence(DE(e)):null,(DatumBasisAxis)a.Basis,a.Reverse,DV(a.Origin),DV(a.Direction),a.Offset)
            {Revision=a.Revision,Frame=T(a.Frame),SourceRevisions=a.SourceRevisions.ToImmutableArray(),IsStale=a.Stale}).ToImmutableDictionary(a=>a.Id),
         Points=(book.Points??[]).Select(p=>new CadDatumPoint(p.Id,new(p.Part),p.Name,(DatumPointKind)p.Kind,DV(p.Value),p.First,p.Second,p.Third,p.Exact is {} e?Evidence(DE(e)):null)
            {Revision=p.Revision,Position=DV(p.Position),SourceRevisions=p.SourceRevisions.ToImmutableArray(),IsStale=p.Stale}).ToImmutableDictionary(p=>p.Id)};
    }
    internal static IEnumerable<PackGeometry> PackedDatumGeometries(ReadOnlyMemory<byte> bytes)
    {
        var b=Read<PackDatumBook>(bytes);
        return b.Planes.SelectMany(p=>new[]{p.Primary.Exact,p.Secondary?.Exact}).Concat((b.Axes??[]).Select(a=>a.Exact)).Concat((b.Points??[]).Select(p=>p.Exact)).OfType<PackExactDatum>().Select(e=>e.Source);
    }
    internal static byte[] EmptyLegacyDatums()=>Serialize(new PackDatumBook([],[],null));
    internal static ReadOnlyMemory<byte> UpgradeExtendedDatums(ReadOnlyMemory<byte> bytes)
    {
        var b=Read<PackDatumBook>(bytes);
        static bool Extended(PackDatumSource s)=>s.Kind>2||s.Exact is not null||s.Authored is not null||s.Point is not null;
        if(b.Axes is not null||b.Points is not null||b.Planes is null||b.Planes.Any(p=>p.Stale||p.HingeAxis is not null||Extended(p.Primary)||p.Secondary is {} s&&Extended(s)))
            throw new InvalidDataException("Extended datum data in legacy schema.");
        return bytes;
    }
}
