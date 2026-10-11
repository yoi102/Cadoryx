using System.Collections.Immutable;
using System.Globalization;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchSlidingContactIntegrationTests
{
    private static ParameterBook Driver(CadSketch s,string field,double value,bool reference=false)
    {
        var c=s.Constraints.OfType<FiniteParametricContactConstraint>().Single();var target=ParameterTarget.Sketch(s.Id,c.Id,field);
        var parameter=new CadParameter(Guid.NewGuid(),"contact_input",ParameterDimension.Scalar,
            reference?"":value.ToString("R",CultureInfo.InvariantCulture),reference?target:null);
        return new([parameter],reference?[]:[new(Guid.NewGuid(),parameter.Id,target)]);
    }
    [Theory][InlineData(FiniteContactParameterMode.SolveA)][InlineData(FiniteContactParameterMode.SolveB)][InlineData(FiniteContactParameterMode.SolveBoth)]
    public async Task CommandsUndoRedoAndCurrentStorageRetainSeedsBoundsAndSolvedParameters(FiniteContactParameterMode mode)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var source=SketchSlidingContactSolveTests.Pair("Ellipse",false,FiniteContactContinuity.Curvature,mode) with{PartId=part};
        await session.ExecuteAsync(new UpsertSketchCommand(source,new ManagedSketchConstraintSolver()));
        var after=session.Snapshot;var solved=after.Sketches[source.Id];var c=solved.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        SketchContactSolutionVerification.Validate(source,solved);Assert.True(session.CanUndo);
        await session.UndoAsync();Assert.False(session.Snapshot.Sketches.ContainsKey(source.Id));
        await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var storage=new CadDocumentStorage();string path=files.PathFor("sliding.cadoryx");await storage.SaveAsync(after,assets,path);
        var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
        Assert.Equal(22,manifest.Sections.Single(s=>s.Kind=="sketches").SchemaVersion);
        Assert.Contains("cadoryx.sketch-sliding-contact-parameters.1",manifest.RequiredCapabilities);
        using var opened=await storage.LoadAsync(path,assets);
        Assert.Equal(c,opened.Snapshot.Sketches[source.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single());
        Assert.Equal(0,assets.Count);
    }
    [Fact]
    public async Task ActualAutomaticParameterIsReferenceOnlyAndSeedDriverRecomputesAtomically()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var source=SketchSlidingContactSolveTests.Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveA) with{PartId=part};
        await session.ExecuteAsync(new UpsertSketchCommand(source,new ManagedSketchConstraintSolver()));var sketch=session.Snapshot.Sketches[source.Id];
        var contact=sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();var targets=ParameterTargets.All(session.Snapshot).Where(t=>t.Constraint==contact.Id.Value).ToArray();
        Assert.DoesNotContain(targets,t=>t.Property=="ParameterA");Assert.Contains(targets,t=>t.Property=="SeedA");Assert.Contains(targets,t=>t.Property=="LowerA");Assert.Contains(targets,t=>t.Property=="UpperA");
        Assert.Contains(targets,t=>t.Property=="ParameterB");
        var before=session.Snapshot;long generation=session.Generation;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(Driver(sketch,"ParameterA",.37))));
        Assert.Same(before,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(0,assets.Count);
        await session.ExecuteAsync(new SetParametersCommand(Driver(sketch,"ParameterA",0,true)));
        Assert.Equal(contact.ParameterA,ParameterGraph.Evaluate(session.Snapshot).Values.Single().Value);
        await session.ExecuteAsync(new SetParametersCommand(Driver(sketch,"SeedA",.36)));
        var current=session.Snapshot.Sketches[source.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single();
        Assert.Equal(.36,current.SlidingA!.Seed);Assert.InRange(current.ParameterA,.3699999,.3700001);
        Assert.Equal(contact.SlidingA!.Lower,current.SlidingA.Lower);Assert.Equal(contact.SlidingA.Upper,current.SlidingA.Upper);
        before=session.Snapshot;generation=session.Generation;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(Driver(session.Snapshot.Sketches[source.Id],"SeedA",1))));
        Assert.Same(before,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(0,assets.Count);
    }
    [Fact]
    public async Task SwitchingToAutomaticRejectsAnExistingFixedDesignBindingWithoutDeletingIt()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var source=SketchSlidingContactSolveTests.Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.Fixed) with{PartId=part};
        await session.ExecuteAsync(new UpsertSketchCommand(source,new ManagedSketchConstraintSolver()));
        await session.ExecuteAsync(new SetParametersCommand(Driver(source,"ParameterA",.37)));
        var before=session.Snapshot;var old=before.Sketches[source.Id];var c=old.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        var changed=old with{Constraints=old.Constraints.Replace(c,c with{ParameterMode=FiniteContactParameterMode.SolveA,SlidingA=new(.34,.2,.6)})};
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(changed,new ManagedSketchConstraintSolver())));
        Assert.Same(before,session.Snapshot);Assert.Single(session.Snapshot.Parameters.Bindings);Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("document")][InlineData("sketches")][InlineData("both")]
    public async Task NewSlidingAuthorsCannotBeSmuggledThroughEitherLegacySection(string section)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var source=SketchSlidingContactSolveTests.Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveBoth) with{PartId=part};
        var result=await new ManagedSketchConstraintSolver().SolveAsync(source);Assert.True(result.Succeeded,result.Diagnostic);
        string path=files.PathFor("legacy-sliding.cadoryx");await storage.SaveAsync(initial with{Sketches=initial.Sketches.Add(source.Id,result.Solution!)},assets,path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>
            s.Kind=="document"&&section is "document" or "both"?s with{SchemaVersion=51}:
            s.Kind=="sketches"&&section is "sketches" or "both"?s with{SchemaVersion=20}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(FiniteContactContinuity.Tangent)][InlineData(FiniteContactContinuity.Curvature)]
    public async Task ActualB15FixedAuthorMigratesIndependentlyWithoutCreatingAutomaticState(FiniteContactContinuity continuity)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var source=SketchSlidingContactSolveTests.Pair("Ellipse",false,continuity,FiniteContactParameterMode.Fixed) with{PartId=part};
        string path=files.PathFor("actual-fixed.cadoryx");await storage.SaveAsync(initial with{Sketches=initial.Sketches.Add(source.Id,source)},assets,path);
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="document"?s with{SchemaVersion=51}:s.Kind=="sketches"?s with{SchemaVersion=20}:s).ToImmutableArray()});
        using var opened=await storage.LoadAsync(path,assets);var c=opened.Snapshot.Sketches[source.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single();
        Assert.Equal(FiniteContactParameterMode.Fixed,c.ParameterMode);Assert.Null(c.SlidingA);Assert.Null(c.SlidingB);
        Assert.Equal(source.Constraints.OfType<FiniteParametricContactConstraint>().Single(),c);
    }
    [Theory][InlineData("mode")][InlineData("seed")][InlineData("hidden-fixed-side")][InlineData("length")]
    public async Task MalformedSlidingAuthorOrHiddenStateIsRejectedEvenWhenDisabled(string change)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var source=SketchSlidingContactSolveTests.Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveA) with{PartId=part};
        source=source with{Constraints=source.Constraints.Select(c=>c is FiniteParametricContactConstraint?c with{IsEnabled=false}:c).ToImmutableArray()};
        string path=files.PathFor("bad-sliding.cadoryx");await storage.SaveAsync(initial with{Sketches=initial.Sketches.Add(source.Id,source)},assets,path);
        FormatEvolutionTests.RewriteSection(path,"sketches",bytes=>
        {
            var pack=MessagePackSerializer.Deserialize<PackSketches>(bytes);var rows=pack.Sketches[0].Constraints;
            int index=Array.FindIndex(rows,c=>c.Kind=="sliding-parametric-contact");var c=rows[index];var values=(double[])c.Values.Clone();
            if(change=="mode")values[4]=1;else if(change=="seed")values[5]=0;else if(change=="hidden-fixed-side")values[8]=.3;else values=values[..^1];
            rows[index]=c with{Values=values};return MessagePackSerializer.Serialize(pack);
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
    }
}
