using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.IO;
using MessagePack;
using OcctSharp;
using Xunit;
using Operation=Cadoryx.Db.LocalFeatureOperation;

namespace Cadoryx.Tests;

public sealed partial class ContourLocalTests
{
    private static ContourRadiusLaw TestLaw(string mode)=>mode switch
    {
        "constant"=>new ConstantContourRadius(.5),"linear"=>new LinearContourRadius(.5,1.5),"smooth"=>new SmoothContourRadius(.5,1.5),
        "sampled"=>new SampledContourRadius([new(Guid.NewGuid(),0,.5),new(Guid.NewGuid(),.5,1.2),new(Guid.NewGuid(),1,1.5)]),
        "spline"=>new SplineContourRadius([new(Guid.NewGuid(),.5),new(Guid.NewGuid(),1.5),new(Guid.NewGuid(),1)],[0,1],[3,3],2),
        _=>throw new ArgumentOutOfRangeException(nameof(mode))
    };
    private static async Task<FeatureDefinition> AddTestContour(Fixture f,string mode)
    {
        var seed=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>7.49);
        var chain=await f.Kernel.DiscoverContourAsync(f.Session.Snapshot,seed,Operation.Fillet,null,f.Assets);
        await f.Session.ExecuteAsync(new AddContourLocalCommand(f.Source.OutputBodyId,new(f.Source.Result,Operation.Fillet,[new(Guid.NewGuid(),seed,chain,TestLaw(mode))]),"Contour"));
        return f.Session.Snapshot.Features.Values.Single(f=>f.Recipe is ContourLocalRecipe);
    }
    [Theory][InlineData("constant")][InlineData("linear")][InlineData("smooth")][InlineData("sampled")][InlineData("spline")]
    public async Task NativeDrivenLawAndStableRowsSurviveExactReopen(string mode)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(true);var feature=await AddTestContour(f,mode);var recipe=(ContourLocalRecipe)feature.Recipe;var p=recipe.Programs.Single();
        string property=mode switch
        {
            "constant"=>ContourParameterTargets.Property(p.Id,"Radius"),"linear"=>ContourParameterTargets.Property(p.Id,"Last"),
            "smooth"=>ContourParameterTargets.Property(p.Id,"FirstDerivative"),
            "sampled"=>ContourParameterTargets.Property(p.Id,"SampleRadius",((SampledContourRadius)p.Radius!).Samples[1].Id),
            _=>ContourParameterTargets.Property(p.Id,"PoleRadius",((SplineContourRadius)p.Radius!).Poles[1].Id)
        };
        double value=mode=="smooth"?.3:.8;var target=ParameterTarget.Feature(feature.Id,property);
        Assert.Contains(target,ParameterTargets.All(f.Session.Snapshot));Assert.True(ParameterTargets.Read(f.Session.Snapshot,target).HasDimension(ParameterDimension.Length));
        var parameter=new CadParameter(Guid.NewGuid(),"radius",ParameterDimension.Length,$"{value} mm");var binding=new ParameterBinding(Guid.NewGuid(),parameter.Id,target);
        var before=f.Session.Snapshot;await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[binding])));var after=f.Session.Snapshot;
        Assert.Equal(value,ParameterTargets.Read(after,target).Value);Assert.NotEqual(feature.Result.AssetId,after.Features[feature.Id].Result.AssetId);
        Assert.NotEqual(feature.Result.VolumeMm3,after.Features[feature.Id].Result.VolumeMm3);
        if(mode=="constant")Assert.Equal(1800-15*(1-Math.PI/4)*value*value,after.Features[feature.Id].Result.VolumeMm3,4);
        if(mode=="linear")Assert.Equal(1800-15*(1-Math.PI/4)*(.25+.5*value+value*value)/3,after.Features[feature.Id].Result.VolumeMm3,3);
        int count=f.Assets.Count;var history=f.Session.HistoryUsage;
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(new SetParametersCommand(new([parameter with{Expression=mode=="smooth"?"-10 mm":"-1 mm"}],[binding]))));
        Assert.Same(after,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);Assert.Equal(history,f.Session.HistoryUsage);
        var path=files.PathFor(mode+".cadoryx");var storage=new CadDocumentStorage(contourResolver:f.Kernel);await f.Session.SaveAsync(storage,path);
        var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["document"].Version,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["features"].Version,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);Assert.Contains("cadoryx.contour-local.1",manifest.RequiredCapabilities);
        using(var loaded=await storage.LoadAsync(path,f.Assets))
        {
            loaded.Snapshot.Validate();Assert.Equal(value,ParameterTargets.Read(loaded.Snapshot,target).Value);
            var restored=(ContourLocalRecipe)loaded.Snapshot.Features[feature.Id].Recipe;Assert.Equal(p.Id,restored.Programs[0].Id);
            Assert.Equal(p.Chain.Members.ToArray(),restored.Programs[0].Chain.Members.ToArray());Assert.Equal(p.Chain.Length,restored.Programs[0].Chain.Length);
            Assert.Equal(property,loaded.Snapshot.Parameters.Bindings.Single().Target.Property);
            Assert.Equal(after.Features[feature.Id].Result,loaded.Snapshot.Features[feature.Id].Result);
        }
        Assert.Equal(count,f.Assets.Count);await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }
    [Theory][InlineData("family")][InlineData("sample")][InlineData("pole")]
    public async Task RetainedDriverCannotChangeLawFamilyOrRowIdentity(string change)
    {
        await using var f=await Fixture.Create();var feature=await AddTestContour(f,change=="family"?"linear":change=="sample"?"sampled":"spline");
        var recipe=(ContourLocalRecipe)feature.Recipe;var p=recipe.Programs[0];
        string property=change=="family"?ContourParameterTargets.Property(p.Id,"Last"):change=="sample"?
            ContourParameterTargets.Property(p.Id,"SampleRadius",((SampledContourRadius)p.Radius!).Samples[1].Id):ContourParameterTargets.Property(p.Id,"PoleRadius",((SplineContourRadius)p.Radius!).Poles[1].Id);
        var target=ParameterTarget.Feature(feature.Id,property);var parameter=new CadParameter(Guid.NewGuid(),"kept",ParameterDimension.Length,$"{ParameterTargets.Read(f.Session.Snapshot,target).Value} mm");
        var binding=new ParameterBinding(Guid.NewGuid(),parameter.Id,target);await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[binding])));
        ContourRadiusLaw law=change switch
        {
            "family"=>new SmoothContourRadius(.5,1.5),
            "sample"=>((SampledContourRadius)p.Radius!) with{Samples=((SampledContourRadius)p.Radius!).Samples.SetItem(1,((SampledContourRadius)p.Radius!).Samples[1] with{Id=Guid.NewGuid()})},
            _=>((SplineContourRadius)p.Radius!) with{Poles=((SplineContourRadius)p.Radius!).Poles.SetItem(1,((SplineContourRadius)p.Radius!).Poles[1] with{Id=Guid.NewGuid()})}
        };
        var edited=recipe with{Programs=[p with{Radius=law}]};var before=f.Session.Snapshot;int count=f.Assets.Count;var history=f.Session.HistoryUsage;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,edited)));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);Assert.Equal(history,f.Session.HistoryUsage);
        await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[])));await f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,edited));
        Assert.Equal(p.Id,((ContourLocalRecipe)f.Session.Snapshot.Features[feature.Id].Recipe).Programs[0].Id);
    }
    [Theory][InlineData("legacy")][InlineData("kind")][InlineData("companion")][InlineData("length")][InlineData("order")][InlineData("endpoint")][InlineData("interval")][InlineData("fingerprint")][InlineData("law-companion")][InlineData("sample-id")][InlineData("program-id")]
    public async Task StoredContourProofAndCompanionsAreReverifiedAtomically(string mutation)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(true);await AddTestContour(f,"sampled");
        var storage=new CadDocumentStorage(contourResolver:f.Kernel);var path=files.PathFor("tampered.cadoryx");await f.Session.SaveAsync(storage,path);int count=f.Assets.Count;
        if(mutation=="legacy")FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=36}:s).ToImmutableArray()});
        else FormatEvolutionTests.RewriteSection(path,"features",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
            return MessagePackSerializer.Serialize(data with{Features=data.Features.Select(feature=>
            {
                if(feature.Recipe.ContourLocal is not {} contour)return feature;var p=contour.Programs[0];var c=p.Chain;var law=p.Radius!;
                var altered=mutation switch
                {
                    "length"=>p with{Chain=c with{Length=c.Length+1}},"order"=>p with{Chain=c with{Members=c.Members.Reverse().ToArray()}},
                    "endpoint"=>p with{Chain=c with{FirstVertex=c.LastVertex}},"interval"=>p with{Chain=c with{Members=c.Members.Select((e,i)=>i==0?e with{LastParameter=.49}:e).ToArray()}},
                    "fingerprint"=>p with{Seed=p.Seed with{Fingerprint="forged"}},"law-companion"=>p with{Radius=law with{Poles=[new(Guid.NewGuid(),1)]}},
                    "sample-id"=>p with{Radius=law with{Samples=law.Samples!.Select((s,i)=>i==1?s with{Id=law.Samples![0].Id}:s).ToArray()}},
                    "program-id"=>p with{Id=Guid.Empty},_=>p
                };
                var r=feature.Recipe with{ContourLocal=contour with{Programs=[altered]}};
                if(mutation=="kind")r=r with{Kind="import"};if(mutation=="companion")r=r with{FaceFeature=new()};
                return feature with{Recipe=r};
            }).ToArray()});
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }
    [Fact]public async Task SaveRejectsForgedHistoricalContourBeforeReplacingValidFileAndFrozenRecipeReopens()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();var feature=await AddTestContour(f,"linear");var recipe=(ContourLocalRecipe)feature.Recipe;
        var storage=new CadDocumentStorage(contourResolver:f.Kernel);var path=files.PathFor("preserve.cadoryx");await f.Session.SaveAsync(storage,path);var bytes=File.ReadAllBytes(path);int count=f.Assets.Count;
        var p=recipe.Programs[0];var forged=f.Session.Snapshot with{Features=f.Session.Snapshot.Features.SetItem(feature.Id,feature with{Recipe=recipe with{Programs=[p with{Chain=p.Chain with{Length=p.Chain.Length+1}}]}})};
        await Assert.ThrowsAsync<CadValidationException>(()=>storage.SaveAsync(forged,f.Assets,path));Assert.Equal(bytes,File.ReadAllBytes(path));Assert.Equal(count,f.Assets.Count);
        await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));Assert.True(f.Session.Snapshot.Features[feature.Id].IsStale);
        await f.Session.SaveAsync(storage,path);count=f.Assets.Count;using(var loaded=await storage.LoadAsync(path,f.Assets))
        {Assert.True(loaded.Snapshot.Features[feature.Id].IsStale);Assert.Equal(feature.Result,loaded.Snapshot.Features[feature.Id].Result);Assert.Equal(recipe.Source,((ContourLocalRecipe)loaded.Snapshot.Features[feature.Id].Recipe).Source);}
        Assert.Equal(count,f.Assets.Count);
    }
    [Fact]public async Task Version40OffsetDocumentMigratesThroughItsOwnIntermediateFormat()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();await f.Session.ExecuteAsync(new AddSolidOffsetCommand(f.Source.OutputBodyId,new(f.Source.Result,-1),"Offset"));
        var path=files.PathFor("v40.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,path);
        // Emulate the actual legacy four-field point payload before declaring its document version.
        SketchLegacyPointFixture.Prepare(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind is "document" or "features"?s with{SchemaVersion=s.Kind=="document"?40:36}:s).ToImmutableArray()});
        using var loaded=await storage.LoadAsync(path,f.Assets);loaded.Snapshot.Validate();Assert.Equal(1040,loaded.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,4);
    }
}
