namespace Cadoryx.Db;

/// <summary>Transient correspondence from an explicit corner edit, never serialized or inferred by distance.</summary>
public sealed record SketchCornerReplacement(SketchEntityId Joint,SketchEntityId LineA,SketchEntityId LineB,
    SketchEntityId Connection,SketchEntityId PointA,SketchEntityId PointB);
