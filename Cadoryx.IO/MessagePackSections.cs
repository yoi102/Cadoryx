using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

/// <summary>Version 2 uses explicit numeric DTO keys; no typeless or contractless deserialization.</summary>
internal static partial class MessagePackSections
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
        (int)d.Settings.DisplayUnit,d.Settings.DecimalPlaces,d.Settings.LinearToleranceMm,d.Settings.AngularToleranceRad,
        d.Settings.Grid.Visible,d.Settings.Grid.SpacingMm,d.Settings.Grid.Snap,
        d.Settings.BackgroundTopArgb,d.Settings.BackgroundBottomArgb,
        d.Settings.Origin.Visible,(int)d.Settings.Origin.Style,d.Settings.Origin.SizeMm,
        (int)d.Settings.WorkPlane.Kind,d.Settings.WorkPlane.OffsetMm,
        (d.AssemblyConstraints.IsDefault?[]:d.AssemblyConstraints).OrderBy(c=>c.Id.Value).Select(C).ToArray());
    private static DocumentSection Doc(PackDocument d)=>new(new(d.Id),new(d.StateId),d.Name,new(d.Root),
        new DocumentSettings((LengthUnit)d.Unit,d.Decimals,d.LinearTolerance,d.AngularTolerance)
        { Grid = new(d.GridVisible,d.GridSpacingMm,d.GridSnap),
          BackgroundTopArgb=d.BackgroundTopArgb, BackgroundBottomArgb=d.BackgroundBottomArgb,
          Origin=new(d.OriginVisible,(DocumentOriginStyle)d.OriginStyle,d.OriginSizeMm),
          WorkPlane=new((DocumentWorkPlaneKind)d.WorkPlaneKind,d.WorkPlaneOffsetMm) },
        (d.AssemblyConstraints??[]).Select(c=>C(c,new DocumentId(d.Id))).ToImmutableArray());
    private static PackAssemblyConstraint C(AssemblyConstraint c)=>new(c.Id.Value,c.Name,(int)c.Kind,
        c.PrimaryPath.Slots.Select(s=>s.Value).ToArray(),c.PrimaryDefinitionId.Value,
        c.SecondaryPath?.Slots.Select(s=>s.Value).ToArray(),c.SecondaryDefinitionId?.Value,
        [c.PrimaryLocalPoint.X,c.PrimaryLocalPoint.Y,c.PrimaryLocalPoint.Z],
        [c.SecondaryLocalPoint.X,c.SecondaryLocalPoint.Y,c.SecondaryLocalPoint.Z],
        c.TargetDistanceMm,c.FixedWorld is {} world?T(world):null,
        c.PrimaryTopology is {} primary?PackTopology(primary):null,
        c.SecondaryTopology is {} secondary?PackTopology(secondary):null,c.IsEnabled,c.SchemaVersion,
        c.PrimaryLocalAxis==Vector3d.Zero?null:[c.PrimaryLocalAxis.X,c.PrimaryLocalAxis.Y,c.PrimaryLocalAxis.Z],
        c.SecondaryLocalAxis==Vector3d.Zero?null:[c.SecondaryLocalAxis.X,c.SecondaryLocalAxis.Y,c.SecondaryLocalAxis.Z]);
    private static AssemblyConstraint C(PackAssemblyConstraint c,DocumentId document)
    {
        if(c.PrimarySlots is null||c.PrimaryPoint is not {Length:3}||c.SecondaryPoint is not {Length:3}||
           c.PrimarySlots.Length>128||c.SecondarySlots?.Length>128||
           c.PrimaryAxis is not null and not {Length:3}||c.SecondaryAxis is not null and not {Length:3})
            throw new InvalidDataException("Malformed assembly constraint path or point.");
        return new(new(c.Id),c.Name,
        (AssemblyConstraintKind)c.Kind,new(document,c.PrimarySlots.Select(id=>new ComponentSlotId(id))),new(c.PrimaryDefinition),
        c.SecondarySlots is {} slots?new OccurrencePath(document,slots.Select(id=>new ComponentSlotId(id))):null,
        c.SecondaryDefinition is {} definition?new DefinitionId(definition):null,
        new(c.PrimaryPoint[0],c.PrimaryPoint[1],c.PrimaryPoint[2]),
        new(c.SecondaryPoint[0],c.SecondaryPoint[1],c.SecondaryPoint[2]),c.DistanceMm,
        c.FixedWorld is {} world?T(world):null,
        c.PrimaryTopology is {} primary?UnpackTopology(primary):null,
        c.SecondaryTopology is {} secondary?UnpackTopology(secondary):null,c.Enabled,c.Version,
        c.PrimaryAxis is {} pa?new(pa[0],pa[1],pa[2]):Vector3d.Zero,
        c.SecondaryAxis is {} sa?new(sa[0],sa[1],sa[2]):Vector3d.Zero);
    }
    public static byte[] UpgradeDocumentAssemblyAxes(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDocument>(bytes);
        return Serialize(old with{AssemblyConstraints=old.AssemblyConstraints??[]});
    }
    public static byte[] UpgradeDocumentAssemblyConstraints(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDocument>(bytes);
        return Serialize(old with{AssemblyConstraints=[]});
    }
    public static byte[] UpgradeDocumentWorkPlane(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDocument>(bytes);
        return Serialize(old with { WorkPlaneKind=(int)DocumentWorkPlaneKind.XY, WorkPlaneOffsetMm=0 });
    }
    public static byte[] UpgradeDocumentOrigin(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDocument>(bytes);
        return Serialize(old with { OriginVisible=true, OriginStyle=(int)DocumentOriginStyle.ColorAxes, OriginSizeMm=20 });
    }
    public static byte[] UpgradeDocumentGrid(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDocument>(bytes);
        return Serialize(old with { GridVisible=true, GridSpacingMm=10, GridSnap=false });
    }
    public static byte[] UpgradeDocumentBackground(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDocument>(bytes);
        return Serialize(old with { BackgroundTopArgb=DocumentSettings.DefaultBackgroundTopArgb,
            BackgroundBottomArgb=DocumentSettings.DefaultBackgroundBottomArgb });
    }
    public static byte[] UpgradeSolidDocumentBackground(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDocument>(bytes);
        return Serialize(old with { BackgroundBottomArgb=old.BackgroundTopArgb });
    }
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
    private static PackAppearance A(CadAppearance a)=>new(a.Argb,a.ByLayer,a.PreserveSourceStyles);
    private static CadAppearance A(PackAppearance a)=>new(a.Argb,a.ByLayer,a.PreserveSourceStyles);
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
        LocalFeatureRecipe l=>new("local-box-edge",[l.Box.X,l.Box.Y,l.Box.Z,l.Size,(int)l.First,(int)l.Second,l.SecondDistance??0,l.AdditionalEdges,l.EndRadius??0],T(l.Box.Placement),[G(l.Source)],[],(int)l.Operation),
        HistoryFilletRecipe h=>new("history-edge-fillet",[h.Radius,h.FullTopologyIndex,h.EndRadius??0],null,[G(h.Source)],[],0),
        HistoryChamferRecipe h=>new("history-edge-chamfer",[h.Distance,h.FullTopologyIndex,h.SupportFaceIndex,h.SecondDistance??0],null,[G(h.Source)],[],0),
        BoxRecipe b=>new("box",[b.X,b.Y,b.Z],T(b.Placement),[],[],0),
        CylinderRecipe c=>new("cylinder",[c.Radius,c.Height],T(c.Placement),[],[],0),
        ImportedRecipe i=>new("import",[],null,[G(i.Source)],[],0),
        TransformRecipe t=>new("transform",[],T(t.Transform),[G(t.Source)],[],0),
        BooleanRecipe b=>new("boolean",[],null,b.Inputs.Select(G).ToArray(),[],(int)b.Operation),
        ExtrudeRecipe e when e.Profile.Circle is {} c=>new("extrude-circle",[e.Distance,c.Center.X,c.Center.Y,c.Radius],T(e.Placement),[],[],0,H(e.Profile),P(e.Profile),null,null,I(e.Profile)),
        ExtrudeRecipe e when e.Profile.Arc is {} a=>new("extrude-arc",[e.Distance,a.Start.X,a.Start.Y,a.Middle.X,a.Middle.Y,a.End.X,a.End.Y],T(e.Placement),[],[],0),
        ExtrudeRecipe e when e.Profile.Bezier is {} b=>new("extrude-bezier",[e.Distance,b.Start.X,b.Start.Y,b.Control.X,b.Control.Y,b.End.X,b.End.Y],T(e.Placement),[],[],0),
        ExtrudeRecipe e when e.Profile.Spline is {} s=>new("extrude-spline",[e.Distance],T(e.Placement),[],[],0,
            SplineControls:s.Controls.Select(p=>new[]{p.X,p.Y}).ToArray()),
        ExtrudeRecipe e when !e.Profile.BoundaryCurves.IsEmpty=>new("extrude-mixed",[e.Distance],T(e.Placement),[],[],0,H(e.Profile),P(e.Profile),
            e.Profile.BoundaryCurves.Select(PackCurve).ToArray(),MixedHoles(e.Profile),I(e.Profile)),
        ExtrudeRecipe e=>new("extrude",[e.Distance],T(e.Placement),[],e.Profile.Points.Select(p=>new[]{p.X,p.Y}).ToArray(),0,H(e.Profile),P(e.Profile),null,MixedHoles(e.Profile),I(e.Profile)),
        RevolveRecipe v=>new("revolve",[v.AngleRadians],T(v.Placement),[],v.Profile.Points.Select(p=>new[]{p.X,p.Y}).ToArray(),0),
        _=>throw new NotSupportedException("Unknown recipe cannot be persisted.")
    };
    private static GeometryRecipe R(PackRecipe r)
    {
        int numbers=r.Kind switch{"local-box-edge"=>9,"history-edge-fillet"=>3,"history-edge-chamfer"=>4,"extrude-circle"=>4,"extrude-arc" or "extrude-bezier"=>7,"cylinder"=>2,"box"=>3,"extrude" or "extrude-mixed" or "extrude-spline" or "revolve"=>1,_=>0};
        if(r.Numbers.Length!=numbers)throw new InvalidDataException("Invalid recipe parameter count.");
        bool placed=r.Kind is "local-box-edge" or "box" or "cylinder" or "transform" or "extrude" or "extrude-circle" or "extrude-arc" or "extrude-bezier" or "extrude-spline" or "extrude-mixed" or "revolve";
        if(placed!=(r.Placement is not null))throw new InvalidDataException("Invalid recipe placement.");
        if(r.Kind is "local-box-edge" or "history-edge-fillet" or "history-edge-chamfer" or "import" or "transform"){if(r.Sources.Length!=1)throw new InvalidDataException("Expected one source.");}
        else if(r.Kind!="boolean"&&r.Sources.Length!=0)throw new InvalidDataException("Unexpected recipe source.");
        if(r.Kind is not ("extrude" or "revolve")&&r.Profile.Length!=0)throw new InvalidDataException("Unexpected recipe profile.");
        if(r.Holes is not null&&r.Kind is not ("extrude" or "extrude-circle" or "extrude-mixed"))throw new InvalidDataException("Unexpected circular holes.");
        if(r.PolygonHoles is not null&&r.Kind is not ("extrude" or "extrude-circle" or "extrude-mixed"))throw new InvalidDataException("Unexpected polygon holes.");
        if(r.MixedCurves is not null&&r.Kind!="extrude-mixed")throw new InvalidDataException("Unexpected mixed boundary curves.");
        if(r.MixedHoles is not null&&r.Kind is not ("extrude" or "extrude-circle" or "extrude-mixed"))throw new InvalidDataException("Unexpected mixed holes.");
        if(r.Islands is not null&&r.Kind is not ("extrude" or "extrude-circle" or "extrude-mixed"))throw new InvalidDataException("Unexpected islands.");
        if(r.SplineControls is not null&&r.Kind!="extrude-spline")throw new InvalidDataException("Unexpected spline controls.");
        ImmutableArray<CircularSketchRegion> Holes()=>r.Holes is null?[]:r.Holes.Length<=64?
            r.Holes.Select(h=>new CircularSketchRegion(new(h.X,h.Y),h.Radius)).ToImmutableArray():throw new InvalidDataException("Too many circular holes.");
        ImmutableArray<ImmutableArray<Point2d>> PolygonHoles()=>r.PolygonHoles is null?[]:r.PolygonHoles.Length<=64?
            r.PolygonHoles.Select(h=>h is {Vertices:{Length:>=3 and <=10000}}?
                h.Vertices.Select(p=>p is {Length:2}?new Point2d(p[0],p[1]):throw new InvalidDataException("Malformed polygon hole point.")).ToImmutableArray():
                throw new InvalidDataException("Malformed polygon hole.")).ToImmutableArray():
            throw new InvalidDataException("Too many polygon holes.");
        SketchProfile Profile()=>new(r.Profile.Select(p=>p.Length==2?new Point2d(p[0],p[1]):throw new InvalidDataException("Malformed profile point.")).ToImmutableArray(),null,Holes(),PolygonHoles())
            {MixedHoles=ReadMixedHoles(r.MixedHoles),Islands=ReadIslands(r.Islands)};
        return r.Kind switch
        {
            "local-box-edge"=>Local(r),
            "history-edge-fillet"=>HistoryFillet(r),
            "history-edge-chamfer"=>HistoryChamfer(r),
            "box"=>new BoxRecipe(r.Numbers[0],r.Numbers[1],r.Numbers[2],T(r.Placement!)),
            "cylinder"=>new CylinderRecipe(r.Numbers[0],r.Numbers[1],T(r.Placement!)),
            "import"=>new ImportedRecipe(G(r.Sources[0])),
            "transform"=>new TransformRecipe(G(r.Sources[0]),T(r.Placement!)),
            "boolean"=>new BooleanRecipe((BooleanOperation)r.Operation,r.Sources.Select(G).ToImmutableArray()),
            "extrude"=>new ExtrudeRecipe(Profile(),r.Numbers[0],T(r.Placement!)),
            "extrude-circle"=>CircularExtrude(r,Holes(),PolygonHoles()),
            "extrude-arc"=>ArcExtrude(r),
            "extrude-bezier"=>BezierExtrude(r),
            "extrude-spline"=>SplineExtrude(r),
            "extrude-mixed"=>MixedExtrude(r),
            "revolve"=>new RevolveRecipe(Profile(),r.Numbers[0],T(r.Placement!)),
            _=>throw new NotSupportedException("Unsupported feature recipe: "+r.Kind)
        };
    }
    private static ExtrudeRecipe ArcExtrude(PackRecipe r)
    {
        if(r.Operation!=0||r.Holes is not null||r.PolygonHoles is not null)
            throw new InvalidDataException("Arc segment extrusion cannot contain holes or another operation.");
        var n=r.Numbers;
        var profile=new SketchProfile([]){Arc=new(new(n[1],n[2]),new(n[3],n[4]),new(n[5],n[6]))};
        var recipe=new ExtrudeRecipe(profile,n[0],T(r.Placement!));recipe.Validate();return recipe;
    }
    private static ExtrudeRecipe BezierExtrude(PackRecipe r)
    {
        if(r.Operation!=0||r.Holes is not null||r.PolygonHoles is not null||r.MixedCurves is not null||
            r.MixedHoles is not null||r.Islands is not null)
            throw new InvalidDataException("Bezier segment extrusion cannot contain holes or another operation.");
        var n=r.Numbers;
        var profile=new SketchProfile([]){Bezier=new(new(n[1],n[2]),new(n[3],n[4]),new(n[5],n[6]))};
        var recipe=new ExtrudeRecipe(profile,n[0],T(r.Placement!));recipe.Validate();return recipe;
    }
    private static ExtrudeRecipe SplineExtrude(PackRecipe r)
    {
        if(r.Operation!=0||r.Holes is not null||r.PolygonHoles is not null||r.MixedCurves is not null||
            r.MixedHoles is not null||r.Islands is not null||r.SplineControls is not {Length:>=4 and <=8})
            throw new InvalidDataException("Invalid spline extrusion.");
        var controls=r.SplineControls.Select(ReadMixedPoint).ToImmutableArray();
        var recipe=new ExtrudeRecipe(new SketchProfile([]){Spline=new(controls)},r.Numbers[0],T(r.Placement!));
        recipe.Validate();return recipe;
    }
    private static ExtrudeRecipe MixedExtrude(PackRecipe r)
    {
        if(r.Operation!=0||r.MixedCurves is not {Length:>=3 and <=256})
            throw new InvalidDataException("Invalid mixed profile extrusion.");
        var curves=r.MixedCurves.Select(ReadCurve).ToImmutableArray();
        var profile=new SketchProfile([],null,ReadCircularHoles(r.Holes),ReadPolygonHoles(r.PolygonHoles))
            {BoundaryCurves=curves,MixedHoles=ReadMixedHoles(r.MixedHoles),Islands=ReadIslands(r.Islands)};
        var recipe=new ExtrudeRecipe(profile,r.Numbers[0],T(r.Placement!));recipe.Validate();return recipe;
    }
    private static PackMixedCurve PackCurve(SketchBoundaryCurve c)=>new([c.Start.X,c.Start.Y],[c.End.X,c.End.Y],
        c.Middle is {} middle?[middle.X,middle.Y]:null);
    private static PackMixedCurve[][]? MixedHoles(SketchProfile p)=>p.MixedHoles.IsDefaultOrEmpty?null:
        p.MixedHoles.Select(h=>h.Select(PackCurve).ToArray()).ToArray();
    private static PackIsland[]? I(SketchProfile p)=>p.Islands.IsDefaultOrEmpty?null:
        p.Islands.Select(i=>new PackIsland(i.Points.Select(point=>new[]{point.X,point.Y}).ToArray(),
            i.Circle is {} c?new PackCircularHole(c.Center.X,c.Center.Y,c.Radius):null,
            i.BoundaryCurves.IsDefaultOrEmpty?null:i.BoundaryCurves.Select(PackCurve).ToArray())).ToArray();
    private static Point2d ReadMixedPoint(double[]? values)=>values is {Length:2}?new(values[0],values[1]):
        throw new InvalidDataException("Malformed mixed boundary point.");
    private static SketchBoundaryCurve ReadCurve(PackMixedCurve? c)=>c is null?throw new InvalidDataException("Null mixed curve."):
        new(ReadMixedPoint(c.Start),ReadMixedPoint(c.End),c.Middle is null?null:ReadMixedPoint(c.Middle));
    private static ImmutableArray<ImmutableArray<SketchBoundaryCurve>> ReadMixedHoles(PackMixedCurve[][]? holes)=>holes is null?[]:
        holes.Length<=64?holes.Select(h=>h is {Length:>=3 and <=256}?h.Select(ReadCurve).ToImmutableArray():
            throw new InvalidDataException("Malformed mixed hole.")).ToImmutableArray():throw new InvalidDataException("Too many mixed holes.");
    private static ImmutableArray<SketchIslandRegion> ReadIslands(PackIsland[]? islands)=>islands is null?[]:
        islands.Length<=64?islands.Select(i=>
        {
            if(i is null||i.Points is null)throw new InvalidDataException("Malformed island.");
            var points=i.Points.Select(p=>p is {Length:2}?new Point2d(p[0],p[1]):
                throw new InvalidDataException("Malformed island point.")).ToImmutableArray();
            var curves=i.MixedCurves is null?ImmutableArray<SketchBoundaryCurve>.Empty:
                i.MixedCurves.Select(ReadCurve).ToImmutableArray();
            if((i.Circle is not null?1:0)+(points.IsEmpty?0:1)+(curves.IsEmpty?0:1)!=1)
                throw new InvalidDataException("Island must have exactly one boundary.");
            return new SketchIslandRegion(points,i.Circle is {} c?new(new(c.X,c.Y),c.Radius):null,curves);
        }).ToImmutableArray():throw new InvalidDataException("Too many islands.");
    private static ImmutableArray<CircularSketchRegion> ReadCircularHoles(PackCircularHole[]? holes)=>holes is null?[]:
        holes.Length<=64?holes.Select(h=>new CircularSketchRegion(new(h.X,h.Y),h.Radius)).ToImmutableArray():
            throw new InvalidDataException("Too many circular holes.");
    private static ImmutableArray<ImmutableArray<Point2d>> ReadPolygonHoles(PackPolygonHole[]? holes)=>holes is null?[]:
        holes.Length<=64?holes.Select(h=>h is {Vertices:{Length:>=3 and <=10000}}?
            h.Vertices.Select(p=>p is {Length:2}?new Point2d(p[0],p[1]):throw new InvalidDataException("Malformed polygon hole point.")).ToImmutableArray():
            throw new InvalidDataException("Malformed polygon hole.")).ToImmutableArray():throw new InvalidDataException("Too many polygon holes.");
    private static PackCircularHole[]? H(SketchProfile profile)=>profile.Holes.IsDefaultOrEmpty?null:
        profile.Holes.Select(h=>new PackCircularHole(h.Center.X,h.Center.Y,h.Radius)).ToArray();
    private static PackPolygonHole[]? P(SketchProfile profile)=>profile.PolygonHoles.IsDefaultOrEmpty?null:
        profile.PolygonHoles.Select(h=>new PackPolygonHole(h.Select(p=>new[]{p.X,p.Y}).ToArray())).ToArray();
    private static ExtrudeRecipe CircularExtrude(PackRecipe r,ImmutableArray<CircularSketchRegion> holes,
        ImmutableArray<ImmutableArray<Point2d>> polygonHoles)
    {
        if(r.Operation!=0)throw new InvalidDataException("Invalid circular extrusion operation.");
        var result=new ExtrudeRecipe(SketchProfile.FromCircle(new(r.Numbers[1],r.Numbers[2]),r.Numbers[3]) with
            {Holes=holes,PolygonHoles=polygonHoles,MixedHoles=ReadMixedHoles(r.MixedHoles),Islands=ReadIslands(r.Islands)},r.Numbers[0],T(r.Placement!));
        result.Validate();return result;
    }
    private static LocalFeatureRecipe Local(PackRecipe r)
    {
        if(r.Numbers.Skip(4).Take(2).Any(n=>!double.IsFinite(n)||n!=Math.Truncate(n)||n<0||n>5))throw new InvalidDataException("Invalid semantic edge boundaries.");
        if(!double.IsFinite(r.Numbers[6])||r.Numbers[6]<0||!double.IsFinite(r.Numbers[7])||r.Numbers[7]!=Math.Truncate(r.Numbers[7])||
            r.Numbers[7]<0||r.Numbers[7]>0xfff||!double.IsFinite(r.Numbers[8])||r.Numbers[8]<0)
            throw new InvalidDataException("Invalid local feature parameters.");
        var second=r.Numbers[6]>0?r.Numbers[6]:(double?)null;
        var end=r.Numbers[8]>0?r.Numbers[8]:(double?)null;
        var result=new LocalFeatureRecipe(G(r.Sources[0]),new(r.Numbers[0],r.Numbers[1],r.Numbers[2],T(r.Placement!)),(BoxBoundary)r.Numbers[4],(BoxBoundary)r.Numbers[5],(LocalFeatureOperation)r.Operation,r.Numbers[3],second,(int)r.Numbers[7],end);result.Validate();return result;
    }
    private static HistoryFilletRecipe HistoryFillet(PackRecipe r)
    {
        if(!double.IsFinite(r.Numbers[1])||r.Numbers[1]!=Math.Truncate(r.Numbers[1])||r.Operation!=0||
            !double.IsFinite(r.Numbers[2])||r.Numbers[2]<0)
            throw new InvalidDataException("Invalid history fillet locator.");
        var result=new HistoryFilletRecipe(G(r.Sources[0]),checked((int)r.Numbers[1]),r.Numbers[0],r.Numbers[2]>0?r.Numbers[2]:null);result.Validate();return result;
    }
    private static HistoryChamferRecipe HistoryChamfer(PackRecipe r)
    {
        if(r.Operation!=0||r.Numbers.Skip(1).Take(2).Any(n=>!double.IsFinite(n)||n!=Math.Truncate(n)||n<0||n>100000)||
            !double.IsFinite(r.Numbers[3])||r.Numbers[3]<0)
            throw new InvalidDataException("Invalid history chamfer locator.");
        var result=new HistoryChamferRecipe(G(r.Sources[0]),checked((int)r.Numbers[1]),checked((int)r.Numbers[2]),
            r.Numbers[0],r.Numbers[3]>0?r.Numbers[3]:null);result.Validate();return result;
    }
    private static PackPresentation Presentation(PresentationSection p)=>new(
        p.Layers.Select(l=>new PackLayer(l.Id.Value,l.Name,l.Argb,l.IsVisible,l.IsLocked)).ToArray(),
        p.Materials.Select(m=>new PackMaterial(m.Id.Value,m.Name,m.DensityKgPerMm3)).ToArray());
    private static PresentationSection Presentation(PackPresentation p)=>new(
        p.Layers.Select(l=>new CadLayer(new(l.Id),l.Name,l.Argb,l.Visible,l.Locked)).ToImmutableArray(),
        p.Materials.Select(m=>new CadMaterial(new(m.Id),m.Name,m.Density)).ToImmutableArray());
}
