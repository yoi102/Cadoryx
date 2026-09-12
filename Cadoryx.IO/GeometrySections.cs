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
    [property:Key(9)] PackSketchProfileReference? SketchSource=null);
[MessagePackObject]
public sealed record PackSketchProfileReference([property:Key(0)] Guid Sketch,[property:Key(1)] Guid Revision,[property:Key(2)] Guid[] Lines);
[MessagePackObject]
public sealed record PackRecipeV3([property:Key(0)] string Kind,[property:Key(1)] double[] Numbers,
    [property:Key(2)] PackTransform? Placement,[property:Key(3)] Guid[] Sources,[property:Key(4)] double[][] Profile,[property:Key(5)] int Operation);

internal static partial class MessagePackSections
{
    internal static ReadOnlyMemory<byte> UpgradeLocalFeatures(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.Recipe.Kind=="local-box-edge"))throw new InvalidDataException("Local feature in legacy schema.");
        return bytes;
    }
    internal static IReadOnlyDictionary<AssetId,string> RequiredAssetRoles(SectionPayload geometrySection)
    {
        var roles=new Dictionary<AssetId,string>();
        foreach(var item in Read<PackGeometryTable>(geometrySection.Bytes).Items)
        {
            var geometry=G(item);geometry.Validate();
            Add(geometry.AssetId,AssetFormat.BRepMediaType);
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
    internal static SectionPayload[] SplitGeometry(StructureSection structure,FeaturesSection features,int featureVersion=5)
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
                new(recipe.Kind,recipe.Numbers,recipe.Placement,f.Recipe.AssetInputs.Select(Reference).ToArray(),recipe.Profile,recipe.Operation),
                f.Inputs.Select(i=>i.Value).ToArray(),f.OutputBodyId.Value,Reference(f.Result),f.SchemaVersion,metadata,
                f.SketchSource is {} source?new(source.SketchId.Value,source.Revision,source.Lines.Select(l=>l.Value).ToArray()):null);
        }).ToArray();
        return [new(new("structure",3,"messagepack"),Serialize(new PackStructureV3(structure.Definitions.Select(D).ToArray(),bodies))),
            new(new("features",featureVersion,"messagepack"),Serialize(new PackFeaturesV3(featureRecords))),
            new(new("geometry",1,"messagepack"),Serialize(new PackGeometryTable(table.Values.OrderBy(g=>g.Revision.Value).Select(G).ToArray())))];
    }
    internal static (StructureSection Structure,FeaturesSection Features) JoinGeometry(IReadOnlyDictionary<string,SectionPayload> sections,
        IReadOnlyDictionary<AssetId,AssetFormat> formats)
    {
        var table=new Dictionary<Guid,GeometryAssetRef>();
        foreach(var item in Read<PackGeometryTable>(sections["geometry"].Bytes).Items)
        {
            var geometry=G(item);
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
        var features=f.Features.Select(v=>
        {
            var r=v.Recipe;
            // Reuse the audited recipe whitelist and parameter validation, then restore shared references.
            var inputs=r.Sources.Select(Resolve).ToArray();
            var recipe=R(new PackRecipe(r.Kind,r.Numbers,r.Placement,inputs.Select(G).ToArray(),r.Profile,r.Operation));
            recipe=recipe switch
            {
                LocalFeatureRecipe l=>l with{Source=inputs[0]},ImportedRecipe i=>i with{Source=inputs[0]},TransformRecipe t=>t with{Source=inputs[0]},
                BooleanRecipe b=>b with{Inputs=inputs.ToImmutableArray()},_=>recipe
            };
            var metadata=v.OutputMetadata;
            return new FeatureDefinition(new(v.Id),new(v.Part),v.Name,recipe,v.Inputs.Select(i=>new FeatureId(i)).ToImmutableArray(),new(v.Output),
                Resolve(v.Result),v.Version,metadata is null?null:new(metadata.Name,new(metadata.Layer),A(metadata.Appearance),metadata.Visible,metadata.Material is {} m?new MaterialId(m):null))
                {SketchSource=v.SketchSource is {} source?new(new(source.Sketch),source.Revision,source.Lines.Select(l=>new SketchEntityId(l)).ToImmutableArray()):null};
        }).ToImmutableArray();
        if(used.Count!=table.Count)throw new InvalidDataException("Unreferenced geometry table records.");
        return(new(s.Definitions.Select(D).ToImmutableArray(),bodies),new(features));
    }

    internal static ReadOnlyMemory<byte> UpgradeSketchFeatureReferences(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.SketchSource is not null))
            throw new InvalidDataException("Unexpected sketch association in legacy features.");
        return bytes; // V4 adds optional Key(9); absence explicitly means a frozen recipe.
    }
}
