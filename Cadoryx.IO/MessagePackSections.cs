using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

/// <summary>Version 2 uses explicit numeric DTO keys; no typeless or contractless deserialization.</summary>
internal static class MessagePackSections
{
    private static readonly MessagePackSerializerOptions Options=MessagePackSerializerOptions.Standard
        .WithSecurity(MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(128))
        .WithCompression(MessagePackCompression.None);

    public static byte[] Encode(string kind,object section)=>kind switch
    {
        "document"=>Serialize(Doc((DocumentSection)section)),
        "structure"=>Serialize(Structure((StructureSection)section)),
        "features"=>Serialize(new PackFeatures(((FeaturesSection)section).Features.Select(F).ToArray())),
        "presentation"=>Serialize(Presentation((PresentationSection)section)),
        _=>throw new NotSupportedException(kind)
    };
    public static T Decode<T>(string kind,ReadOnlyMemory<byte> bytes)
    {
        object result=kind switch
        {
            "document"=>Doc(Read<PackDocument>(bytes)),
            "structure"=>Structure(Read<PackStructure>(bytes)),
            "features"=>new FeaturesSection(Read<PackFeatures>(bytes).Features.Select(F).ToImmutableArray()),
            "presentation"=>Presentation(Read<PackPresentation>(bytes)),
            _=>throw new NotSupportedException(kind)
        };
        return (T)result;
    }
    private static byte[] Serialize<T>(T value)=>MessagePackSerializer.Serialize(value,Options);
    private static T Read<T>(ReadOnlyMemory<byte> bytes) where T:class
    {
        var reader=new MessagePackReader(bytes);
        var result=MessagePackSerializer.Deserialize<T>(ref reader,Options)??throw new InvalidDataException("Null MessagePack section.");
        if(!reader.End)throw new InvalidDataException("Trailing MessagePack section data.");
        return result;
    }
    private static PackDocument Doc(DocumentSection d)=>new(d.Id.Value,d.StateId.Value,d.Name,d.RootAssemblyId.Value,
        (int)d.Settings.DisplayUnit,d.Settings.DecimalPlaces,d.Settings.LinearToleranceMm,d.Settings.AngularToleranceRad);
    private static DocumentSection Doc(PackDocument d)=>new(new(d.Id),new(d.StateId),d.Name,new(d.Root),
        new((LengthUnit)d.Unit,d.Decimals,d.LinearTolerance,d.AngularTolerance));
    private static PackStructure Structure(StructureSection s)=>new(s.Definitions.Select(D).ToArray(),s.Bodies.Select(B).ToArray());
    private static StructureSection Structure(PackStructure s)=>new(s.Definitions.Select(D).ToImmutableArray(),s.Bodies.Select(B).ToImmutableArray());
    private static PackDefinition D(CadDefinition d)=>d switch
    {
        AssemblyDefinition a=>new(a.Id.Value,a.Name,true,[],[],a.Children.Select(S).ToArray()),
        PartDefinition p=>new(p.Id.Value,p.Name,false,p.Bodies.Select(x=>x.Value).ToArray(),p.Features.Select(x=>x.Value).ToArray(),[]),
        _=>throw new InvalidDataException("Unknown definition.")
    };
    private static CadDefinition D(PackDefinition d)
    {
        if(d.Assembly)
        {
            if(d.Bodies.Length>0||d.Features.Length>0)throw new InvalidDataException("Assembly cannot own part data.");
            return new AssemblyDefinition(new(d.Id),d.Name,d.Children.Select(S).ToImmutableArray());
        }
        if(d.Children.Length>0)throw new InvalidDataException("Part cannot own assembly slots.");
        return new PartDefinition(new(d.Id),d.Name,d.Bodies.Select(x=>new BodyId(x)).ToImmutableArray(),d.Features.Select(x=>new FeatureId(x)).ToImmutableArray());
    }
    private static PackSlot S(ComponentSlot s)=>new(s.Id.Value,s.DefinitionId.Value,s.Name,T(s.LocalTransform),s.IsVisible,s.AppearanceOverride is {} a?A(a):null);
    private static ComponentSlot S(PackSlot s)=>new(new(s.Id),new(s.DefinitionId),s.Name,T(s.Transform),s.Visible,s.Appearance is {} a?A(a):null);
    private static PackTransform T(RigidTransform3d t)=>new(t.Translation.X,t.Translation.Y,t.Translation.Z,t.Rotation.X,t.Rotation.Y,t.Rotation.Z,t.Rotation.W);
    private static RigidTransform3d T(PackTransform t)=>new(new(t.X,t.Y,t.Z),new(t.Qx,t.Qy,t.Qz,t.Qw));
    private static PackAppearance A(CadAppearance a)=>new(a.Argb,a.ByLayer);
    private static CadAppearance A(PackAppearance a)=>new(a.Argb,a.ByLayer);
    private static PackGeometry G(GeometryAssetRef g)=>new(g.AssetId.Sha256,g.Revision.Value,(int)g.Kind,
        [g.Bounds.Min.X,g.Bounds.Min.Y,g.Bounds.Min.Z],[g.Bounds.Max.X,g.Bounds.Max.Y,g.Bounds.Max.Z],g.VolumeMm3,g.Source?.ContextAssetId.Sha256,g.Source?.DefinitionEntry);
    private static GeometryAssetRef G(PackGeometry g)
    {
        if(g.Min.Length!=3||g.Max.Length!=3||(g.ContextHash is null)!=(g.Entry is null))throw new InvalidDataException("Malformed geometry record.");
        return new(new(g.Hash),new(g.Revision),(BodyKind)g.Kind,new(new(g.Min[0],g.Min[1],g.Min[2]),new(g.Max[0],g.Max[1],g.Max[2])),g.Volume,
            g.ContextHash is {} hash?new XdeSourceRef(new(hash),g.Entry!):null);
    }
    private static PackBody B(CadBody b)=>new(b.Id.Value,b.PartId.Value,b.Name,G(b.Geometry),b.Producer?.Value,b.LayerId.Value,A(b.Appearance),b.IsVisible,b.MaterialId?.Value);
    private static CadBody B(PackBody b)=>new(new(b.Id),new(b.Part),b.Name,G(b.Geometry),b.Producer is {} producer?new FeatureId(producer):null,new(b.Layer),
        A(b.Appearance),b.Visible,b.Material is {} material?new MaterialId(material):null);
    private static PackFeature F(FeatureDefinition f)=>new(f.Id.Value,f.PartId.Value,f.Name,R(f.Recipe),f.Inputs.Select(x=>x.Value).ToArray(),f.OutputBodyId.Value,G(f.Result),f.SchemaVersion,
        f.OutputMetadata is {} m?new(m.Name,m.Layer.Value,A(m.Appearance),m.Visible,m.Material?.Value):null);
    private static FeatureDefinition F(PackFeature f)=>new(new(f.Id),new(f.Part),f.Name,R(f.Recipe),f.Inputs.Select(x=>new FeatureId(x)).ToImmutableArray(),new(f.Output),G(f.Result),f.Version,
        f.OutputMetadata is {} m?new(m.Name,new(m.Layer),A(m.Appearance),m.Visible,m.Material is {} material?new MaterialId(material):null):null);
    private static PackRecipe R(GeometryRecipe r)=>r switch
    {
        BoxRecipe b=>new("box",[b.X,b.Y,b.Z],T(b.Placement),[],[],0),
        CylinderRecipe c=>new("cylinder",[c.Radius,c.Height],T(c.Placement),[],[],0),
        ImportedRecipe i=>new("import",[],null,[G(i.Source)],[],0),
        TransformRecipe t=>new("transform",[],T(t.Transform),[G(t.Source)],[],0),
        BooleanRecipe b=>new("boolean",[],null,b.Inputs.Select(G).ToArray(),[],(int)b.Operation),
        ExtrudeRecipe e=>new("extrude",[e.Distance],T(e.Placement),[],e.Profile.Points.Select(p=>new[]{p.X,p.Y}).ToArray(),0),
        RevolveRecipe v=>new("revolve",[v.AngleRadians],T(v.Placement),[],v.Profile.Points.Select(p=>new[]{p.X,p.Y}).ToArray(),0),
        _=>throw new NotSupportedException("Unknown recipe cannot be persisted.")
    };
    private static GeometryRecipe R(PackRecipe r)
    {
        int numbers=r.Kind switch{"box"=>3,"cylinder"=>2,"extrude" or "revolve"=>1,_=>0};
        if(r.Numbers.Length!=numbers)throw new InvalidDataException("Invalid recipe parameter count.");
        bool placed=r.Kind is "box" or "cylinder" or "transform" or "extrude" or "revolve";
        if(placed!=(r.Placement is not null))throw new InvalidDataException("Invalid recipe placement.");
        if(r.Kind is "import" or "transform"){if(r.Sources.Length!=1)throw new InvalidDataException("Expected one source.");}
        else if(r.Kind!="boolean"&&r.Sources.Length!=0)throw new InvalidDataException("Unexpected recipe source.");
        if(r.Kind is not ("extrude" or "revolve")&&r.Profile.Length!=0)throw new InvalidDataException("Unexpected recipe profile.");
        SketchProfile Profile()=>new(r.Profile.Select(p=>p.Length==2?new Point2d(p[0],p[1]):throw new InvalidDataException("Malformed profile point.")).ToImmutableArray());
        return r.Kind switch
        {
            "box"=>new BoxRecipe(r.Numbers[0],r.Numbers[1],r.Numbers[2],T(r.Placement!)),
            "cylinder"=>new CylinderRecipe(r.Numbers[0],r.Numbers[1],T(r.Placement!)),
            "import"=>new ImportedRecipe(G(r.Sources[0])),
            "transform"=>new TransformRecipe(G(r.Sources[0]),T(r.Placement!)),
            "boolean"=>new BooleanRecipe((BooleanOperation)r.Operation,r.Sources.Select(G).ToImmutableArray()),
            "extrude"=>new ExtrudeRecipe(Profile(),r.Numbers[0],T(r.Placement!)),
            "revolve"=>new RevolveRecipe(Profile(),r.Numbers[0],T(r.Placement!)),
            _=>throw new NotSupportedException("Unsupported feature recipe: "+r.Kind)
        };
    }
    private static PackPresentation Presentation(PresentationSection p)=>new(
        p.Layers.Select(l=>new PackLayer(l.Id.Value,l.Name,l.Argb,l.IsVisible,l.IsLocked)).ToArray(),
        p.Materials.Select(m=>new PackMaterial(m.Id.Value,m.Name,m.DensityKgPerMm3)).ToArray());
    private static PresentationSection Presentation(PackPresentation p)=>new(
        p.Layers.Select(l=>new CadLayer(new(l.Id),l.Name,l.Argb,l.Visible,l.Locked)).ToImmutableArray(),
        p.Materials.Select(m=>new CadMaterial(new(m.Id),m.Name,m.Density)).ToImmutableArray());
}
