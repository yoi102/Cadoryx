using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>The material boundary belongs to this reference, never to the datum label.</summary>
public sealed record DrawingDatumReference(Guid Id,DrawingMaterialCondition BoundaryCondition=DrawingMaterialCondition.None);
public sealed record DrawingDatumFeatureBinding(Guid Id,DrawingDatumFeatureType FeatureType,DrawingTopologyReference Source)
{
    public DrawingTopologyReference? OppositeSource {get;init;}
}
/// <summary>One precedence compartment. Several references establish one common datum.</summary>
public sealed record DrawingDatumGroup(ImmutableArray<DrawingDatumReference> References)
{
    public void Validate()
    {
        if(References.IsDefault||References.Length is <1 or >3||References.Any(r=>r is null||r.Id==Guid.Empty||!Enum.IsDefined(r.BoundaryCondition)))
            throw new CadValidationException("A datum precedence group requires one to three typed references.");
    }
    public bool SameReferences(DrawingDatumGroup other)=>References.SequenceEqual(other.References);
}

/// <summary>A conservative shared ISO/GB/ASME annotation subset, not a datum simulator or an inspection verdict.</summary>
public static class DrawingManufacturingRules
{
    public static ImmutableArray<DrawingDatumFeatureBinding> Bind(TechnicalDrawingSheet sheet,DrawingFeatureControlFrame frame)=>
        [..frame.ReferencedDatumIds.Select(id=>sheet.DatumSymbols.SingleOrDefault(d=>d.Id==id)??
            throw new CadValidationException("A control frame references a missing datum ID.")).Select(d=>new DrawingDatumFeatureBinding(d.Id,d.FeatureType,d.Source)
                {OppositeSource=d.PlanarSizeEvidence?.OppositeSource})];
    public static void ConfirmDatumBindings(TechnicalDrawingSheet sheet,DrawingFeatureControlFrame frame)
    {
        if(!frame.DatumBindings.SequenceEqual(Bind(sheet,frame)))
            throw new CadValidationException("A referenced datum feature changed; explicitly confirm the control frame.");
    }
    public static bool SameFeature(DrawingTopologyReference a,DrawingTopologyReference b)=>
        a.Path.Equals(b.Path)&&a.DefinitionId==b.DefinitionId&&a.BodyId==b.BodyId&&a.Asset==b.Asset&&a.Revision==b.Revision&&
        a.Kind==b.Kind&&a.FullTopologyIndex==b.FullTopologyIndex;

    public static void Validate(TechnicalDrawingSheet sheet,DrawingFeatureControlFrame frame)
    {
        if(frame.StaleReason is null)ConfirmDatumBindings(sheet,frame);
        foreach(var group in frame.OrderedDatumGroups)
        {
            var datums=group.References.Select(r=>sheet.DatumSymbols.SingleOrDefault(d=>d.Id==r.Id)??
                throw new CadValidationException("A control frame references a missing datum ID.")).ToArray();
            if(datums.Any(d=>!d.Source.Path.Equals(frame.Source.Path)||d.Source.DefinitionId!=frame.Source.DefinitionId))
                throw new CadValidationException("A datum system must belong to the same part occurrence as its controlled feature.");
            if(frame.StaleReason is null&&datums.Any(d=>FeatureSources(d.Source,d.PlanarSizeEvidence).Any(s=>
                FeatureSources(frame.Source,frame.PlanarSizeEvidence).Any(f=>SameFeature(s,f)))))
                throw new CadValidationException("A controlled feature cannot reference itself as a datum.");
            if(group.References.Where((r,i)=>r.BoundaryCondition!=DrawingMaterialCondition.None&&
                datums[i].FeatureType is not (DrawingDatumFeatureType.CylinderSizeAxis or DrawingDatumFeatureType.PlanarSizeMedianPlane)).Any())
                throw new CadValidationException("Material boundary modifiers require a complete size datum axis or median plane.");
            if(datums.Select(d=>d.FeatureType).Distinct().Count()>1)
                throw new CadValidationException("Mixed surface and size-axis common datums are not supported.");
            if(frame.StaleReason is null&&datums.Length>1&&datums[0].FeatureType==DrawingDatumFeatureType.CylinderSizeAxis)
            {
                var first=datums[0].SizeEvidence??throw new CadValidationException("Common axis datum proof is missing.");
                if(datums.Skip(1).Select(d=>d.SizeEvidence??throw new CadValidationException("Common axis datum proof is missing."))
                    .Any(s=>first.Direction.Cross(s.Direction).Length>1e-8||(s.Origin-first.Origin).Cross(first.Direction).Length>1e-6))
                    throw new CadValidationException("Common cylindrical datum axes must be coaxial in this supported subset.");
            }
            if(frame.StaleReason is null&&datums.Length>1&&datums[0].FeatureType==DrawingDatumFeatureType.PlanarSizeMedianPlane)
            {
                var first=datums[0].PlanarSizeEvidence??throw new CadValidationException("Common median-plane proof is missing.");
                if(datums.Skip(1).Select(d=>d.PlanarSizeEvidence??throw new CadValidationException("Common median-plane proof is missing."))
                    .Any(s=>first.Direction.Cross(s.Direction).Length>1e-8||Math.Abs((s.Origin-first.Origin).Dot(first.Direction))>1e-6))
                    throw new CadValidationException("Common size median planes must be coplanar in this supported subset.");
            }
        }
        var all=frame.ReferencedDatumIds.Select(id=>sheet.DatumSymbols.Single(d=>d.Id==id)).ToArray();
        for(int i=0;i<all.Length;i++)for(int j=0;j<i;j++)
            if(frame.StaleReason is null&&FeatureSources(all[i].Source,all[i].PlanarSizeEvidence).Any(a=>
                FeatureSources(all[j].Source,all[j].PlanarSizeEvidence).Any(b=>SameFeature(a,b))))
                throw new CadValidationException("Different datum labels cannot repeat either original face of a size feature in a datum system.");
        // Surface flatness and cylindrical form are distinct from derived size-axis tolerances.
        if(frame.StaleReason is null&&frame.Target==DrawingToleranceTarget.Surface&&
            (frame.Characteristic==DrawingGeometricCharacteristic.Flatness&&frame.Source.Geometry!=DrawingAttachmentGeometry.PlaneFace||
             frame.Characteristic==DrawingGeometricCharacteristic.Cylindricity&&frame.Source.Geometry!=DrawingAttachmentGeometry.CylinderFace||
             frame.Characteristic==DrawingGeometricCharacteristic.Circularity&&frame.Source.Geometry is not (DrawingAttachmentGeometry.CylinderFace or DrawingAttachmentGeometry.CircleEdge)))
            throw new CadValidationException("The selected surface does not support this form characteristic.");
        if(frame.StaleReason is null&&frame.Characteristic is DrawingGeometricCharacteristic.CircularRunout or DrawingGeometricCharacteristic.TotalRunout&&
            !all.Any(d=>d.FeatureType==DrawingDatumFeatureType.CylinderSizeAxis))
            throw new CadValidationException("Runout requires a complete cylindrical datum axis in this supported subset.");
    }
    private static IEnumerable<DrawingTopologyReference> FeatureSources(DrawingTopologyReference source,DrawingPlanarSizeEvidence? planar)=>
        planar is null?[source]:[source,planar.OppositeSource];
}

/// <summary>UI syntax: commas separate precedence; hyphens join a common datum; (M)/(L) modify an individual reference.</summary>
public static class DrawingDatumSystemText
{
    public static ImmutableArray<DrawingDatumGroup> Parse(string text,IEnumerable<DrawingDatumSymbol> symbols)
    {
        if(string.IsNullOrWhiteSpace(text))return [];
        if(text.Length>256)throw new CadValidationException("Datum system text is too long.");
        var labels=symbols.ToDictionary(d=>d.Name,StringComparer.Ordinal);
        DrawingDatumReference Reference(string token)
        {
            token=token.Trim();var condition=DrawingMaterialCondition.None;
            if(token.EndsWith("(M)",StringComparison.Ordinal)){condition=DrawingMaterialCondition.Maximum;token=token[..^3];}
            else if(token.EndsWith("(L)",StringComparison.Ordinal)){condition=DrawingMaterialCondition.Least;token=token[..^3];}
            if(!labels.TryGetValue(token,out var datum))throw new CadValidationException("Unknown datum reference: "+token);
            return new(datum.Id,condition);
        }
        var groups=text.Split(',').Select(p=>new DrawingDatumGroup([..p.Split('-').Select(Reference)])).ToImmutableArray();
        if(groups.Length>3)throw new CadValidationException("At most three datum precedence groups are supported.");
        foreach(var group in groups)group.Validate();
        return groups;
    }
    public static string Format(DrawingFeatureControlFrame frame,IEnumerable<DrawingDatumSymbol> symbols)
    {
        var labels=symbols.ToDictionary(d=>d.Id,d=>d.Name);
        return string.Join(",",frame.OrderedDatumGroups.Select(g=>string.Join("-",g.References.Select(r=>
            labels[r.Id]+(r.BoundaryCondition switch{DrawingMaterialCondition.Maximum=>"(M)",DrawingMaterialCondition.Least=>"(L)",_=>""})))));
    }
}
