using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.ViewModels.Toolboxes;

// These records contain only author data and exact selections. Native anchors, measurements,
// projected strokes, quantities and revision caches are always rebuilt by production services.
internal sealed record DrawingSelectionAuthor(string Path,Guid BodyId,Guid Revision,string Asset,string Fingerprint,int Index);
internal sealed record DrawingSheetAuthor(string Name,double WidthMm,double HeightMm,LengthUnit Unit,string Title,
    DrawingStandard Standard=DrawingStandard.ISO,string? Author=null,DrawingSheetTemplate? Template=null);
internal sealed record DrawingViewAuthor(string Name,DrawingViewKind Kind,Point2d CenterMm,double Scale,
    Guid? ParentView=null,string? SourcePath=null,Vector3d? SectionNormal=null,double SectionOffsetMm=0,
    Point2d? DetailCenter=null,double DetailRadius=0,DrawingSectionHatching? Hatching=null);
internal sealed record DrawingDimensionAuthor(Guid ViewId,DrawingMeasureKind Kind,DrawingSelectionAuthor First,
    Point2d TextPositionMm,DrawingSelectionAuthor? Second=null,uint Argb=0xFF202020,double UpperTolerance=0,double LowerTolerance=0);
internal sealed record DrawingDatumAuthor(Guid ViewId,string Name,DrawingSelectionAuthor Source,Point2d PositionMm,
    DrawingDatumFeatureType FeatureType=DrawingDatumFeatureType.Surface,DrawingSelectionAuthor? Opposite=null,bool InternalWidth=false);
internal sealed record DrawingControlAuthor(Guid ViewId,DrawingSelectionAuthor Source,Point2d PositionMm,
    DrawingGeometricCharacteristic Characteristic,double ToleranceMm,DrawingToleranceTarget Target=DrawingToleranceTarget.Surface,
    bool DiameterZone=false,DrawingMaterialCondition MaterialCondition=DrawingMaterialCondition.None,
    ImmutableArray<DrawingDatumGroup> DatumSystem=default,DrawingSelectionAuthor? Opposite=null,bool InternalWidth=false);
internal sealed record DrawingBalloonAuthor(Guid ViewId,DrawingSelectionAuthor Source,Point2d PositionMm,int ItemNumber);
internal sealed record DrawingHoleRowAuthor(int Number,DrawingSelectionAuthor Source,Guid? Id=null);
internal sealed record DrawingFrameAuthor(Vector3d Origin,Vector3d Normal,Vector3d U);
internal sealed record DrawingCoordinateAuthor(DrawingHoleCoordinateBinding Binding,ImmutableArray<Guid> Revisions);
internal sealed record DrawingHoleTableAuthor(Guid ViewId,string Name,string CoordinatePath,DrawingFrameAuthor? Frame,
    Point2d PositionMm,ImmutableArray<DrawingHoleRowAuthor> Rows,DrawingCoordinateAuthor? Coordinates=null);
internal sealed record DrawingFieldAuthor(string Caption,string Value,Point2d PositionMm,double WidthMm,double HeightMm=12);
internal sealed record DrawingNoteAuthor(string Text,Point2d PositionMm,double WidthMm);
internal sealed record DrawingPartsAuthor(Point2d? PositionMm);
internal sealed record DrawingZonesAuthor(DrawingFrameZones? Zones);
