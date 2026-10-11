using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum DrawingAttachmentGeometry { GeneralFace,PlaneFace,CylinderFace,GeneralEdge,LineEdge,CircleEdge }
public enum DrawingGeometricCharacteristic
{ Straightness,Flatness,Circularity,Cylindricity,ProfileLine,ProfileSurface,Parallelism,Perpendicularity,Angularity,Position,CircularRunout,TotalRunout }
public enum DrawingToleranceTarget { Surface,Axis,MedianPlane }
public enum DrawingMaterialCondition { None,Maximum,Least }
public enum DrawingDatumFeatureType { Surface,CylinderSizeAxis,PlanarSizeMedianPlane }

/// <summary>Attachment on one immutable original BRep. Face anchors lie on an exact boundary
/// edge, never on an unchecked midpoint of an untrimmed supporting surface.</summary>
public sealed record DrawingTopologyReference(OccurrencePath Path,DefinitionId DefinitionId,BodyId BodyId,
    FeatureId? FeatureId,GeometryRevisionId Revision,AssetId Asset,string Fingerprint,
    int FullTopologyIndex,TopologyKind Kind,DrawingAttachmentGeometry Geometry,
    int BoundaryEdgeIndex,double EdgeParameter,Vector3d LocalPoint,int SchemaVersion=1)
{
    /// <summary>Immutable author selection. This reference describes the derived current BRep;
    /// the origin is never replaced by a later traversal slot.</summary>
    public DrawingTopologyReference? HistoryOrigin {get;init;}
    // Keep this as a method: record formatting and JSON serialization inspect public
    // properties, so a computed property returning this would recurse indefinitely.
    public DrawingTopologyReference AuthorSelection()=>HistoryOrigin??this;
    public void Validate(DocumentId document)
    {
        if(Path is null||Path.DocumentId!=document||Path.Slots.Length is <1 or >128||SchemaVersion!=1||
           !Enum.IsDefined(Kind)||!Enum.IsDefined(Geometry)||FullTopologyIndex is <0 or >100000||
           Fingerprint is not {Length:64}||!Fingerprint.All(Uri.IsHexDigit)||!double.IsFinite(EdgeParameter)||
           (Kind==TopologyKind.Face?BoundaryEdgeIndex is <0 or >100000:BoundaryEdgeIndex!=-1)||
           (Kind==TopologyKind.Face)!=(Geometry is DrawingAttachmentGeometry.GeneralFace or DrawingAttachmentGeometry.PlaneFace or DrawingAttachmentGeometry.CylinderFace))
            throw new CadValidationException("Invalid exact drawing attachment.");
        CadGuard.Id(DefinitionId);CadGuard.Id(BodyId);CadGuard.Id(Revision);Asset.Validate();LocalPoint.Validate();
        if(FeatureId is {} feature)CadGuard.Id(feature);
        if(HistoryOrigin is {} origin)
        {
            if(origin.HistoryOrigin is not null||origin.FeatureId is null||FeatureId is null||origin.FeatureId==FeatureId||
               !origin.Path.Equals(Path)||origin.DefinitionId!=DefinitionId||origin.Kind!=Kind)
                throw new CadValidationException("Invalid manufacturing attachment history origin.");
            origin.Validate(document);
        }
    }
    public bool IsCurrent(DocumentSnapshot document)=>
        document.EnumerateOccurrences().Any(o=>o.Path.Equals(Path)&&o.DefinitionId==DefinitionId)&&
        document.Definitions.GetValueOrDefault(DefinitionId) is PartDefinition part&&part.Bodies.Contains(BodyId)&&
        document.Bodies.TryGetValue(BodyId,out var body)&&body.Producer==FeatureId&&
        body.Geometry.Revision==Revision&&body.Geometry.AssetId==Asset&&
        (FeatureId is not {} id||document.Features.TryGetValue(id,out var feature)&&!feature.IsStale&&
            feature.PartId==DefinitionId&&feature.OutputBodyId==BodyId&&feature.Result.Revision==Revision&&feature.Result.AssetId==Asset);
}

public sealed record DrawingDatumSymbol(Guid Id,Guid ViewId,string Name,DrawingTopologyReference Source,
    Point2d PositionMm,string? StaleReason=null)
{
    public DrawingDatumFeatureType FeatureType {get;init;}=DrawingDatumFeatureType.Surface;
    public DrawingCylinderSizeEvidence? SizeEvidence {get;init;}
    public DrawingPlanarSizeEvidence? PlanarSizeEvidence {get;init;}
    public void Validate(DocumentId document)
    {
        DrawingAnnotationGuard.Common(Id,ViewId,PositionMm,StaleReason);Source.Validate(document);
        if(!Enum.IsDefined(FeatureType)||FeatureType==DrawingDatumFeatureType.Surface&&SizeEvidence is not null||
            FeatureType==DrawingDatumFeatureType.CylinderSizeAxis&&(Source.Geometry!=DrawingAttachmentGeometry.CylinderFace||
                StaleReason is null&&SizeEvidence is null))
            throw new CadValidationException("A cylindrical datum axis requires complete original size evidence.");
        if(SizeEvidence is {} evidence){evidence.Validate(document);if(evidence.Source!=Source)throw new CadValidationException("Datum size evidence has a different attachment.");}
        if(FeatureType!=DrawingDatumFeatureType.PlanarSizeMedianPlane&&PlanarSizeEvidence is not null||
            FeatureType==DrawingDatumFeatureType.PlanarSizeMedianPlane&&(SizeEvidence is not null||StaleReason is null&&PlanarSizeEvidence is null))
            throw new CadValidationException("A size median plane requires two complete original planar face domains.");
        if(PlanarSizeEvidence is {} planar){planar.Validate(document);if(planar.Source!=Source)throw new CadValidationException("Median-plane proof has a different first attachment.");}
        // I, O and Q are excluded to avoid confusion with numbers; labels are explicit, stable IDs are dependencies.
        if(Name is not {Length:>=1 and <=3}||Name.Any(c=>c is <'A' or >'Z'||c is 'I' or 'O' or 'Q'))
            throw new CadValidationException("Datum labels use one to three uppercase letters, excluding I, O and Q.");
    }
}
public sealed record DrawingFeatureControlFrame(Guid Id,Guid ViewId,DrawingTopologyReference Source,
    Point2d PositionMm,DrawingGeometricCharacteristic Characteristic,double ToleranceMm,
    DrawingToleranceTarget Target=DrawingToleranceTarget.Surface,bool DiameterZone=false,
    DrawingMaterialCondition MaterialCondition=DrawingMaterialCondition.None,string? StaleReason=null)
{
    public ImmutableArray<Guid> Datums {get;init;}=[];
    public ImmutableArray<DrawingDatumGroup> DatumSystem {get;init;}=[];
    public DrawingCylinderSizeEvidence? SizeEvidence {get;init;}
    public DrawingPlanarSizeEvidence? PlanarSizeEvidence {get;init;}
    public ImmutableArray<DrawingDatumGroup> OrderedDatumGroups=>DatumSystem.IsEmpty?
        [..Datums.Select(id=>new DrawingDatumGroup([new(id)]))]:DatumSystem;
    public IEnumerable<Guid> ReferencedDatumIds=>OrderedDatumGroups.SelectMany(g=>g.References.Select(r=>r.Id));
    public ImmutableArray<DrawingDatumFeatureBinding> DatumBindings {get;init;}=[];
    public void Validate(DocumentId document)
    {
        DrawingAnnotationGuard.Common(Id,ViewId,PositionMm,StaleReason);Source.Validate(document);
        if(!Enum.IsDefined(Characteristic)||!Enum.IsDefined(Target)||!Enum.IsDefined(MaterialCondition)||
           !double.IsFinite(ToleranceMm)||ToleranceMm is <=0 or >1e6||Datums.IsDefault||Datums.Length>3||
           Datums.Any(id=>id==Guid.Empty)||Datums.Distinct().Count()!=Datums.Length)
            throw new CadValidationException("Invalid feature control frame.");
        if(DatumSystem.IsDefault||!DatumSystem.IsEmpty&&!Datums.IsEmpty||DatumSystem.Length>3||
            DatumSystem.Any(g=>g is null))throw new CadValidationException("Use one ordered datum system with at most three precedence groups.");
        foreach(var group in DatumSystem)group.Validate();
        var references=ReferencedDatumIds.ToArray();
        if(references.Distinct().Count()!=references.Length)throw new CadValidationException("A datum cannot be repeated within or across precedence groups.");
        if(DatumBindings.IsDefault||DatumBindings.Length>9||DatumBindings.Any(b=>b is null)||
            (StaleReason is null||!DatumBindings.IsEmpty)&&!DatumBindings.Select(b=>b.Id).SequenceEqual(references))
            throw new CadValidationException("Control frame datum feature bindings differ from its ordered references.");
        foreach(var binding in DatumBindings)
        {
            binding.Source.Validate(document);binding.OppositeSource?.Validate(document);
            if(!Enum.IsDefined(binding.FeatureType)||(binding.FeatureType==DrawingDatumFeatureType.PlanarSizeMedianPlane)!=(binding.OppositeSource is not null))
                throw new CadValidationException("Invalid datum feature binding type or second size face.");
        }
        if(Target==DrawingToleranceTarget.Surface&&SizeEvidence is not null||Target==DrawingToleranceTarget.Axis&&StaleReason is null&&SizeEvidence is null)
            throw new CadValidationException("An axis tolerance requires complete original cylindrical size evidence.");
        if(SizeEvidence is {} evidence){evidence.Validate(document);if(evidence.Source!=Source)throw new CadValidationException("Control frame size evidence has a different attachment.");}
        if(Target!=DrawingToleranceTarget.MedianPlane&&PlanarSizeEvidence is not null||
            Target==DrawingToleranceTarget.MedianPlane&&(SizeEvidence is not null||StaleReason is null&&PlanarSizeEvidence is null))
            throw new CadValidationException("A median-plane target requires a complete two-face planar size proof.");
        if(PlanarSizeEvidence is {} planar){planar.Validate(document);if(planar.Source!=Source)throw new CadValidationException("Median-plane proof has a different first attachment.");}
        bool form=Characteristic is DrawingGeometricCharacteristic.Straightness or DrawingGeometricCharacteristic.Flatness or
            DrawingGeometricCharacteristic.Circularity or DrawingGeometricCharacteristic.Cylindricity;
        bool referenceRequired=Characteristic is DrawingGeometricCharacteristic.Parallelism or DrawingGeometricCharacteristic.Perpendicularity or
            DrawingGeometricCharacteristic.Angularity or DrawingGeometricCharacteristic.Position or DrawingGeometricCharacteristic.CircularRunout or DrawingGeometricCharacteristic.TotalRunout;
        if(form&&references.Length!=0||referenceRequired&&references.Length==0)
            throw new CadValidationException("Form tolerances exclude datums; orientation, position and runout require datums.");
        if(Target==DrawingToleranceTarget.Axis&&Source.Geometry!=DrawingAttachmentGeometry.CylinderFace)
            throw new CadValidationException("An axis target requires an exact cylindrical face.");
        if(Target==DrawingToleranceTarget.Axis&&Characteristic is not (DrawingGeometricCharacteristic.Straightness or
            DrawingGeometricCharacteristic.Parallelism or DrawingGeometricCharacteristic.Perpendicularity or DrawingGeometricCharacteristic.Angularity or DrawingGeometricCharacteristic.Position))
            throw new CadValidationException("This characteristic requires a surface target.");
        if(Target==DrawingToleranceTarget.MedianPlane&&Characteristic is not (DrawingGeometricCharacteristic.Flatness or
            DrawingGeometricCharacteristic.Parallelism or DrawingGeometricCharacteristic.Perpendicularity or DrawingGeometricCharacteristic.Angularity or DrawingGeometricCharacteristic.Position))
            throw new CadValidationException("This characteristic does not support a planar size median-plane target.");
        if(DiameterZone&&(Target!=DrawingToleranceTarget.Axis||Characteristic is not (DrawingGeometricCharacteristic.Straightness or
            DrawingGeometricCharacteristic.Parallelism or DrawingGeometricCharacteristic.Perpendicularity or DrawingGeometricCharacteristic.Angularity or DrawingGeometricCharacteristic.Position)))
            throw new CadValidationException("A diameter zone requires a supported cylindrical axis tolerance.");
        if(MaterialCondition!=DrawingMaterialCondition.None&&!(Target==DrawingToleranceTarget.Axis&&DiameterZone&&
            Characteristic is DrawingGeometricCharacteristic.Straightness or DrawingGeometricCharacteristic.Parallelism or
            DrawingGeometricCharacteristic.Perpendicularity or DrawingGeometricCharacteristic.Angularity or DrawingGeometricCharacteristic.Position||
            Target==DrawingToleranceTarget.MedianPlane&&!DiameterZone&&Characteristic is DrawingGeometricCharacteristic.Flatness or
            DrawingGeometricCharacteristic.Parallelism or DrawingGeometricCharacteristic.Perpendicularity or DrawingGeometricCharacteristic.Angularity or DrawingGeometricCharacteristic.Position))
            throw new CadValidationException("Material conditions require a supported complete size axis or median plane and its appropriate zone.");
    }
}

/// <summary>An explicit occurrence is ballooned; identity is never inferred from a part name.</summary>
public sealed record DrawingAssemblyBalloon(Guid Id,Guid ViewId,DrawingTopologyReference Source,
    Point2d PositionMm,int ItemNumber,string? StaleReason=null)
{
    public int Quantity {get;init;}=1;
    public string PartName {get;init;}="Part";
    public void Validate(DocumentId document)
    {
        DrawingAnnotationGuard.Common(Id,ViewId,PositionMm,StaleReason);Source.Validate(document);
        if(ItemNumber is <1 or >9999||Quantity is <1 or >100000||string.IsNullOrWhiteSpace(PartName)||PartName.Length>128||PartName.Any(char.IsControl))
            throw new CadValidationException("Invalid assembly item number, quantity or part name.");
    }
}

public static class DrawingAssemblyParts
{
    /// <summary>Counts distinct instance paths represented by this sheet, across all views and bodies.</summary>
    public static int Quantity(DocumentSnapshot document,TechnicalDrawingSheet sheet,DefinitionId definition)
    {
        var paths=sheet.Views.SelectMany(v=>v.Sources).Select(s=>s.Path).ToHashSet();
        return document.EnumerateOccurrences().Count(o=>o.DefinitionId==definition&&paths.Contains(o.Path));
    }
}

internal static class DrawingAnnotationGuard
{
    internal static void Common(Guid id,Guid view,Point2d position,string? stale)
    {
        if(id==Guid.Empty||view==Guid.Empty||!double.IsFinite(position.X)||!double.IsFinite(position.Y)||
           Math.Abs(position.X)>1e6||Math.Abs(position.Y)>1e6||stale is {Length:>2048})
            throw new CadValidationException("Invalid manufacturing annotation identity or placement.");
    }
}
