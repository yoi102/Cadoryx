using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class FaceFeatureTests
{
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)]
    public async Task ShellHasExactClosingFacesThicknessDirectionAndIndependentOutput(bool outward,bool both)
    {
        await using var f=new Fixture();await f.Initialize();var recipe=f.Shell(outward,both);var before=f.Session.Snapshot;
        using(var preview=await f.Kernel.EvaluateAsync(recipe,f.Assets))
        {
            Assert.Same(before,f.Session.Snapshot);Assert.Equal(BodyKind.Solid,preview.Geometry.Kind);
            if(!outward)Assert.Equal(both?600:680,preview.Geometry.VolumeMm3,4);
            else Assert.True(preview.Geometry.VolumeMm3>0);
            using var shape=OcctGeometryBridge.ReadShape(preview.Geometry,f.Assets);Assert.True(shape.GetSelfInterferenceReport().IsClear);
        }
        await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Shell"));var after=f.Session.Snapshot;after.Validate();
        Assert.Single(after.Bodies);var feature=after.Features.Values.Single(p=>p.Recipe is ShellRecipe);Assert.Null(feature.TopologyHistory);Assert.Equal(f.Source.Id,feature.Inputs.Single());
        Assert.Equal(1800,after.Features[f.Source.Id].Result.VolumeMm3,5);await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }
    [Theory][InlineData(.05)][InlineData(-.05)]
    public async Task DraftTwoExplicitProgramsRespectSignedAnglesAndPreserveSource(double angle)
    {
        await using var f=new Fixture();await f.Initialize();var recipe=f.Draft(angle);
        using(var result=await f.Kernel.EvaluateAsync(recipe,f.Assets))
        {
            Assert.Equal(1800-12*15*15*(Math.Tan(angle)+Math.Tan(2*angle))/2,result.Geometry.VolumeMm3,4);
            using var shape=OcctGeometryBridge.ReadShape(result.Geometry,f.Assets);Assert.True(shape.GetSelfInterferenceReport().IsClear);
        }
        await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Draft"));f.Session.Snapshot.Validate();Assert.Equal(1800,f.Session.Snapshot.Features[f.Source.Id].Result.VolumeMm3,5);
        var feature=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is DraftRecipe);
        var trace=await f.Kernel.TraceAsync(f.Session.Snapshot,TopologyReference.Box(f.Session.Snapshot,f.Source.Id,BoxBoundary.YMin,BoxBoundary.ZMin),feature.Id,f.Assets);
        // This native draft leaves the untouched bottom edge unmapped. Geometric equality is not identity evidence.
        Assert.NotNull(feature.TopologyHistory);Assert.Equal(HistoryResolutionStatus.Unsupported,trace.Status);Assert.Null(trace.Target);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SourceEditFreezesFaceFeatureAndSuccessorUntilAuthorReselects(bool draft)
    {
        await using var f=new Fixture();await f.Initialize();GeometryRecipe recipe=draft?f.Draft():f.Shell();
        await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Face feature"));var local=f.Session.Snapshot.Features.Values.Single(p=>p.Id!=f.Source.Id);
        await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(30,0,0)),"Other",local.PartId));
        await f.Session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,f.Session.Snapshot.Bodies.Keys));var following=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is BooleanRecipe);
        var before=f.Session.Snapshot;
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));var frozen=f.Session.Snapshot;frozen.Validate();
        Assert.True(frozen.Features[local.Id].IsStale);Assert.True(frozen.Features[following.Id].IsStale);Assert.Equal(local.Result,frozen.Features[local.Id].Result);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new ReselectFaceFeatureCommand(local.Id,recipe)));Assert.Same(frozen,f.Session.Snapshot);
        f.Source=frozen.Features[f.Source.Id];await f.Pick();
        GeometryRecipe selected=draft?((DraftRecipe)recipe) with{Source=f.Source.Result,Programs=((DraftRecipe)recipe).Programs.Select((p,i)=>p with{Face=i==0?f.Left:f.Right}).ToImmutableArray()}:
            ((ShellRecipe)recipe) with{Source=f.Source.Result,ClosingFaces=[f.Top]};
        await f.Session.ExecuteAsync(new ReselectFaceFeatureCommand(local.Id,selected));var restored=f.Session.Snapshot;restored.Validate();
        Assert.False(restored.Features[local.Id].IsStale);Assert.False(restored.Features[following.Id].IsStale);Assert.NotEqual(local.Result.AssetId,restored.Features[local.Id].Result.AssetId);
        await f.Session.UndoAsync();Assert.Same(frozen,f.Session.Snapshot);await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FaceFeatureRoundtripAndLegacyPayloadRejectionPreserveExactPrograms(bool draft)
    {
        using var files=new TestFiles();await using var f=new Fixture();await f.Initialize();GeometryRecipe recipe=draft?f.Draft():f.Shell();
        await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Face feature"));var feature=f.Session.Snapshot.Features.Values.Single(p=>p.Id!=f.Source.Id);
        var path=files.PathFor("face.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,path);int count=f.Assets.Count;
        using(var loaded=await storage.LoadAsync(path,f.Assets))
        {
            loaded.Snapshot.Validate();var actual=loaded.Snapshot.Features[feature.Id].Recipe;Assert.Equal(FaceFeatureRecipes.Faces(recipe),FaceFeatureRecipes.Faces(actual));
            if(draft)Assert.Equal(((DraftRecipe)recipe).Programs.AsEnumerable(),((DraftRecipe)actual).Programs.AsEnumerable());else Assert.Equal(((ShellRecipe)recipe).Thickness,((ShellRecipe)actual).Thickness);
            await using var reopened=new CadDocumentSession(loaded.Snapshot,f.Assets,f.Kernel,new InlineSessionDispatcher());
            GeometryRecipe changed=draft?((DraftRecipe)actual) with{Programs=((DraftRecipe)actual).Programs.Select(p=>p with{Angle=p.Angle*1.2}).ToImmutableArray()}:
                ((ShellRecipe)actual) with{Thickness=.5};await reopened.ExecuteAsync(new RecomputeCommand(feature.Id,changed));reopened.Snapshot.Validate();
        }
        Assert.Equal(count,f.Assets.Count);var manifest=FormatEvolutionTests.Manifest(path);Assert.Contains("cadoryx.face-features.1",manifest.RequiredCapabilities);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["features"].Version,manifest.Sections.Single(p=>p.Kind=="features").SchemaVersion);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=26}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }
    [Theory][InlineData("empty")][InlineData("duplicate")][InlineData("foreign")][InlineData("revision")][InlineData("fingerprint")][InlineData("kind")][InlineData("thick")]
    public async Task InvalidShellIsAtomicAndNeverLeaks(string failure)
    {
        await using var f=new Fixture();await f.Initialize();var recipe=f.Shell();var before=f.Session.Snapshot;int count=f.Assets.Count;
        recipe=failure switch
        {
            "empty"=>recipe with{ClosingFaces=[]},"duplicate"=>recipe with{ClosingFaces=[f.Top,f.Top]},
            "foreign"=>recipe with{ClosingFaces=[f.Top with{DocumentId=DocumentId.New()}]},"revision"=>recipe with{ClosingFaces=[f.Top with{Revision=GeometryRevisionId.New()}]},
            "fingerprint"=>recipe with{ClosingFaces=[f.Top with{Fingerprint=new string('0',64)}]},"kind"=>recipe with{ClosingFaces=[f.Top with{Kind=HistoryShapeKind.Edge}]},
            _=>recipe with{Thickness=6}
        };
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Bad shell")));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
    }
    [Fact] public async Task DraftParametersAndNeutralDatumDriveGeometryWithoutReplacingFaceIdentity()
    {
        await using var f=new Fixture();await f.Initialize();var datum=new CadDatumPlane(Guid.NewGuid(),f.Source.PartId,"Neutral",DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY),Value:0);
        await f.Session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(datum.Id,datum)}));datum=f.Session.Snapshot.Datums.Planes[datum.Id];
        var recipe=f.Draft() with{Programs=f.Draft().Programs.Select(p=>p with{Datum=datum.Id,DatumRevision=datum.Revision}).ToImmutableArray()};
        await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Driven draft"));var feature=f.Session.Snapshot.Features.Values.Single(p=>p.Id!=f.Source.Id);
        var parameter=new CadParameter(Guid.NewGuid(),"angle",ParameterDimension.Angle,"5 deg");var binding=new ParameterBinding(Guid.NewGuid(),parameter.Id,ParameterTarget.Feature(feature.Id,recipe.Programs[0].AngleProperty));
        await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[binding])));
        await f.Session.ExecuteAsync(new SetDatumsCommand(f.Session.Snapshot.Datums with{Planes=f.Session.Snapshot.Datums.Planes.SetItem(datum.Id,f.Session.Snapshot.Datums.Planes[datum.Id] with{Value=2})}));
        var actual=(DraftRecipe)f.Session.Snapshot.Features[feature.Id].Recipe;Assert.Equal(2,actual.Programs[0].NeutralPlane.Translation.Z,5);Assert.Equal(recipe.Programs[0].Face,actual.Programs[0].Face);
        Assert.Equal(1800-12*15*5.5*(Math.Tan(5*Math.PI/180)+Math.Tan(.1)),f.Session.Snapshot.Features[feature.Id].Result.VolumeMm3,4);f.Session.Snapshot.Validate();
        var before=f.Session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,actual with{Programs=actual.Programs.Select(p=>p with{NeutralPlane=RigidTransform3d.Translate(0,0,3)}).ToImmutableArray()})));Assert.Same(before,f.Session.Snapshot);
    }
    [Fact] public async Task DrivenShellKeepsAuthoredThicknessWhenSourceRevisionFreezesGeometry()
    {
        await using var f=new Fixture();await f.Initialize();await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,f.Shell(),"Shell"));var feature=f.Session.Snapshot.Features.Values.Single(p=>p.Id!=f.Source.Id);
        var parameter=new CadParameter(Guid.NewGuid(),"wall",ParameterDimension.Length,"0.5 mm");var binding=new ParameterBinding(Guid.NewGuid(),parameter.Id,ParameterTarget.Feature(feature.Id,"Thickness"));
        await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[binding])));Assert.Equal(364.5,f.Session.Snapshot.Features[feature.Id].Result.VolumeMm3,4);
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));Assert.True(f.Session.Snapshot.Features[feature.Id].IsStale);
        f.Session.Snapshot.Validate();Assert.Equal(.5,ParameterTargets.Read(f.Session.Snapshot,binding.Target).Value);
    }
    [Theory][InlineData("companion")][InlineData("kind")][InlineData("null-face")][InlineData("direction")]
    public async Task MalformedFaceFeatureStorageRejectsWithoutAcquiringAssets(string mutation)
    {
        using var files=new TestFiles();await using var f=new Fixture();await f.Initialize();await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,f.Draft(),"Draft"));
        var path=files.PathFor("bad-draft.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,path);int count=f.Assets.Count;
        FormatEvolutionTests.RewriteSection(path,"features",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);return MessagePackSerializer.Serialize(data with{Features=data.Features.Select(feature=>
            {
                if(feature.Recipe.Kind!="draft")return feature;var r=feature.Recipe;var payload=r.FaceFeature!;
                r=mutation switch{"companion"=>r with{Numbers=[1]},"kind"=>r with{Kind="box"},"null-face"=>r with{FaceFeature=payload with{Programs=[payload.Programs![0] with{Face=null!}]}},
                    _=>r with{FaceFeature=payload with{Programs=[payload.Programs![0] with{Pull=[0,0,0]}]}}};return feature with{Recipe=r};
            }).ToArray()});
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CurvedCylinderShellAndDraftKeepExactAnalyticVolumes(bool draft)
    {
        await using var f=new Fixture();await f.Session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,20,RigidTransform3d.Identity),"Cylinder"));f.Source=f.Session.Snapshot.Features.Values.Single();
        ExactTopologySelection selected;
        using(var shape=OcctGeometryBridge.ReadShape(f.Source.Result,f.Assets))using(var map=RepairSnapshot.Create(shape))
        {
            var item=map.Topology.Single(t=>
            {
                if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);var b=face.GetBoundingBox();
                return draft?b.SizeZ>19.99:Math.Abs(b.Minimum.Z-20)<1e-5&&Math.Abs(b.Maximum.Z-20)<1e-5;
            });
            selected=new(f.Session.Snapshot.Id,f.Source.Id,f.Source.Result.Revision,f.Source.Result.AssetId,map.Fingerprint,item.Selection.Index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);
        }
        GeometryRecipe recipe=draft?new DraftRecipe(f.Source.Result,[new(Guid.NewGuid(),selected,.05,RigidTransform3d.Identity,Vector3d.UnitZ,FaceDraftPropagation.TangentChain)]):new ShellRecipe(f.Source.Result,[selected],1);
        await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Curved"));var output=f.Session.Snapshot.Bodies.Values.Single().Geometry;
        double top=5-20*Math.Tan(.05);Assert.Equal(draft?Math.PI*20*(25+5*top+top*top)/3:196*Math.PI,output.VolumeMm3,4);
        using var result=OcctGeometryBridge.ReadShape(output,f.Assets);Assert.True(result.GetValidationReport(geometryChecks:true,exact:true).IsValid);Assert.True(result.GetSelfInterferenceReport().IsClear);f.Session.Snapshot.Validate();
    }
    [Theory][InlineData("direction")][InlineData("neutral")][InlineData("duplicate")][InlineData("angle")][InlineData("cut-through")][InlineData("parallel-face")]
    public async Task DraftPreflightOrBuildFailureKeepsDocumentAndAssets(string failure)
    {
        await using var f=new Fixture();await f.Initialize();var recipe=f.Draft();var p=recipe.Programs[0];
        recipe=failure switch
        {
            "direction"=>recipe with{Programs=[p with{PullDirection=Vector3d.Zero}]},
            "neutral"=>recipe with{Programs=[p with{NeutralPlane=new(Vector3d.Zero,Quaterniond.FromEulerDegrees(0,90,0))}]},
            "duplicate"=>recipe with{Programs=[p,p with{Id=Guid.NewGuid()}]},
            "angle"=>recipe with{Programs=[p with{Angle=Math.PI/2}]},
            "parallel-face"=>recipe with{Programs=[p with{Face=f.Top}]},
            _=>recipe with{Programs=recipe.Programs.Select(v=>v with{Angle=1}).ToImmutableArray()}
        };
        var before=f.Session.Snapshot;int count=f.Assets.Count;
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Invalid draft")));Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FinalFaceFeatureTopologyCanBeExplicitlySelectedForLocalSuccessor(bool draft)
    {
        await using var f=new Fixture();await f.Initialize();GeometryRecipe recipe=draft?f.Draft():f.Shell();await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Face feature"));
        var feature=f.Session.Snapshot.Features.Values.Single(p=>p.Id!=f.Source.Id);ExactTopologySelection selected;
        using(var shape=OcctGeometryBridge.ReadShape(feature.Result,f.Assets))using(var map=RepairSnapshot.Create(shape))
        {
            var item=map.Topology.Single(t=>
            {
                if(t.Kind!=ShapeKind.Edge)return false;using var edge=map.CopySubshape(t.Selection);var b=edge.GetBoundingBox();
                return Math.Abs(b.Minimum.Z)<1e-5&&Math.Abs(b.Maximum.Z)<1e-5&&Math.Abs(b.Minimum.X)<1e-5&&Math.Abs(b.Maximum.X)<1e-5&&b.SizeY>11.99;
            });
            selected=new(f.Session.Snapshot.Id,feature.Id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,item.Selection.Index,HistoryShapeKind.Edge,OcctGeometryKernel.HistoryAdapterVersion);
        }
        await f.Session.ExecuteAsync(new ExactLocalFeatureCommand(selected,Cadoryx.Db.LocalFeatureOperation.Fillet,.1));
        var successor=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HistoryFilletRecipe);Assert.False(successor.IsStale);Assert.Equal(feature.Id,successor.Inputs.Single());Assert.NotNull(successor.TopologyBinding!.ExactEdge);
        var before=f.Session.Snapshot;GeometryRecipe changed=draft?((DraftRecipe)recipe) with{Programs=((DraftRecipe)recipe).Programs.Select(p=>p with{Angle=p.Angle*1.1}).ToImmutableArray()}:((ShellRecipe)recipe) with{Thickness=.8};
        await f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,changed));Assert.True(f.Session.Snapshot.Features[successor.Id].IsStale);Assert.Equal(successor.Result,f.Session.Snapshot.Features[successor.Id].Result);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task DrivenFaceFeatureRefreshesDrawingAndStepRoundtrip(bool draft)
    {
        using var files=new TestFiles();await using var f=new Fixture();await f.Initialize();GeometryRecipe recipe=draft?f.Draft():f.Shell();
        await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Face feature"));var feature=f.Session.Snapshot.Features.Values.Single(p=>p.Id!=f.Source.Id);
        var sheet=TechnicalDrawingSheet.A4Landscape("Face drawing");await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(70,90),1));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Section",DrawingViewKind.Section,new(140,90),1,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:5));
        var before=f.Session.Snapshot;var parameter=new CadParameter(Guid.NewGuid(),"dimension",draft?ParameterDimension.Angle:ParameterDimension.Length,draft?"5 deg":"0.5 mm");
        var property=draft?((DraftRecipe)recipe).Programs[0].AngleProperty:"Thickness";var binding=new ParameterBinding(Guid.NewGuid(),parameter.Id,ParameterTarget.Feature(feature.Id,property));
        await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[binding])));var after=f.Session.Snapshot;after.Validate();
        Assert.All(after.DrawingSheets[sheet.Id].Views,v=>{Assert.Null(v.StaleReason);Assert.NotEmpty(v.Strokes);Assert.Equal(after.Features[feature.Id].Result.AssetId,v.Sources.Single().Asset);});
        int count=f.Assets.Count;await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(new SetParametersCommand(new([parameter with{Expression=draft?"90 deg":"6 mm"}],[binding]))));Assert.Same(after,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
        var path=files.PathFor("face-drawing.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);using(var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets))
        {loaded.Snapshot.Validate();TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("face.pdf"));Assert.True(new FileInfo(files.PathFor("face.pdf")).Length>1000);}
        var step=files.PathFor("face.step");await f.Kernel.ExportAsync(after,f.Assets,step);using(var loaded=await f.Kernel.ImportAsync(step,f.Assets))
        {Assert.Equal(after.Bodies.Values.Single().Geometry.VolumeMm3,loaded.Snapshot.Bodies.Values.Sum(b=>b.Geometry.VolumeMm3),4);Assert.All(loaded.Snapshot.Bodies.Values,b=>Assert.Equal(BodyKind.Solid,b.Geometry.Kind));loaded.Snapshot.Validate();}
        Assert.Equal(count,f.Assets.Count);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public OcctGeometryKernel Kernel {get;}=new();public CadDocumentSession Session {get;}public FeatureDefinition Source {get;set;}=null!;
        public ExactTopologySelection Top {get;private set;}=null!;public ExactTopologySelection Bottom {get;private set;}=null!;public ExactTopologySelection Left {get;private set;}=null!;public ExactTopologySelection Right {get;private set;}=null!;
        public Fixture()=>Session=new(DocumentSnapshot.Create("Face features"),Assets,Kernel,new InlineSessionDispatcher());
        public async Task Initialize(){await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Box"));Source=Session.Snapshot.Features.Values.Single();await Pick();}
        public Task Pick()
        {
            using var shape=OcctGeometryBridge.ReadShape(Source.Result,Assets);using var map=RepairSnapshot.Create(shape);
            ExactTopologySelection Choose(Func<BoundingBox3d,bool> predicate)
            {
                var item=map.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return predicate(face.GetBoundingBox());});
                return new(Session.Snapshot.Id,Source.Id,Source.Result.Revision,Source.Result.AssetId,map.Fingerprint,item.Selection.Index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);
            }
            Top=Choose(b=>Math.Abs(b.Minimum.Z-15)<1e-5&&Math.Abs(b.Maximum.Z-15)<1e-5);Bottom=Choose(b=>Math.Abs(b.Minimum.Z)<1e-5&&Math.Abs(b.Maximum.Z)<1e-5);
            Left=Choose(b=>Math.Abs(b.Minimum.X)<1e-5&&Math.Abs(b.Maximum.X)<1e-5);Right=Choose(b=>Math.Abs(b.Minimum.X-Source.Result.Bounds.Max.X)<1e-5&&Math.Abs(b.Maximum.X-Source.Result.Bounds.Max.X)<1e-5);return Task.CompletedTask;
        }
        public ShellRecipe Shell(bool outward=false,bool both=false)=>new(Source.Result,both?[Top,Bottom]:[Top],1,outward);
        public DraftRecipe Draft(double angle=.05)=>new(Source.Result,[new(Guid.NewGuid(),Left,angle,RigidTransform3d.Identity,Vector3d.UnitZ),new(Guid.NewGuid(),Right,2*angle,RigidTransform3d.Identity,Vector3d.UnitZ)]);
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
