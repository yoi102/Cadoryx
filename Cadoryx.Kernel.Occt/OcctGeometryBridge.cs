using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using System.Diagnostics;

namespace Cadoryx.Kernel.Occt;

/// <summary>Only native adapters use this bridge. Each read constructs an independent native graph.</summary>
public static class OcctGeometryBridge
{
    public readonly record struct ShapeStoreTiming(double TopologyMs,double ValidityMs,double BoundsMs,
        double VolumeMs,double WriteMs,double StageMs);
    public static Shape ReadShape(GeometryAssetRef geometry,IAssetStore assets)
    {
        geometry.Validate();
        if(geometry.SerializedOccurrences is not null)
        {
            using var graph=ReadOccurrenceShape(geometry,assets);
            // Resolve creates a separate native root handle before the query owner is released.
            return graph.Resolve([]);
        }
        if(geometry.SerializedReferences is not null)
        {
            var graph=ReadReferencedShape(geometry,assets);
            // Transfer the root to the caller. Members own separate handles into this same graph.
            foreach(var member in graph.Members)member.Dispose();
            return graph.Root;
        }
        using var lease=assets.Acquire(geometry.AssetId);using var files=new KernelFiles();
        var format=geometry.Format??AssetFormatPolicy.Inspect(lease.Content.Span);
        AssetFormatPolicy.RequireBRep(format);AssetFormatPolicy.VerifyPayload(format,lease.Content.Span);
        var path=files.PathFor("input.brep");File.WriteAllBytes(path,lease.Content.ToArray());
        return ShapeExchange.ReadBrep(path);
    }
    /// <summary>Root and members belong to this one decoded graph. File import never proves original capture.</summary>
    public static BrepReferenceRestoreResult ReadReferencedShape(GeometryAssetRef geometry,IAssetStore assets)
    {
        geometry.Validate();var references=geometry.SerializedReferences??throw new CadValidationException("No stored native reference evidence; explicitly select the current graph.");
        using var boundary=assets.Acquire(geometry.AssetId);using var table=assets.Acquire(references.TableAssetId);
        var referenceFormat=references.Format??AssetFormatPolicy.Inspect(table.Content.Span);
        AssetFormatPolicy.RequireReferenceTable(referenceFormat);AssetFormatPolicy.VerifyPayload(referenceFormat,table.Content.Span);
        var format=geometry.Format??AssetFormatPolicy.Inspect(boundary.Content.Span);
        AssetFormatPolicy.RequireBRep(format);AssetFormatPolicy.VerifyPayload(format,boundary.Content.Span);
        SerializedReferenceAssetPolicy.VerifyBinding(boundary.Content.Span,table.Content.Span);
        return BrepReferenceSnapshot.Open(boundary.Content.Span,table.Content.Span).Restore();
    }
    public static int FindDecodedReferenceIndex(BrepReferenceRestoreResult graph,int referenceIndex)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if((uint)referenceIndex>=(uint)graph.Members.Count)throw new ArgumentOutOfRangeException(nameof(referenceIndex));
        int actual=RepairSnapshot.FindTopologyIndex(graph.Root,graph.Members[referenceIndex]);
        if(actual<0||actual!=graph.Report.Rows[referenceIndex].DecodedTopologyIndex)
            throw new CadValidationException("Stored reference is not an actual member of this decoded root.");
        return actual;
    }
    public static BrepOccurrenceRestoreResult ReadOccurrenceShape(GeometryAssetRef geometry,IAssetStore assets,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();geometry.Validate();
        var occurrences=geometry.SerializedOccurrences??throw new CadValidationException("No complete stored current occurrence graph; explicitly select current geometry.");
        using var boundary=assets.Acquire(geometry.AssetId);using var table=assets.Acquire(occurrences.TableAssetId);
        var format=geometry.Format??AssetFormatPolicy.Inspect(boundary.Content.Span);
        AssetFormatPolicy.RequireBRep(format);AssetFormatPolicy.VerifyPayload(format,boundary.Content.Span);
        var tableFormat=occurrences.Format??AssetFormatPolicy.Inspect(table.Content.Span);
        AssetFormatPolicy.RequireOccurrenceTable(tableFormat);AssetFormatPolicy.VerifyPayload(tableFormat,table.Content.Span);
        SerializedOccurrenceAssetPolicy.VerifyBinding(boundary.Content.Span,table.Content.Span);
        return BrepOccurrenceSnapshot.Open(boundary.Content.Span,table.Content.Span,token).Restore(token);
    }
    /// <summary>Explicit current selection only. Context labels guard freshness; actual paths are verified by the native decoder.</summary>
    public static Shape ResolveCurrentOccurrence(Cadoryx.Db.DocumentSnapshot current,DocumentId expectedDocument,BodyId body,
        GeometryAssetRef expected,IReadOnlyList<int> path,IAssetStore assets,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();expected.Validate();
        if(current.Id!=expectedDocument||!current.Bodies.TryGetValue(body,out var selected)||selected.Geometry!=expected||
            selected.Producer is {} producer&&(!current.Features.TryGetValue(producer,out var feature)||feature.IsStale||feature.IsSuppressed))
            throw new CadValidationException("Occurrence selection is foreign or stale; select the current body explicitly.");
        using var graph=ReadOccurrenceShape(expected,assets,token);var member=graph.Resolve(path);
        try{token.ThrowIfCancellationRequested();return member;}catch{member.Dispose();throw;}
    }
    /// <summary>The primary BRep and complete table are produced by one native Capture. No proof flag is persisted.</summary>
    public static GeometryResult StoreShapeWithOccurrences(Shape shape,IAssetStore assets,CancellationToken token=default,
        Action<BrepOccurrenceReport>? captureEvidence=null)
    {
        token.ThrowIfCancellationRequested();var snapshot=BrepOccurrenceSnapshot.Capture(shape,token);
        return StoreCapturedOccurrences(shape,snapshot,assets,token,captureEvidence);
    }
    /// <summary>Optional current-path evidence for already valid geometry. Only capture rejection permits
    /// ordinary geometry; asset staging, callbacks, disposal and cancellation failures still propagate.</summary>
    public static GeometryResult StoreShapeWithPreferredOccurrences(Shape shape,IAssetStore assets,CancellationToken token=default,
        Action<BrepOccurrenceReport>? captureEvidence=null)
    {
        ArgumentNullException.ThrowIfNull(shape);ArgumentNullException.ThrowIfNull(assets);token.ThrowIfCancellationRequested();
        if(!shape.IsValid)throw new CadValidationException("Cannot retain invalid geometry as an occurrence capture fallback.");
        BrepOccurrenceSnapshot snapshot;
        try{snapshot=BrepOccurrenceSnapshot.Capture(shape,token);}
        catch(ArgumentException error)
        {
            // SDK26 uses InvalidArgument for bounded row/depth/byte rejection. It also
            // covers other capture rejections, so preserve the actual reason and do not
            // label every rejection as a budget failure or publish a partial table.
            token.ThrowIfCancellationRequested();using var ordinary=StoreShape(shape,assets);
            token.ThrowIfCancellationRequested();return new(ordinary.Geometry,GeometryAssetLease.Acquire(ordinary.Geometry,assets),
                [new("MODELING.OCCURRENCE_CAPTURE_UNAVAILABLE","Complete current occurrence capture was unavailable: "+error.Message+
                    " Geometry was retained without an occurrence table; explicitly select its decoded graph.")]);
        }
        return StoreCapturedOccurrences(shape,snapshot,assets,token,captureEvidence);
    }
    private static GeometryResult StoreCapturedOccurrences(Shape shape,BrepOccurrenceSnapshot snapshot,IAssetStore assets,
        CancellationToken token,Action<BrepOccurrenceReport>? captureEvidence)
    {
        // Both callers capture this actual shape once immediately before entering.
        // The primary boundary and table remain from that same snapshot; metadata
        // serialization is temporary and is never used as the table's boundary.
        using var metadata=StoreShape(shape,assets);IAssetLease? boundary=null,table=null;
        try
        {
            token.ThrowIfCancellationRequested();boundary=assets.Stage(snapshot.CopyBrepBytes());table=assets.Stage(snapshot.CopyOccurrenceTable());
            var geometry=metadata.Geometry with{AssetId=boundary.Id,SerializedOccurrences=new(table.Id,Format:AssetFormatPolicy.CurrentOccurrenceTable)};
            geometry.Validate();SerializedOccurrenceAssetPolicy.VerifyBinding(boundary.Content.Span,table.Content.Span);
            token.ThrowIfCancellationRequested();captureEvidence?.Invoke(snapshot.Report);token.ThrowIfCancellationRequested();
            var owned=GeometryAssetLease.Own(boundary,table);boundary=null;table=null;return new(geometry,owned);
        }
        finally{boundary?.Dispose();table?.Dispose();}
    }
    /// <summary>Captures borrowed native members while the actual source root is alive. No proof flags enter storage.</summary>
    public static GeometryResult StoreShapeWithReferences(Shape shape,IEnumerable<Shape> references,IAssetStore assets,
        CancellationToken token=default,Action<BrepReferenceReport>? captureEvidence=null)
    {
        token.ThrowIfCancellationRequested();var snapshot=BrepReferenceSnapshot.Capture(shape,references);
        token.ThrowIfCancellationRequested();using var metadata=StoreShape(shape,assets);
        return StoreCapturedReferences(metadata.Geometry,snapshot,assets,token,captureEvidence);
    }
    internal static GeometryResult StoreCapturedReferences(GeometryAssetRef metadata,BrepReferenceSnapshot snapshot,IAssetStore assets,
        CancellationToken token,Action<BrepReferenceReport>? captureEvidence=null)
    {
        IAssetLease? boundary=null,table=null;
        try
        {
            boundary=assets.Stage(snapshot.CopyBrepBytes());table=assets.Stage(snapshot.CopyReferenceTable());
            var geometry=metadata with{AssetId=boundary.Id,SerializedReferences=new(table.Id,Format:AssetFormatPolicy.CurrentReferenceTable)};
            geometry.Validate();SerializedReferenceAssetPolicy.VerifyBinding(boundary.Content.Span,table.Content.Span);
            token.ThrowIfCancellationRequested();captureEvidence?.Invoke(snapshot.Report);
            var owned=GeometryAssetLease.Own(boundary,table);boundary=null;table=null;
            return new(geometry,owned);
        }
        finally{boundary?.Dispose();table?.Dispose();}
    }
    public static XdeDocument ReadContext(AssetId id,IAssetStore assets,AssetFormat? declaredFormat=null)
    {
        using var lease=assets.Acquire(id);using var files=new KernelFiles();
        var format=declaredFormat??AssetFormatPolicy.Inspect(lease.Content.Span);AssetFormatPolicy.RequireXde(format);AssetFormatPolicy.VerifyPayload(format,lease.Content.Span);
        var path=files.PathFor("context.xbf");File.WriteAllBytes(path,lease.Content.ToArray());
        return XdeDocument.Open(path);
    }
    public static GeometryResult StoreShape(Shape shape,IAssetStore assets,Action<ShapeStoreTiming>? timing=null,bool adaptiveVolume=false)
    {
        long start=Stopwatch.GetTimestamp();
        var summary=shape.GetTopologySummary();var counts=summary.UniqueCounts;
        bool empty=counts.VertexCount==0&&counts.EdgeCount==0&&counts.FaceCount==0;
        double topologyMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;start=Stopwatch.GetTimestamp();
        // GetTopologySummary already runs the same BRepCheck_Analyzer::IsValid query.
        // Repeating Shape.IsValid traverses every imported BRep a second time.
        if(!empty&&!summary.IsValid)throw new CadValidationException("OCCT produced invalid topology.");
        double validityMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;start=Stopwatch.GetTimestamp();
        var kind=empty?BodyKind.Empty:counts.SolidCount==1?BodyKind.Solid:counts.SolidCount>1?BodyKind.Compound:counts.FaceCount>0?BodyKind.Sheet:BodyKind.Wire;
        var box=empty?new Bounds3d(Vector3d.Zero,Vector3d.Zero):Bounds(shape.GetBoundingBox());
        double boundsMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;start=Stopwatch.GetTimestamp();
        // Authored exact freeform profiles need adaptive quadrature; fixed integration
        // can misreport a valid elliptical offset prism's persisted volume.
        double volume=counts.SolidCount>0?Math.Abs(adaptiveVolume?
            shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-11).Properties.Mass:
            shape.InspectProperties(InspectionPropertyKind.Volume).Mass):0;
        double volumeMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;start=Stopwatch.GetTimestamp();
        using var files=new KernelFiles();var path=files.PathFor("result.brep");ShapeExchange.WriteBrep(shape,path);
        double writeMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;start=Stopwatch.GetTimestamp();
        var lease=assets.Stage(File.ReadAllBytes(path));
        double stageMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        try
        {
            var reference=new GeometryAssetRef(lease.Id,GeometryRevisionId.New(),kind,box,volume,Format:AssetFormatPolicy.CurrentBRep);reference.Validate();
            timing?.Invoke(new(topologyMs,validityMs,boundsMs,volumeMs,writeMs,stageMs));
            return new(reference,lease);
        }
        catch{lease.Dispose();throw;}
    }
    public static GpTrsf ToNative(RigidTransform3d transform)
    {
        transform.Validate();var q=transform.Rotation;double angle=2*Math.Acos(Math.Clamp(q.W,-1,1));
        var axis=new Vector3d(q.X,q.Y,q.Z);axis=axis.Length<1e-12?Vector3d.UnitZ:axis.Normalized();
        return GpTrsf.Create(transform.Translation.X,transform.Translation.Y,transform.Translation.Z,axis.X,axis.Y,axis.Z,angle);
    }
    public static RigidTransform3d FromNative(GpTrsf t)
    {
        var x=new Vector3d(t.Value(1,1),t.Value(2,1),t.Value(3,1));
        var y=new Vector3d(t.Value(1,2),t.Value(2,2),t.Value(3,2));
        var z=new Vector3d(t.Value(1,3),t.Value(2,3),t.Value(3,3));
        if(Math.Abs(x.Length-1)>1e-7||Math.Abs(y.Length-1)>1e-7||Math.Abs(z.Length-1)>1e-7||
            Math.Abs(x.Dot(y))>1e-7||Math.Abs(x.Cross(y).Dot(z)-1)>1e-7)
            throw new NotSupportedException("Scaled or mirrored assembly placements must be baked before import.");
        double a=x.X,b=y.X,c=z.X,d=x.Y,e=y.Y,f=z.Y,g=x.Z,h=y.Z,i=z.Z;
        Quaterniond q;
        double trace=a+e+i;
        if(trace>0){var s=Math.Sqrt(trace+1)*2;q=new((h-f)/s,(c-g)/s,(d-b)/s,s/4);}
        else if(a>e&&a>i){var s=Math.Sqrt(1+a-e-i)*2;q=new(s/4,(b+d)/s,(c+g)/s,(h-f)/s);}
        else if(e>i){var s=Math.Sqrt(1+e-a-i)*2;q=new((b+d)/s,s/4,(f+h)/s,(c-g)/s);}
        else{var s=Math.Sqrt(1+i-a-e)*2;q=new((c+g)/s,(f+h)/s,s/4,(d-b)/s);}
        var result=new RigidTransform3d(new(t.Value(1,4),t.Value(2,4),t.Value(3,4)),q);result.Validate();return result;
    }
    public static uint ToArgb(XdeColor color)
    {
        static uint Srgb(double v)=>(uint)Math.Round(255*(v<=0.0031308?v*12.92:1.055*Math.Pow(v,1/2.4)-0.055));
        return ((uint)Math.Round(color.Alpha*255)<<24)|(Srgb(color.Red)<<16)|(Srgb(color.Green)<<8)|Srgb(color.Blue);
    }
    public static XdeColor ToXdeColor(uint argb)
    {
        static double Linear(uint v){double n=v/255.0;return n<=0.04045?n/12.92:Math.Pow((n+0.055)/1.055,2.4);}
        return new(Linear((argb>>16)&255),Linear((argb>>8)&255),Linear(argb&255),(argb>>24)/255.0);
    }
    private static Bounds3d Bounds(BoundingBox3d b)=>new(new(b.Minimum.X,b.Minimum.Y,b.Minimum.Z),new(b.Maximum.X,b.Maximum.Y,b.Maximum.Z));
}
internal sealed class KernelFiles : IDisposable
{
    private readonly string directory;
    public KernelFiles(){directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Cadoryx",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);}
    public string PathFor(string fileName)=>System.IO.Path.Combine(directory,fileName);
    public void Dispose()
    {
        var boundary=System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Cadoryx"))+System.IO.Path.DirectorySeparatorChar;
        if(!System.IO.Path.GetFullPath(directory).StartsWith(boundary,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Invalid temporary directory.");
        try{Directory.Delete(directory,true);}catch(IOException){/* Antivirus may briefly hold a file; startup cleanup may reclaim it. */}
    }
}
