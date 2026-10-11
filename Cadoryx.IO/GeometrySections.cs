using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackGeometryTable([property:Key(0)] PackGeometry[] Items);
[MessagePackObject]
public sealed record PackStructureV3([property:Key(0)] PackDefinition[] Definitions,[property:Key(1)] PackBodyV3[] Bodies);
[MessagePackObject]
public sealed record PackBodyV3([property:Key(0)] Guid Id,[property:Key(1)] Guid Part,[property:Key(2)] string Name,
    [property:Key(3)] Guid Geometry,[property:Key(4)] Guid? Producer,[property:Key(5)] Guid Layer,
    [property:Key(6)] PackAppearance Appearance,[property:Key(7)] bool Visible,[property:Key(8)] Guid? Material);
[MessagePackObject]
public sealed record PackFeaturesV3([property:Key(0)] PackFeatureV3[] Features);
[MessagePackObject]
public sealed record PackFeatureV3([property:Key(0)] Guid Id,[property:Key(1)] Guid Part,[property:Key(2)] string Name,
    [property:Key(3)] PackRecipeV3 Recipe,[property:Key(4)] Guid[] Inputs,[property:Key(5)] Guid Output,
    [property:Key(6)] Guid Result,[property:Key(7)] int Version,[property:Key(8)] PackOutputMetadata? OutputMetadata,
    [property:Key(9)] PackSketchProfileReference? SketchSource=null,[property:Key(10)] bool IsStale=false,
    [property:Key(11)] bool IsSuppressed=false,[property:Key(12)] PackRepetitionPresentation? RepetitionPresentation=null);
[MessagePackObject]
public sealed record PackRepetitionPresentation([property:Key(0)] string SourceFingerprint,[property:Key(1)] string ResultFingerprint,
    [property:Key(2)] int SourceTopologyCount,[property:Key(3)] int ResultTopologyCount,[property:Key(4)] int StyledSourceSlots,
    [property:Key(5)] int CopiedResultSlots,[property:Key(6)] int SharedResultSlots,[property:Key(7)] bool CompleteHistory,
    [property:Key(8)] int[] Conflicts);
[MessagePackObject]
public sealed record PackSketchProfileReference([property:Key(0)] Guid Sketch,[property:Key(1)] Guid Revision,[property:Key(2)] Guid[] Lines,
    [property:Key(3)] Guid? Circle=null,[property:Key(4)] Guid[]? Holes=null,[property:Key(5)] Guid[][]? PolygonHoles=null,
    [property:Key(6)] Guid? Arc=null,[property:Key(7)] Guid[]? MixedBoundary=null,
    [property:Key(8)] Guid[][]? MixedHoles=null,[property:Key(9)] Guid[]? IslandCircles=null,
    [property:Key(10)] Guid[][]? IslandPolygons=null,[property:Key(11)] Guid[][]? IslandMixed=null,
    [property:Key(12)] Guid? Bezier=null,[property:Key(13)] Guid? Spline=null,
    [property:Key(14)] Guid[]? HoleSplines=null,
    [property:Key(15)] PackSketchRegionReference[]? Regions=null);
[MessagePackObject]
public sealed record PackSketchRegionReference([property:Key(0)] Guid? Circle,
    [property:Key(1)] Guid[]? Polygon,[property:Key(2)] Guid[]? Mixed,
    [property:Key(3)] Guid? Spline,[property:Key(4)] PackSketchRegionReference[]? Children);
[MessagePackObject]
public sealed record PackRecipeV3([property:Key(0)] string Kind,[property:Key(1)] double[] Numbers,
    [property:Key(2)] PackTransform? Placement,[property:Key(3)] Guid[] Sources,[property:Key(4)] double[][] Profile,[property:Key(5)] int Operation,
    [property:Key(6)] PackCircularHole[]? Holes=null,[property:Key(7)] PackPolygonHole[]? PolygonHoles=null,
    [property:Key(8)] PackMixedCurve[]? MixedCurves=null,[property:Key(9)] PackMixedCurve[][]? MixedHoles=null,
    [property:Key(10)] PackIsland[]? Islands=null,[property:Key(11)] double[][]? SplineControls=null,
    [property:Key(12)] double[][][]? SplineHoles=null,[property:Key(13)] PackRepetition? Repetition=null,[property:Key(14)] PackHole? Hole=null,
    [property:Key(15)] PackExtrusion? Extrusion=null,[property:Key(16)] PackGuidedAuthoring? Guided=null,[property:Key(17)] PackFaceFeature? FaceFeature=null,
    [property:Key(18)] PackRevolveAxis? RevolveAxis=null,[property:Key(19)] PackHolePattern? HolePattern=null,
    [property:Key(20)] PackExtrusionPattern? ExtrusionPattern=null,
    [property:Key(21)] PackSplineKnots? SplineKnots=null,[property:Key(22)] PackSplineKnots?[]? SplineHoleKnots=null,
    [property:Key(23)] PackContourLocal? ContourLocal=null,[property:Key(24)] PackFaceEdit? FaceEdit=null);

internal static partial class MessagePackSections
{
    internal static bool HasGeometryReferences(ReadOnlyMemory<byte> bytes)=>Read<PackGeometryTable>(bytes).Items.Any(g=>g.SerializedReferences is not null);
    private static GeometryAssetRef AttachReferenceFormat(GeometryAssetRef geometry,IReadOnlyDictionary<AssetId,AssetFormat> formats)
    {
        if(geometry.SerializedReferences is not {} references)return geometry;
        if(!formats.TryGetValue(references.TableAssetId,out var format))throw new InvalidDataException("Native reference table missing from manifest.");
        AssetFormatPolicy.RequireReferenceTable(format);return geometry with{SerializedReferences=references with{Format=format}};
    }
    internal static ReadOnlyMemory<byte> UpgradeGeometryReferences(ReadOnlyMemory<byte> bytes)
    {
        var table=Read<PackGeometryTable>(bytes);
        if(table.Items.Any(g=>g.SerializedReferences is not null))throw new InvalidDataException("Native references cannot appear under legacy geometry schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeLocalFeatures(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.Recipe.Kind=="local-box-edge"))throw new InvalidDataException("Local feature in legacy schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeLocalChamferTwoDistances(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        return Serialize(old with{Features=old.Features.Select(f=>
        {
            if(f.Recipe.Kind!="local-box-edge")return f;
            if(f.Recipe.Numbers.Length!=6)throw new InvalidDataException("Invalid legacy local feature parameter count.");
            return f with{Recipe=f.Recipe with{Numbers=[..f.Recipe.Numbers,0]}};
        }).ToArray()});
    }
    internal static ReadOnlyMemory<byte> UpgradeLocalMultiEdgeAndVariableRadius(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        return Serialize(old with{Features=old.Features.Select(f=>
        {
            if(f.Recipe.Kind!="local-box-edge")return f;
            if(f.Recipe.Numbers.Length!=7)throw new InvalidDataException("Invalid v7 local feature parameter count.");
            return f with{Recipe=f.Recipe with{Numbers=[..f.Recipe.Numbers,0,0]}};
        }).ToArray()});
    }
    internal static ReadOnlyMemory<byte> UpgradeBoundVariableRadius(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        return Serialize(old with{Features=old.Features.Select(f=>
        {
            if(f.Recipe.Kind!="history-edge-fillet")return f;
            if(f.Recipe.Numbers.Length!=2)throw new InvalidDataException("Invalid v8 bound fillet parameter count.");
            return f with{Recipe=f.Recipe with{Numbers=[..f.Recipe.Numbers,0]}};
        }).ToArray()});
    }
    internal static IReadOnlyDictionary<AssetId,string> RequiredAssetRoles(SectionPayload geometrySection)
    {
        var roles=new Dictionary<AssetId,string>();
        foreach(var item in Read<PackGeometryTable>(geometrySection.Bytes).Items)
        {
            var geometry=G(item);geometry.Validate();
            Add(geometry.AssetId,AssetFormat.BRepMediaType);
            if(geometry.SerializedReferences is {} references)Add(references.TableAssetId,AssetFormat.ReferenceTableMediaType);
            if(geometry.Source is {} source)Add(source.ContextAssetId,AssetFormat.XdeMediaType);
        }
        return roles;
        void Add(AssetId id,string role)
        {
            if(roles.TryGetValue(id,out var existing)&&existing!=role)throw new InvalidDataException("Conflicting native asset roles.");
            roles[id]=role;
        }
    }

    // V3 changes geometry fields from embedded records to revision IDs; V2 contracts remain intact.
    internal static SectionPayload[] SplitGeometry(StructureSection structure,FeaturesSection features,int? featureVersion=null,IEnumerable<GeometryAssetRef>? datumGeometries=null)
    {
        var table=new Dictionary<GeometryRevisionId,GeometryAssetRef>();
        Guid Reference(GeometryAssetRef geometry)
        {
            geometry.Validate();
            if(table.TryGetValue(geometry.Revision,out var previous)&&previous!=geometry)
                throw new InvalidDataException("Conflicting records for one geometry revision.");
            table[geometry.Revision]=geometry;return geometry.Revision.Value;
        }
        var bodies=structure.Bodies.Select(b=>new PackBodyV3(b.Id.Value,b.PartId.Value,b.Name,Reference(b.Geometry),
            b.Producer?.Value,b.LayerId.Value,A(b.Appearance),b.IsVisible,b.MaterialId?.Value)).ToArray();
        var featureRecords=features.Features.Select(f=>
        {
            var recipe=R(f.Recipe);var metadata=F(f).OutputMetadata;
            return new PackFeatureV3(f.Id.Value,f.PartId.Value,f.Name,
                new(recipe.Kind,recipe.Numbers,recipe.Placement,f.Recipe.AssetInputs.Select(Reference).ToArray(),recipe.Profile,recipe.Operation,recipe.Holes,recipe.PolygonHoles,recipe.MixedCurves,recipe.MixedHoles,recipe.Islands,recipe.SplineControls,recipe.SplineHoles,recipe.Repetition,recipe.Hole,recipe.Extrusion,recipe.Guided,recipe.FaceFeature,recipe.RevolveAxis,recipe.HolePattern,recipe.ExtrusionPattern,recipe.SplineKnots,recipe.SplineHoleKnots,recipe.ContourLocal,recipe.FaceEdit),
                f.Inputs.Select(i=>i.Value).ToArray(),f.OutputBodyId.Value,Reference(f.Result),f.SchemaVersion,metadata,
                f.SketchSource is {} source?new(source.SketchId.Value,source.Revision,source.Lines.Select(l=>l.Value).ToArray(),source.CircleId?.Value,
                    source.HoleCircleIds.IsDefaultOrEmpty?null:source.HoleCircleIds.Select(id=>id.Value).ToArray(),
                    source.PolygonHoleLines.IsDefaultOrEmpty?null:source.PolygonHoleLines.Select(h=>h.Select(id=>id.Value).ToArray()).ToArray(),
                    source.ArcId?.Value,source.MixedBoundaryIds.IsEmpty?null:source.MixedBoundaryIds.Select(id=>id.Value).ToArray(),
                    source.MixedHoleIds.IsDefaultOrEmpty?null:source.MixedHoleIds.Select(h=>h.Select(id=>id.Value).ToArray()).ToArray(),
                    source.IslandCircleIds.IsDefaultOrEmpty?null:source.IslandCircleIds.Select(id=>id.Value).ToArray(),
                    source.IslandPolygonLines.IsDefaultOrEmpty?null:source.IslandPolygonLines.Select(h=>h.Select(id=>id.Value).ToArray()).ToArray(),
                    source.IslandMixedIds.IsDefaultOrEmpty?null:source.IslandMixedIds.Select(h=>h.Select(id=>id.Value).ToArray()).ToArray(),
                    source.BezierId?.Value,source.SplineId?.Value,
                    source.HoleSplineIds.IsDefaultOrEmpty?null:source.HoleSplineIds.Select(id=>id.Value).ToArray(),
                    source.Regions.IsDefaultOrEmpty?null:source.Regions.Select(PackRegion).ToArray()):null,f.IsStale,f.IsSuppressed,
                f.RepetitionPresentation is {} proof?new(proof.SourceFingerprint,proof.ResultFingerprint,proof.SourceTopologyCount,proof.ResultTopologyCount,
                    proof.StyledSourceSlots,proof.CopiedResultSlots,proof.SharedResultSlots,proof.CompleteHistory,proof.ConflictingResultIndices.ToArray()):null);
        }).ToArray();
        foreach(var geometry in datumGeometries??[])Reference(geometry);
        return [new(new("structure",3,"messagepack"),Serialize(new PackStructureV3(structure.Definitions.Select(D).ToArray(),bodies))),
            new(new("features",featureVersion??CadSectionMigrationRegistry.CurrentFormats["features"].Version,"messagepack"),Serialize(new PackFeaturesV3(featureRecords))),
            new(CadSectionMigrationRegistry.CurrentFormats["geometry"],Serialize(new PackGeometryTable(table.Values.OrderBy(g=>g.Revision.Value).Select(G).ToArray())))];
    }
    internal static (StructureSection Structure,FeaturesSection Features) JoinGeometry(IReadOnlyDictionary<string,SectionPayload> sections,
        IReadOnlyDictionary<AssetId,AssetFormat> formats)
    {
        var table=new Dictionary<Guid,GeometryAssetRef>();
        foreach(var item in Read<PackGeometryTable>(sections["geometry"].Bytes).Items)
        {
            var geometry=G(item);
            if(geometry.SerializedReferences is {} references)
            {
                if(!formats.TryGetValue(references.TableAssetId,out var referenceFormat))throw new InvalidDataException("Native reference table is missing from the manifest.");
                AssetFormatPolicy.RequireReferenceTable(referenceFormat);
                geometry=geometry with{SerializedReferences=references with{Format=referenceFormat}};
            }
            if(!formats.TryGetValue(geometry.AssetId,out var format))throw new InvalidDataException("Geometry asset is missing from the manifest.");
            XdeSourceRef? source=geometry.Source;
            if(source is not null)
            {
                if(!formats.TryGetValue(source.ContextAssetId,out var sourceFormat))throw new InvalidDataException("Source XDE asset is missing from the manifest.");
                AssetFormatPolicy.RequireXde(sourceFormat);source=source with{Format=sourceFormat};
            }
            AssetFormatPolicy.RequireBRep(format);geometry=geometry with{Format=format,Source=source};geometry.Validate();
            if(!table.TryAdd(item.Revision,geometry))throw new InvalidDataException("Duplicate geometry revision.");
        }
        var used=new HashSet<Guid>();
        GeometryAssetRef Resolve(Guid id)
        {
            if(!table.TryGetValue(id,out var geometry))throw new InvalidDataException("Unresolved geometry revision.");
            used.Add(id);return geometry;
        }
        var s=Read<PackStructureV3>(sections["structure"].Bytes);
        var bodies=s.Bodies.Select(b=>new CadBody(new(b.Id),new(b.Part),b.Name,Resolve(b.Geometry),b.Producer is {} p?new FeatureId(p):null,
            new(b.Layer),A(b.Appearance),b.Visible,b.Material is {} m?new MaterialId(m):null)).ToImmutableArray();
        var f=Read<PackFeaturesV3>(sections["features"].Bytes);
        if(f.Features.Any(v=>v.SketchSource?.PolygonHoles?.Any(h=>h is null)==true||
            v.SketchSource?.MixedHoles?.Any(h=>h is null)==true||
            v.SketchSource?.IslandPolygons?.Any(h=>h is null)==true||v.SketchSource?.IslandMixed?.Any(h=>h is null)==true))
            throw new InvalidDataException("Malformed polygon hole line references.");
        var features=f.Features.Select(v=>
        {
            var r=v.Recipe;
            // Reuse the audited recipe whitelist and parameter validation, then restore shared references.
            var inputs=r.Sources.Select(Resolve).ToArray();
            var recipe=R(new PackRecipe(r.Kind,r.Numbers,r.Placement,inputs.Select(G).ToArray(),r.Profile,r.Operation,r.Holes,r.PolygonHoles,r.MixedCurves,r.MixedHoles,r.Islands,r.SplineControls,r.SplineHoles,r.Repetition,r.Hole,r.Extrusion,r.Guided,r.FaceFeature,r.RevolveAxis,r.HolePattern,r.ExtrusionPattern,r.SplineKnots,r.SplineHoleKnots,r.ContourLocal,r.FaceEdit));
            recipe=recipe switch
            {
                LocalFeatureRecipe l=>l with{Source=inputs[0]},ImportedRecipe i=>i with{Source=inputs[0]},TransformRecipe t=>t with{Source=inputs[0]},
                RepetitionRecipe repeat=>repeat with{Source=inputs[0]},
                HoleRecipe hole=>hole with{Source=inputs[0]},
                HolePatternRecipe pattern=>pattern with{Layout=pattern.Layout with{Source=inputs[0]},Template=pattern.Template with{Source=inputs[1]}},
                ExtrusionPatternRecipe pattern=>pattern with{Layout=pattern.Layout with{Source=inputs[0]},Template=pattern.Template with{Options=pattern.Template.Options! with
                    {Source=inputs[1],LimitGeometry=pattern.Template.Options!.LimitGeometry is null?null:inputs[2]}}},
                ShellRecipe shell=>shell with{Source=inputs[0]},DraftRecipe draft=>draft with{Source=inputs[0]},
                SolidOffsetRecipe offset=>offset with{Source=inputs[0]},
                ContourLocalRecipe contour=>contour with{Source=inputs[0]},
                PlanarPushPullRecipe pushPull=>pushPull with{Source=inputs[0]},DefeatureRecipe defeature=>defeature with{Source=inputs[0]},
                ExtrudeRecipe {Options:{} options} extrusion=>extrusion with{Options=options with
                    {Source=options.Source is null?null:inputs[0],LimitGeometry=options.LimitGeometry is null?null:inputs[^1]}},
                BooleanRecipe b=>b with{Inputs=inputs.ToImmutableArray()},HistoryFilletRecipe h=>h with{Source=inputs[0]},
                HistoryChamferRecipe h=>h with{Source=inputs[0]},_=>recipe
            };
            var metadata=v.OutputMetadata;
            return new FeatureDefinition(new(v.Id),new(v.Part),v.Name,recipe,v.Inputs.Select(i=>new FeatureId(i)).ToImmutableArray(),new(v.Output),
                Resolve(v.Result),v.Version,metadata is null?null:new(metadata.Name,new(metadata.Layer),A(metadata.Appearance),metadata.Visible,metadata.Material is {} m?new MaterialId(m):null))
                {SketchSource=v.SketchSource is {} source?new(new(source.Sketch),source.Revision,source.Lines.Select(l=>new SketchEntityId(l)).ToImmutableArray(),
                    source.Circle is {} circle?new SketchEntityId(circle):null,
                    source.Holes is null?default:source.Holes.Select(id=>new SketchEntityId(id)).ToImmutableArray(),
                    source.PolygonHoles is null?default:source.PolygonHoles.Select(h=>h.Select(id=>new SketchEntityId(id)).ToImmutableArray()).ToImmutableArray())
                    {ArcId=source.Arc is {} arc?new SketchEntityId(arc):null,
                        MixedBoundaryIds=source.MixedBoundary is null?[]:source.MixedBoundary.Select(id=>new SketchEntityId(id)).ToImmutableArray(),
                        MixedHoleIds=source.MixedHoles is null?[]:source.MixedHoles.Select(h=>h.Select(id=>new SketchEntityId(id)).ToImmutableArray()).ToImmutableArray(),
                        IslandCircleIds=source.IslandCircles is null?[]:source.IslandCircles.Select(id=>new SketchEntityId(id)).ToImmutableArray(),
                        IslandPolygonLines=source.IslandPolygons is null?[]:source.IslandPolygons.Select(h=>h.Select(id=>new SketchEntityId(id)).ToImmutableArray()).ToImmutableArray(),
                        IslandMixedIds=source.IslandMixed is null?[]:source.IslandMixed.Select(h=>h.Select(id=>new SketchEntityId(id)).ToImmutableArray()).ToImmutableArray(),
                        BezierId=source.Bezier is {} bezier?new SketchEntityId(bezier):null,
                        SplineId=source.Spline is {} spline?new SketchEntityId(spline):null,
                        HoleSplineIds=source.HoleSplines is null?[]:source.HoleSplines.Select(id=>new SketchEntityId(id)).ToImmutableArray(),
                        Regions=source.Regions is null?[]:source.Regions.Select(region=>UnpackRegion(region)).ToImmutableArray()}:null,
                IsStale=v.IsStale,IsSuppressed=v.IsSuppressed,
                RepetitionPresentation=v.RepetitionPresentation is {} proof?new(proof.SourceFingerprint,proof.ResultFingerprint,proof.SourceTopologyCount,
                    proof.ResultTopologyCount,proof.StyledSourceSlots,proof.CopiedResultSlots,proof.SharedResultSlots,proof.CompleteHistory,
                    (proof.Conflicts??throw new InvalidDataException("Missing presentation conflict indices.")).ToImmutableArray()):null};
        }).ToImmutableArray();
        if(sections.TryGetValue("datums",out var datums))
            foreach(var inline in PackedDatumGeometries(datums.Bytes))
            {var referenced=Resolve(inline.Revision);if(G(G(referenced))!=G(inline))throw new InvalidDataException("Exact datum geometry differs from its geometry table record.");}
        if(sections.TryGetValue("sketches",out var sketches))
            foreach(var inline in Read<PackSketches>(sketches.Bytes).Sketches.SelectMany(s=>(s.NativeProjections??[]).Select(p=>p.Source)))
            {var referenced=Resolve(inline.Revision);if(G(G(referenced))!=G(inline))throw new InvalidDataException("Native sketch projection geometry differs from its geometry table record.");}
        if(used.Count!=table.Count)throw new InvalidDataException("Unreferenced geometry table records.");
        return(new(s.Definitions.Select(D).ToImmutableArray(),bodies),new(features));
    }

    internal static ReadOnlyMemory<byte> UpgradeSketchFeatureReferences(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.SketchSource is not null))
            throw new InvalidDataException("Unexpected sketch association in legacy features.");
        return bytes; // V4 adds optional Key(9); absence explicitly means a frozen recipe.
    }
    internal static ReadOnlyMemory<byte> UpgradeFeatureSuppression(ReadOnlyMemory<byte> bytes)
    {
        var features=Read<PackFeaturesV3>(bytes);
        if(features.Features is null||features.Features.Any(f=>f.IsSuppressed))
            throw new InvalidDataException("Feature suppression in legacy schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeAnalyticalRevolve(ReadOnlyMemory<byte> bytes)
    {
        var features=Read<PackFeaturesV3>(bytes);
        if(features.Features is null||features.Features.Any(f=>f.Recipe.Kind is "revolve-circle" or "revolve-arc" or "revolve-mixed"))
            throw new InvalidDataException("Analytical revolve recipe in legacy features schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeRepetition(ReadOnlyMemory<byte> bytes)
    {
        var features=Read<PackFeaturesV3>(bytes);
        if(features.Features is null||features.Features.Any(f=>f.Recipe.Kind=="repetition"||f.Recipe.Repetition is not null))
            throw new InvalidDataException("Repetition recipe in legacy features schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeHoles(ReadOnlyMemory<byte> bytes)
    {
        var features=Read<PackFeaturesV3>(bytes);
        if(features.Features is null||features.Features.Any(f=>f.Recipe.Kind=="hole"||f.Recipe.Hole is not null))
            throw new InvalidDataException("Hole recipe in legacy features schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeCircularSketchProfiles(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        if(old.Features.Any(f=>f.Recipe.Kind=="extrude-circle"||f.SketchSource?.Circle is not null))
            throw new InvalidDataException("Circular sketch profile in legacy features schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeCircularHoles(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        if(old.Features.Any(f=>f.Recipe.Holes is not null||f.SketchSource?.Holes is not null))
            throw new InvalidDataException("Circular holes in legacy features schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradePolygonHoles(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        if(old.Features.Any(f=>f.Recipe.PolygonHoles is not null||f.SketchSource?.PolygonHoles is not null))
            throw new InvalidDataException("Polygon holes in legacy features schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeArcSegmentFeatures(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        if(old.Features.Any(f=>f.Recipe.Kind=="extrude-arc"||f.SketchSource?.Arc is not null))
            throw new InvalidDataException("Arc segment in legacy features schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeMixedCurveFeatures(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        if(old.Features.Any(f=>f.Recipe.Kind=="extrude-mixed"||f.Recipe.MixedCurves is not null||f.SketchSource?.MixedBoundary is not null))
            throw new InvalidDataException("Mixed boundary in legacy features schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeExpandedMixedFeatures(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        if(old.Features.Any(f=>f.Recipe.MixedHoles is not null||f.SketchSource?.MixedHoles is not null||
            f.Recipe.MixedCurves?.Count(c=>c.Middle is not null)>1))
            throw new InvalidDataException("Expanded mixed profile in legacy features schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeIslandFeatures(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        if(old.Features.Any(f=>f.Recipe.Islands is not null||f.SketchSource?.IslandCircles is not null||
            f.SketchSource?.IslandPolygons is not null||f.SketchSource?.IslandMixed is not null))
            throw new InvalidDataException("Islands in legacy features schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeBezierFeatures(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        if(old.Features.Any(f=>f.Recipe.Kind=="extrude-bezier"||f.SketchSource?.Bezier is not null))
            throw new InvalidDataException("Bezier feature in legacy schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSplineFeatures(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        if(old.Features.Any(f=>f.Recipe.Kind=="extrude-spline"||f.Recipe.SplineControls is not null||f.SketchSource?.Spline is not null))
            throw new InvalidDataException("Spline feature in legacy schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSplineHoleFeatures(ReadOnlyMemory<byte> bytes)
    {
        static bool NewCurve(PackMixedCurve c)=>c.BezierControl is not null||c.SplineControls is not null;
        static bool NewIsland(PackIsland i)=>i.SplineControls is not null||i.Holes is not null||
            i.PolygonHoles is not null||i.MixedHoles is not null||i.SplineHoles is not null||i.Islands is not null||
            i.MixedCurves?.Any(NewCurve)==true;
        var old=Read<PackFeaturesV3>(bytes);
        if(old.Features.Any(f=>f.Recipe.SplineHoles is not null||f.SketchSource?.HoleSplines is not null||
            f.SketchSource?.Regions is not null||
            f.Recipe.MixedCurves?.Any(NewCurve)==true||
            f.Recipe.MixedHoles?.Any(h=>h.Any(NewCurve))==true||
            f.Recipe.Islands?.Any(NewIsland)==true))
            throw new InvalidDataException("Spline hole in legacy features schema.");
        return Serialize(old);
    }
    private static PackSketchRegionReference PackRegion(SketchRegionReference r)=>new(r.CircleId?.Value,
        r.PolygonLineIds.IsDefaultOrEmpty?null:r.PolygonLineIds.Select(id=>id.Value).ToArray(),
        r.MixedCurveIds.IsDefaultOrEmpty?null:r.MixedCurveIds.Select(id=>id.Value).ToArray(),
        r.SplineId?.Value,r.Children.IsDefaultOrEmpty?null:r.Children.Select(PackRegion).ToArray());
    private static SketchRegionReference UnpackRegion(PackSketchRegionReference? r)=>UnpackRegion(r,0);
    private static SketchRegionReference UnpackRegion(PackSketchRegionReference? r,int depth)
    {
        if(r is null||depth>12||r.Polygon is {Length:>256}||r.Mixed is {Length:>256}||
           r.Children is {Length:>64})throw new InvalidDataException("Malformed nested sketch region.");
        return new(r.Circle is {} c?new SketchEntityId(c):null,
            r.Polygon is null?[]:r.Polygon.Select(id=>new SketchEntityId(id)).ToImmutableArray(),
            r.Mixed is null?[]:r.Mixed.Select(id=>new SketchEntityId(id)).ToImmutableArray(),
            r.Spline is {} s?new SketchEntityId(s):null,
            r.Children is null?[]:r.Children.Select(child=>UnpackRegion(child,depth+1)).ToImmutableArray());
    }
}
