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

public sealed class RepetitionStyleTests
{
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets {get;}=new();internal OcctGeometryKernel Kernel {get;}=new();
        internal CadDocumentSession Session {get;}internal FeatureDefinition Source {get;}
        internal Fixture(bool uniform=false,bool hidden=false,ShapeKind? markedContainers=null)
        {
            using var files=new TestFiles();using var native=XdeDocument.Create();using var shape=markedContainers is null?ShapeFactory.CreateBox(4,3,2):ContainerSource();
            XdeLabel root;
            using(var edit=native.BeginTransaction("Distinct direct presentation"))
            {
                root=native.AddShape(shape,"Source");root.SetPresentationColor(XdeColorChannel.General,new(.2,.4,.8,.25));
                root.SetPresentationColor(XdeColorChannel.Surface,new(.1,.3,.7,.5));root.SetPresentationColor(XdeColorChannel.Curve,new(.5,.5,.5,.75));
                root.AddLayer("Manufacturing");root.Material=new("Alloy","Density",2.7,"g/cm3","density");
                root.IsVisible=!hidden;root.UpdateValidationPropertiesFromShape();root.Comment="Not valid for transformed geometry";
                using var map=new RepairDocumentSession(native,root);int face=0;
                foreach(var slot in map.Source.Topology.Where(t=>t.Kind==ShapeKind.Face))
                {
                    var label=map.GetOrCreateSubshapeLabel(slot.Selection);
                    label.SetPresentationColor(XdeColorChannel.Surface,uniform?new(.4,.7,.2,.5):new((face%6)/6.0,1-(face%6)/6.0,.2,.5));
                    label.SetPresentationColor(XdeColorChannel.Curve,new(0,.2,.9,.4));label.AddLayer(uniform?"Shared surface":"Face "+face);
                    label.VisualMaterial=new("Coating",uniform?new(.4,.7,.2,.5):new((face%6)/6.0,1-(face%6)/6.0,.2,.5),.4,.6,new(0,0,0));label.IsVisible=uniform||face!=3;
                    face++;
                }
                foreach(var slot in map.Source.Topology.Skip(1).Where(t=>t.Kind==markedContainers))
                {
                    var label=map.GetOrCreateSubshapeLabel(slot.Selection);
                    label.SetPresentationColor(XdeColorChannel.General,new(.8,.2,.1));label.AddLayer("Container "+slot.Selection.Index);
                    label.Material=new("Container alloy","Density",7.8,"g/cm3","density");label.IsVisible=slot.Selection.Index%2==0;
                }
                edit.Commit();
            }
            var path=files.PathFor("source.xbf");native.Save(path);using var context=Assets.Stage(File.ReadAllBytes(path));
            using var stored=OcctGeometryBridge.StoreShape(shape,Assets);
            var geometry=stored.Geometry with{Source=new(context.Id,root.Entry,AssetFormatPolicy.CurrentXde)};
            var doc=DocumentSnapshot.Create("Multicopy presentation");var part=DefinitionId.New();var body=BodyId.New();var feature=FeatureId.New();
            var sourceBody=new CadBody(body,part,"Source",geometry,feature,doc.Layers.Keys.First(),new(0xFF86ACC5,PreserveSourceStyles:true));
            Source=new(feature,part,"Source",new ImportedRecipe(geometry),[],body,geometry,OutputMetadata:BodyOutputMetadata.FromBody(sourceBody));
            var assembly=(AssemblyDefinition)doc.Definitions[doc.RootAssemblyId];
            doc=(doc with{Bodies=doc.Bodies.Add(body,sourceBody),Features=doc.Features.Add(feature,Source),Definitions=doc.Definitions.Add(part,
                new PartDefinition(part,"Part",[body],[feature])).SetItem(assembly.Id,assembly with{Children=assembly.Children.Add(new(ComponentSlotId.New(),part,"Source",RigidTransform3d.Identity))})}).WithNewState();
            Session=new(doc,Assets,Kernel,new InlineSessionDispatcher());
        }
        private static Shape ContainerSource()
        {
            using var first=ShapeFactory.CreateBox(4,3,2);using var second=ShapeFactory.CreateBox(2,2,3);
            using var placement=GpTrsf.Create(12,0,0);using var moved=second.Transformed(placement);
            return ShapeFactory.CreateCompound([first,moved]);
        }
        internal RepetitionRecipe Layout(RepetitionKind kind=RepetitionKind.Linear,bool include=true,RepetitionOutput output=RepetitionOutput.Compound,
            RepetitionStylePolicy policy=RepetitionStylePolicy.VerifiedInheritance)
        {
            int count=kind==RepetitionKind.Mirror?(include?2:1):3;
            var frame=kind==RepetitionKind.Mirror?new RigidTransform3d(new(2,0,0),Quaterniond.FromEulerDegrees(0,90,0)):
                new RigidTransform3d(new(20,-3,5),Quaterniond.FromAxisAngle(new(1,2,3),.317));
            return new(Source.Result,kind,frame,count,kind==RepetitionKind.Mirror||kind==RepetitionKind.Circular?0:12,
                Enumerable.Range(0,count).Select(_=>Guid.NewGuid()).ToImmutableArray(),output,include,kind==RepetitionKind.Circular,StylePolicy:policy);
        }
        internal async Task<FeatureDefinition> Repeat(RepetitionRecipe recipe)
        {
            await Session.ExecuteAsync(new AddRepetitionCommand(Source.OutputBodyId,recipe,"Copies"));
            return Session.Snapshot.Features.Values.Single(f=>f.Recipe is RepetitionRecipe);
        }
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }

    [Theory][InlineData(RepetitionKind.Linear,true,false)][InlineData(RepetitionKind.Circular,true,false)]
    [InlineData(RepetitionKind.Mirror,true,false)][InlineData(RepetitionKind.Mirror,false,true)]
    public async Task EveryMarkedFaceFollowsItsNativeCopyWithIndependentChannelsMaterialsLayersAndVisibility(RepetitionKind kind,bool include,bool hidden)
    {
        await using var f=new Fixture(hidden:hidden);using var files=new TestFiles();var recipe=f.Layout(kind,include);
        var sourceBytes=Bytes(f.Assets,f.Source.Result.AssetId);var contextBytes=Bytes(f.Assets,f.Source.Result.Source!.ContextAssetId);
        var feature=await f.Repeat(recipe);var evidence=Assert.IsType<RepetitionPresentationEvidence>(feature.RepetitionPresentation);
        Assert.Equal(6,evidence.StyledSourceSlots);Assert.Equal(6*recipe.Count,evidence.CopiedResultSlots);Assert.True(evidence.CompleteHistory);Assert.Empty(evidence.ConflictingResultIndices);
        CheckNativePresentation(f.Source.Result,feature,f.Assets);Assert.Equal(24*recipe.Count,feature.Result.VolumeMm3,6);
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);var path=files.PathFor("copies.cadoryx");await f.Session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,f.Assets))
        {
            var restored=loaded.Snapshot.Features[feature.Id];CheckNativePresentation(f.Source.Result,restored,f.Assets);
            Assert.Equal(recipe.CopyIds.ToArray(),Assert.IsType<RepetitionRecipe>(restored.Recipe).CopyIds.ToArray());
        }
        Assert.Equal(sourceBytes,Bytes(f.Assets,f.Source.Result.AssetId));Assert.Equal(contextBytes,Bytes(f.Assets,f.Source.Result.Source!.ContextAssetId));
    }

    [Fact] public async Task IncompatibleFusedFaceContributorsRejectAtomicallyAndExplicitWholeBodyChoicePersists()
    {
        await using var f=new Fixture();using var files=new TestFiles();var before=f.Session.Snapshot;int count=f.Assets.Count;
        var recipe=f.Layout(RepetitionKind.Mirror,output:RepetitionOutput.Fused);
        var failure=await Assert.ThrowsAsync<CadValidationException>(()=>f.Repeat(recipe));Assert.Contains("STYLES.CONFLICT",failure.Message);
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
        var feature=await f.Repeat(recipe with{StylePolicy=RepetitionStylePolicy.WholeBody});var proof=feature.RepetitionPresentation!;
        Assert.NotEmpty(proof.ConflictingResultIndices);Assert.Equal(0,proof.CopiedResultSlots);Assert.Equal(24,feature.Result.VolumeMm3,6);
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);var path=files.PathFor("whole-body.cadoryx");await f.Session.SaveAsync(storage,path);
        using var loaded=await storage.LoadAsync(path,f.Assets);
        Assert.Equal(RepetitionStylePolicy.WholeBody,Assert.IsType<RepetitionRecipe>(loaded.Snapshot.Features[feature.Id].Recipe).StylePolicy);
        Assert.Equal(proof.ConflictingResultIndices.ToArray(),loaded.Snapshot.Features[feature.Id].RepetitionPresentation!.ConflictingResultIndices.ToArray());
    }

    [Theory][InlineData(ShapeKind.Solid)][InlineData(ShapeKind.Shell)][InlineData(ShapeKind.Wire)]
    public async Task ContainerPresentationUsesCompleteNativeMembershipRatherThanAnAssumedContainerIndex(ShapeKind kind)
    {
        await using var f=new Fixture(markedContainers:kind);using var files=new TestFiles();var feature=await f.Repeat(f.Layout());
        using var original=OcctGeometryBridge.ReadContext(f.Source.Result.Source!.ContextAssetId,f.Assets);
        using var result=OcctGeometryBridge.ReadContext(feature.Result.Source!.ContextAssetId,f.Assets);
        var sourceRoot=original.GetLabel(f.Source.Result.Source.DefinitionEntry);var resultRoot=result.GetLabel(feature.Result.Source.DefinitionEntry);
        using var sourceMap=new RepairDocumentSession(original,sourceRoot);
        using var resultMap=new RepairDocumentSession(result,resultRoot);
        using var readA=original.BeginTransaction("Inspect source container proof");using var readB=result.BeginTransaction("Inspect result container proof");
        var sourceLabels=sourceMap.Source.Topology.ToDictionary(t=>t.Selection.Index,t=>t.Selection.Index==0?sourceRoot:sourceMap.GetOrCreateSubshapeLabel(t.Selection));
        var resultLabels=resultMap.Source.Topology.ToDictionary(t=>t.Selection.Index,t=>t.Selection.Index==0?resultRoot:resultMap.GetOrCreateSubshapeLabel(t.Selection));
        var containers=sourceMap.Source.Topology.Skip(1).Where(t=>t.Kind==kind).ToArray();
        var leaves=sourceMap.Source.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex).ToArray();
        foreach(var container in containers)
        {
            var expected=sourceLabels[container.Selection.Index];using var sourceShape=expected.Shape;
            var members=new List<int>();
            foreach(var leaf in leaves){using var shape=sourceLabels[leaf.Selection.Index].Shape;if(RepairSnapshot.FindTopologyIndex(sourceShape,shape)>=0)members.Add(leaf.Selection.Index);}
            foreach(var copy in ((RepetitionRecipe)feature.Recipe).CopyIds)
            {
                var mapped=members.Select(i=>Assert.Single(feature.TopologyHistory!.Entries,e=>e.CopyId==copy&&e.SourceIndex==i).ResultIndex!.Value).ToHashSet();
                var matches=new List<XdeLabel>();
                foreach(var candidate in resultMap.Source.Topology.Skip(1).Where(t=>t.Kind==kind))
                {
                    var label=resultLabels[candidate.Selection.Index];using var target=label.Shape;var actualMembers=new HashSet<int>();
                    foreach(var leaf in resultMap.Source.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex))
                    {using var member=resultLabels[leaf.Selection.Index].Shape;if(RepairSnapshot.FindTopologyIndex(target,member)>=0)actualMembers.Add(leaf.Selection.Index);}
                    if(mapped.SetEquals(actualMembers))matches.Add(label);
                }
                var actual=Assert.Single(matches);Assert.Equal(expected.GetPresentationColors(),actual.GetPresentationColors());
                Assert.Equal(expected.Layers,actual.Layers);Assert.Equal(expected.Material,actual.Material);
            }
        }
        Assert.Equal(12+containers.Length,feature.RepetitionPresentation!.StyledSourceSlots);
        Assert.Equal(3*(12+containers.Length),feature.RepetitionPresentation.CopiedResultSlots);
        await f.Kernel.ConfirmRepetitionPresentationAsync(feature,f.Assets);
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);var file=files.PathFor("containers.cadoryx");await f.Session.SaveAsync(storage,file);
        using var reopened=await storage.LoadAsync(file,f.Assets);Assert.Equal(feature.RepetitionPresentation,reopened.Snapshot.Features[feature.Id].RepetitionPresentation);
    }

    [Fact] public async Task MergedContainerBoundaryRejectsInheritanceAndKeepsTheDocumentAtomic()
    {
        await using var f=new Fixture(uniform:true,markedContainers:ShapeKind.Solid);var before=f.Session.Snapshot;int count=f.Assets.Count;
        var recipe=f.Layout(RepetitionKind.Mirror,output:RepetitionOutput.Fused);
        var failure=await Assert.ThrowsAsync<CadValidationException>(()=>f.Repeat(recipe));Assert.Contains("STYLES.CONTAINER",failure.Message);
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
        var feature=await f.Repeat(recipe with{StylePolicy=RepetitionStylePolicy.WholeBody});Assert.Equal(0,feature.RepetitionPresentation!.CopiedResultSlots);
        await f.Kernel.ConfirmRepetitionPresentationAsync(feature,f.Assets);
    }

    [Fact] public async Task EqualPresentationCanMergeWhileCopyHistoryRemainsAmbiguous()
    {
        await using var f=new Fixture(uniform:true);var recipe=f.Layout(RepetitionKind.Mirror,output:RepetitionOutput.Fused);var feature=await f.Repeat(recipe);
        var proof=feature.RepetitionPresentation!;Assert.Empty(proof.ConflictingResultIndices);Assert.True(proof.SharedResultSlots>0);Assert.Equal(6,proof.CopiedResultSlots);
        CheckNativePresentation(f.Source.Result,feature,f.Assets);
        var shared=Assert.Single(feature.TopologyHistory!.Entries.Where(e=>e.SourceKind==HistoryShapeKind.Face).Take(1));
        Assert.NotEqual(HistoryResolutionStatus.Resolved,TopologyHistoryReduction.ResolveCopy(feature.TopologyHistory,shared.CopyId!.Value,shared.SourceIndex,shared.SourceKind).Status);
        await f.Kernel.ConfirmRepetitionPresentationAsync(feature,f.Assets);
    }

    [Fact] public async Task ForgedReportOrForeignContextCannotReplaceAnExistingNativeDocument()
    {
        await using var f=new Fixture();using var files=new TestFiles();var feature=await f.Repeat(f.Layout());
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);var path=files.PathFor("verified.cadoryx");await f.Session.SaveAsync(storage,path);var original=File.ReadAllBytes(path);int count=f.Assets.Count;
        var badReport=feature with{RepetitionPresentation=feature.RepetitionPresentation! with{CopiedResultSlots=1}};
        var bad=f.Session.Snapshot with{Features=f.Session.Snapshot.Features.SetItem(feature.Id,badReport)};
        await Assert.ThrowsAsync<CadValidationException>(()=>storage.SaveAsync(bad,f.Assets,path));Assert.Equal(original,File.ReadAllBytes(path));
        var history=feature.TopologyHistory!;var faces=history.Entries.Where(e=>e.SourceKind==HistoryShapeKind.Face).Take(2).ToArray();
        var forged=history with{Entries=history.Entries.Select(e=>e==faces[0]?e with{ResultIndex=faces[1].ResultIndex}:
            e==faces[1]?e with{ResultIndex=faces[0].ResultIndex}:e).ToImmutableArray()};
        bad=f.Session.Snapshot with{Features=f.Session.Snapshot.Features.SetItem(feature.Id,feature with{TopologyHistory=forged})};bad.Validate();
        await Assert.ThrowsAsync<CadValidationException>(()=>storage.SaveAsync(bad,f.Assets,path));Assert.Equal(original,File.ReadAllBytes(path));
        var geometry=feature.Result with{Source=f.Source.Result.Source};var body=f.Session.Snapshot.Bodies[feature.OutputBodyId];
        bad=f.Session.Snapshot with{Features=f.Session.Snapshot.Features.SetItem(feature.Id,feature with{Result=geometry}),
            Bodies=f.Session.Snapshot.Bodies.SetItem(body.Id,body with{Geometry=geometry})};bad.Validate();
        await Assert.ThrowsAsync<CadValidationException>(()=>storage.SaveAsync(bad,f.Assets,path));Assert.Equal(original,File.ReadAllBytes(path));Assert.Equal(count,f.Assets.Count);
        await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().SaveAsync(f.Session.Snapshot,f.Assets,path));Assert.Equal(original,File.ReadAllBytes(path));
    }

    [Fact] public async Task LateCancellationDuringContextStagingReleasesBothAssetsAndPreservesTheSource()
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;int count=f.Assets.Count;
        using(var candidate=await f.Kernel.EvaluateAsync(f.Layout(),f.Assets)){Assert.NotNull(candidate.Geometry.Source);Assert.True(f.Assets.Count>count);}
        Assert.Equal(count,f.Assets.Count);using var cancellation=new CancellationTokenSource();
        var store=new CancelOnSecondStage(f.Assets,cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Kernel.EvaluateAsync(f.Layout(),store,cancellation.Token));
        Assert.Equal(2,store.Stages);Assert.Equal(count,f.Assets.Count);Assert.Same(before,f.Session.Snapshot);
    }

    [Fact] public async Task DrawingStopsAtMultipleCopiesAndAnExplicitCopyAttachmentContinuesThroughFilletThenFreezesOnResize()
    {
        await using var f=new Fixture();using var files=new TestFiles();
        using var sourceShape=OcctGeometryBridge.ReadShape(f.Source.Result,f.Assets);using var sourceMap=RepairSnapshot.Create(sourceShape);
        ExactTopologySelection Pick(HistoryShapeKind kind,Func<BoundingBox3d,bool> predicate)
        {
            var slots=sourceMap.Topology.Where(t=>t.Kind==(kind==HistoryShapeKind.Face?ShapeKind.Face:ShapeKind.Edge));
            int index=slots.First(t=>{using var member=sourceMap.CopySubshape(t.Selection);return predicate(member.GetBoundingBox());}).Selection.Index;
            return new(f.Session.Snapshot.Id,f.Source.Id,f.Source.Result.Revision,f.Source.Result.AssetId,sourceMap.Fingerprint,index,kind,OcctGeometryKernel.HistoryAdapterVersion);
        }
        var face=Pick(HistoryShapeKind.Face,b=>Math.Abs(b.Minimum.X-4)<1e-5&&Math.Abs(b.Maximum.X-4)<1e-5);
        var edge=Pick(HistoryShapeKind.Edge,b=>Math.Abs(b.Minimum.X)<1e-5&&Math.Abs(b.Maximum.X)<1e-5);
        var occurrence=f.Session.Snapshot.EnumerateOccurrences().Single();
        var author=await f.Kernel.ResolveDrawingTopologyAsync(f.Session.Snapshot,occurrence.Path,f.Source.OutputBodyId,face.FullTopologyIndex,face.Fingerprint,f.Assets);
        var sheet=DrawingSheetTemplate.Create("Explicit native copy");var view=Guid.NewGuid();
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,view,"Front",DrawingViewKind.Front,new(90,60),1));
        var datum=new DrawingDatumSymbol(Guid.NewGuid(),view,"A",author,new(35,30));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(sheet.Id,datum));
        var recipe=f.Layout();var repeated=await f.Repeat(recipe);
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(sheet.Id,view,null));
        var stopped=f.Session.Snapshot.DrawingSheets[sheet.Id].DatumSymbols.Single();
        Assert.NotNull(stopped.StaleReason);Assert.Equal(author,stopped.Source);Assert.Null(stopped.Source.HistoryOrigin);
        Assert.Contains(author.Asset,f.Session.Snapshot.ReferencedAssets());
        var faceCopy=await f.Kernel.TraceCopyAsync(f.Session.Snapshot,face,repeated.Id,repeated.Id,recipe.CopyIds[1],f.Assets);
        var edgeCopy=await f.Kernel.TraceCopyAsync(f.Session.Snapshot,edge,repeated.Id,repeated.Id,recipe.CopyIds[1],f.Assets);
        Assert.Equal(HistoryResolutionStatus.Resolved,faceCopy.Status);Assert.Equal(HistoryResolutionStatus.Resolved,edgeCopy.Status);
        var selected=faceCopy.VerifiedSelection!;
        var explicitAuthor=await f.Kernel.ResolveDrawingTopologyAsync(f.Session.Snapshot,occurrence.Path,repeated.OutputBodyId,selected.FullTopologyIndex,selected.Fingerprint,f.Assets);
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(sheet.Id,stopped with{Source=explicitAuthor},true));
        Assert.Null(f.Session.Snapshot.DrawingSheets[sheet.Id].DatumSymbols.Single().StaleReason);
        await f.Session.ExecuteAsync(new ExactLocalFeatureCommand(edgeCopy.VerifiedSelection!,Cadoryx.Db.LocalFeatureOperation.Fillet,.1));
        var fillet=f.Session.Snapshot.Features.Values.Single(v=>v.Recipe is HistoryFilletRecipe);
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(sheet.Id,view,null));
        var continued=f.Session.Snapshot.DrawingSheets[sheet.Id].DatumSymbols.Single();
        Assert.Null(continued.StaleReason);Assert.Equal(explicitAuthor,continued.Source.HistoryOrigin);Assert.Equal(fillet.Id,continued.Source.FeatureId);
        var downstream=await f.Kernel.TraceCopyAsync(f.Session.Snapshot,face,fillet.Id,repeated.Id,recipe.CopyIds[1],f.Assets);
        Assert.Equal(HistoryResolutionStatus.Resolved,downstream.Status);Assert.Equal(2,downstream.CompletedSteps);
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);var file=files.PathFor("copy-drawing.cadoryx");await f.Session.SaveAsync(storage,file);
        using(var reopened=await storage.LoadAsync(file,f.Assets))
        {
            Assert.Equal(continued.Source,reopened.Snapshot.DrawingSheets[sheet.Id].DatumSymbols.Single().Source);
            Assert.Equal(repeated.RepetitionPresentation,reopened.Snapshot.Features[repeated.Id].RepetitionPresentation);
            TechnicalDrawingPdf.Write(reopened.Snapshot,files.PathFor("copy-drawing.pdf"));
        }
        await f.Session.ExecuteAsync(new RecomputeCommand(repeated.Id,recipe.Resize(4)));
        var frozen=f.Session.Snapshot;Assert.True(frozen.Features[fillet.Id].IsStale);
        var old=frozen.DrawingSheets[sheet.Id].DatumSymbols.Single();Assert.NotNull(old.StaleReason);Assert.Equal(continued.Source,old.Source);
        Assert.Contains(explicitAuthor.Asset,frozen.ReferencedAssets());
        Assert.Throws<CadValidationException>(()=>TechnicalDrawingPdf.Write(frozen,files.PathFor("stale.pdf")));
    }

    [Fact] public async Task SourceReplayResizeAndSuppressionRetainPresentationAndStableCopyIds()
    {
        await using var f=new Fixture();using var files=new TestFiles();var recipe=f.Layout();var first=await f.Repeat(recipe);
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,f.Source.Recipe));
        var source=f.Session.Snapshot.Features[f.Source.Id];Assert.NotNull(source.Result.Source);
        var current=f.Session.Snapshot.Features[first.Id];Assert.NotNull(current.RepetitionPresentation);CheckNativePresentation(source.Result,current,f.Assets);
        var resized=Assert.IsType<RepetitionRecipe>(current.Recipe).Resize(2);
        await f.Session.ExecuteAsync(new RecomputeCommand(first.Id,resized));current=f.Session.Snapshot.Features[first.Id];
        Assert.Equal(recipe.CopyIds.Take(2),Assert.IsType<RepetitionRecipe>(current.Recipe).CopyIds);Assert.Equal(12,current.RepetitionPresentation!.CopiedResultSlots);
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(first.Id,true));Assert.Null(f.Session.Snapshot.Features[first.Id].RepetitionPresentation);
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(first.Id,false));current=f.Session.Snapshot.Features[first.Id];
        CheckNativePresentation(source.Result,current,f.Assets);Assert.Equal(12,current.RepetitionPresentation!.CopiedResultSlots);
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);var path=files.PathFor("recomputed.cadoryx");await f.Session.SaveAsync(storage,path);
        using var reopened=await storage.LoadAsync(path,f.Assets);Assert.Equal(12,reopened.Snapshot.Features[first.Id].RepetitionPresentation!.CopiedResultSlots);
    }

    [Theory][InlineData(37)][InlineData(38)]
    public async Task OlderFeatureEnvelopeCannotSmugglePresentationPolicyOrEvidence(int version)
    {
        await using var f=new Fixture();using var files=new TestFiles();await f.Repeat(f.Layout());
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);var path=files.PathFor("smuggle.cadoryx");await f.Session.SaveAsync(storage,path);int count=f.Assets.Count;
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=version}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }

    [Fact] public async Task IndependentEditablePartRemapsCopyPathsAndRetainsPresentationWithoutRebinding()
    {
        await using var f=new Fixture();using var files=new TestFiles();var original=await f.Repeat(f.Layout());
        var root=new OccurrencePath(f.Session.Snapshot.Id,[]);var slot=ComponentSlotId.New();
        await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,original.PartId,slot,"Independent copy",RigidTransform3d.Translate(80,0,0)));
        var command=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await f.Session.ExecuteAsync(command);
        var clone=f.Session.Snapshot.Features.Values.Single(feature=>feature.PartId==command.ResultPartId&&feature.Recipe is RepetitionRecipe);
        var before=(RepetitionRecipe)original.Recipe;var after=(RepetitionRecipe)clone.Recipe;
        Assert.Empty(before.CopyIds.Intersect(after.CopyIds));Assert.Equal(original.Result,clone.Result);
        Assert.Equal(after.CopyIds.Order(),clone.TopologyHistory!.Entries.Select(e=>e.CopyId!.Value).Distinct().Order());
        Assert.Equal(original.RepetitionPresentation,clone.RepetitionPresentation);clone.TopologyHistory.ValidateFor(clone);
        await f.Kernel.ConfirmRepetitionPresentationAsync(clone,f.Assets);CheckNativePresentation(after.Source,clone,f.Assets);
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);var path=files.PathFor("independent.cadoryx");await f.Session.SaveAsync(storage,path);
        using var reopened=await storage.LoadAsync(path,f.Assets);Assert.Equal(after.CopyIds.ToArray(),((RepetitionRecipe)reopened.Snapshot.Features[clone.Id].Recipe).CopyIds.ToArray());
    }

    [Fact] public async Task LegacyFeaturePolicyMigratesThroughTheFixedIntermediateWithoutInventingEvidence()
    {
        await using var f=new Fixture();using var files=new TestFiles();var feature=await f.Repeat(f.Layout());
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);var path=files.PathFor("legacy.cadoryx");await f.Session.SaveAsync(storage,path);
        FormatEvolutionTests.RewriteSection(path,"features",bytes=>
        {
            var section=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
            return MessagePackSerializer.Serialize(section with{Features=section.Features.Select(v=>v with{RepetitionPresentation=null,
                Recipe=v.Recipe with{Repetition=v.Recipe.Repetition is {} layout?layout with{StylePolicy=null}:null}}).ToArray()});
        });
        // Emulate the actual legacy four-field point payload before declaring its document version.
        SketchLegacyPointFixture.Prepare(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=38}:
            s.Kind=="document"?s with{SchemaVersion=44}:s).ToImmutableArray()});
        using var reopened=await storage.LoadAsync(path,f.Assets);var restored=reopened.Snapshot.Features[feature.Id];
        Assert.Null(restored.RepetitionPresentation);Assert.Equal(RepetitionStylePolicy.VerifiedInheritance,Assert.IsType<RepetitionRecipe>(restored.Recipe).StylePolicy);
        Assert.Equal(((RepetitionRecipe)feature.Recipe).CopyIds.ToArray(),((RepetitionRecipe)restored.Recipe).CopyIds.ToArray());
    }

    private sealed class CancelOnSecondStage(IAssetStore inner,CancellationTokenSource cancellation):IAssetStore
    {
        internal int Stages {get;private set;}
        public IAssetLease Stage(ReadOnlySpan<byte> content){var lease=inner.Stage(content);if(++Stages==2)cancellation.Cancel();return lease;}
        public IAssetLease Acquire(AssetId id)=>inner.Acquire(id);
    }
    private static byte[] Bytes(IAssetStore assets,AssetId id){using var lease=assets.Acquire(id);return lease.Content.ToArray();}
    private static void CheckNativePresentation(GeometryAssetRef source,FeatureDefinition feature,IAssetStore assets)
    {
        var first=source.Source!;var last=feature.Result.Source!;
        using var original=OcctGeometryBridge.ReadContext(first.ContextAssetId,assets,first.Format);
        using var result=OcctGeometryBridge.ReadContext(last.ContextAssetId,assets,last.Format);
        var a=original.GetLabel(first.DefinitionEntry);var b=result.GetLabel(last.DefinitionEntry);
        Assert.Equal(a.GetPresentationColors(),b.GetPresentationColors());Assert.Equal(a.Layers,b.Layers);Assert.Equal(a.Material,b.Material);
        Assert.False(b.ValidationProperties.HasAny);Assert.Null(b.Comment);
        using var sourceMap=new RepairDocumentSession(original,a);using var targetMap=new RepairDocumentSession(result,b);
        using var readSource=original.BeginTransaction("Read exact source slots");using var readTarget=result.BeginTransaction("Read exact result slots");
        foreach(var rows in feature.TopologyHistory!.Entries.Where(e=>e.SourceKind==HistoryShapeKind.Face&&e.ResultKind==HistoryShapeKind.Face&&e.ResultIndex.HasValue).GroupBy(e=>e.ResultIndex!.Value))
        {
            var actual=targetMap.GetOrCreateSubshapeLabel(targetMap.Source.Select(rows.Key));
            foreach(int index in rows.Select(e=>e.SourceIndex).Distinct())
            {
                var expected=sourceMap.GetOrCreateSubshapeLabel(sourceMap.Source.Select(index));
                Assert.Equal(expected.GetPresentationColors(),actual.GetPresentationColors());Assert.Equal(expected.Layers,actual.Layers);
                Assert.Equal(expected.Material,actual.Material);Assert.Equal(expected.VisualMaterial,actual.VisualMaterial);
            }
        }
    }
}
