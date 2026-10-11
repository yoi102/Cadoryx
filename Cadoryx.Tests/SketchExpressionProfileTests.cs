using System.Collections.Immutable;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class SketchExpressionProfileTests
{
    private static SketchBoundaryCurve Edge(SketchCurveExpressionGeometry geometry)=>new(geometry.At(0),geometry.At(1)){Expression=geometry};
    private static SketchCurveExpressionGeometry Circle(double radius,Point2d center,double distance,bool reverse=false)
    {
        var g=new SketchEllipseGeometry(center,new(radius,0),new(0,radius),0,Math.Tau);
        var expression=SketchCurveExpressionGeometry.FromBoundary(new(g.At(0),g.At(1)){Ellipse=g});
        return(reverse?expression.Reverse():expression).Offset(distance);
    }
    private static SketchProfile Profile(SketchCurveExpressionGeometry geometry)=>new([]){BoundaryCurves=[Edge(geometry)]};
    public static IEnumerable<object[]> Circles()=>from reverse in new[]{false,true} from distance in new[]{-.125,.125} from tilted in new[]{false,true} select new object[]{reverse,distance,tilted};
    [Theory][MemberData(nameof(Circles))]
    public async Task ClosedOffsetsProduceRealFacesAndPlacedSolidsWithAnalyticAreaAndVolume(bool reverse,double distance,bool tilted)
    {
        var expression=Circle(2,new(8,1),distance,reverse);var profile=Profile(expression);profile.Validate();Assert.True(profile.RequiresExactRegionInspection);
        double radius=2+(reverse?-distance:distance),area=Math.PI*radius*radius;var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        using(var face=OcctSketchProfileGeometry.CreateFace(profile,false))
        {Assert.True(face.IsValid);Assert.InRange(Math.Abs(face.InspectProperties(InspectionPropertyKind.Area).Mass-area),0,1e-8);}
        var placement=tilted?new RigidTransform3d(new(100,200,30),Quaterniond.FromEulerDegrees(75,15,0)):RigidTransform3d.Identity;
        using(var result=await kernel.EvaluateAsync(new ExtrudeRecipe(profile,3,placement),assets))
        using(var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets))
        {
            Assert.Equal(BodyKind.Solid,result.Geometry.Kind);Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-3*area),0,1e-7);
            Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);
            var edges=shape.GetSubShapes(ShapeKind.Edge);
            try{Assert.Contains(edges,e=>e.GetEdgeCurveSnapshot().CurveType==CurveGeometryType.OffsetCurve);}
            finally{foreach(var edge in edges)edge.Dispose();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FreeformOffsetPlusExplicitChordRetainsExactAreaAndProfileValues(bool reverse)
    {
        var basis=SketchCurveExpressionGeometry.FromBoundary(new(new(4,0),new(6,1)){BezierControl=new(5,0)});
        var expression=(reverse?basis.Reverse():basis).Offset(.05).Trim(.1,.9);var edge=Edge(expression);
        var profile=new SketchProfile([]){BoundaryCurves=[edge,new(edge.End,edge.Start)]};var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        // Independent composite Gauss integration of the author's Green integral, plus the exact chord.
        double[] nodes=[-.8611363115940526,-.3399810435848563,.3399810435848563,.8611363115940526];
        double[] weights=[.3478548451374539,.6521451548625461,.6521451548625461,.3478548451374539];double integral=0;
        for(int i=0;i<128;i++)for(int j=0;j<4;j++)
        {double t=(i+.5+.5*nodes[j])/128;var v=expression.Evaluate(t);integral+=weights[j]*(v.Point.X*v.First.Y-v.Point.Y*v.First.X)/256;}
        double area=Math.Abs((integral+edge.End.X*edge.Start.Y-edge.Start.X*edge.End.Y)/2);
        var copied=ExactSketchCurve.FromBoundary(edge);Assert.True(expression.SameValues(copied.Expression!));
        Assert.InRange(Math.Abs(kernel.InspectRegion(profile.BoundaryCurves.Select(ExactSketchCurve.FromBoundary).ToImmutableArray()).Area-area),0,1e-8);
        using(var result=await kernel.EvaluateAsync(new ExtrudeRecipe(profile,2,RigidTransform3d.Identity),assets))Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-2*area),0,1e-7);
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExpressionMeridiansRevolveAsExactToriAndRejectInteriorAxisCrossing(bool crossing)
    {
        var expression=Circle(2,new(crossing?1:8,1),.1);var recipe=new RevolveRecipe(Profile(expression),Math.Tau,RigidTransform3d.Identity);
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        if(crossing)await Assert.ThrowsAnyAsync<ArgumentException>(()=>kernel.EvaluateAsync(recipe,assets));
        else using(var result=await kernel.EvaluateAsync(recipe,assets))Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-2*Math.PI*Math.PI*8*2.1*2.1),0,1e-7);
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ExactExpressionHolesAndIslandsKeepTheirMaterialMeaning()
    {
        var root=Profile(Circle(4,new(0,0),.1)) with{MixedHoles=[[Edge(Circle(2,new(0,0),-.1))]],
            Islands=[new([],null,[Edge(Circle(1,new(0,0),.2))])]};var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var result=await kernel.EvaluateAsync(new ExtrudeRecipe(root,3,RigidTransform3d.Identity),assets))
        {Assert.Equal(BodyKind.Compound,result.Geometry.Kind);Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-3*Math.PI*(4.1*4.1-1.9*1.9+1.2*1.2)),0,1e-7);}
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SweepExpressionSectionsAndPathsKeepAnalyticVolume(bool curved)
    {
        SketchCurveExpressionGeometry path;
        if(curved)path=SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(5,5),new(5-5/Math.Sqrt(2),5/Math.Sqrt(2)))).Offset(.1);
        else path=SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(0,10))).Offset(.1);
        var start=path.At(0);var recipe=new SweepRecipe(new(Profile(Circle(.5,new(0,0),.05)),RigidTransform3d.Translate(start.X,0,start.Y)),
            new([Edge(path)],new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0))));
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();double length=curved?4.9*Math.PI/2:10;
        using(var result=await kernel.EvaluateAsync(recipe,assets))Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-Math.PI*.55*.55*length),0,1e-7);
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task LoftExpressionSectionsRemainExactAndAuthorOrdered()
    {
        var first=Profile(Circle(1,new(0,0),.1));var last=Profile(Circle(2,new(0,0),.1));var recipe=new LoftRecipe(
            [RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,5)],[new(Guid.NewGuid(),null,[first,last])],Ruled:true);
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var result=await kernel.EvaluateAsync(recipe,assets))Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-5*Math.PI*(1.1*1.1+1.1*2.1+2.1*2.1)/3),0,1e-7);
        Assert.Equal(0,assets.Count);
    }
    [Fact] public void FiniteQueriesAndNativeRegionRelationsUseExpressionGeometry()
    {
        var kernel=new OcctGeometryKernel();var expression=Circle(2,new(0,0),.1);var curve=ExactSketchCurve.FromBoundary(Edge(expression));
        var p=kernel.Evaluate(curve,.25);Assert.InRange(double.Hypot(p.Point.X,p.Point.Y-2.1),0,1e-9);
        var projected=kernel.Project(curve,new(3,0));Assert.InRange(Math.Abs(projected[0].Distance-.9),0,1e-9);
        var line=new ExactSketchCurve(new(-3,0),new(3,0));var hits=kernel.Intersect(curve,line);
        Assert.Equal(3,hits.Length);Assert.Equal(2,hits.Count(h=>Math.Abs(h.Point.X-2.1)<=1e-9));Assert.Single(hits.Where(h=>Math.Abs(h.Point.X+2.1)<=1e-9));
        Assert.Contains(hits,h=>h.FirstParameter<=1e-9);Assert.Contains(hits,h=>h.FirstParameter>=1-1e-9);
        Assert.Equal(ExactRegionRelation.Contains,kernel.CompareRegions([Edge(expression)],[Edge(Circle(1,new(0,0),.1))]));
        Assert.Equal(ExactRegionRelation.Disjoint,kernel.CompareRegions([Edge(expression)],[Edge(Circle(1,new(10,0),.1))]));
    }
    [Theory][InlineData("extrude")][InlineData("revolve")][InlineData("sweep")][InlineData("loft")]
    public async Task RecipeExpressionsPersistWithAtomicUndoAndLegacySchemaRejection(string kind)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var profile=Profile(Circle(1,new(kind=="revolve"?8:0,0),.1));GeometryRecipe recipe=kind switch
        {
            "revolve"=>new RevolveRecipe(profile,Math.Tau,RigidTransform3d.Identity),
            "sweep"=>new SweepRecipe(new(profile,RigidTransform3d.Identity),new([new(new(0,0),new(0,5))],new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0)))),
            "loft"=>new LoftRecipe([RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,5)],[new(Guid.NewGuid(),null,[profile,profile])],Ruled:true),
            _=>new ExtrudeRecipe(profile,3,RigidTransform3d.Identity)
        };
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Expression consumers"),assets,kernel,new InlineSessionDispatcher()))
        {
            var before=session.Snapshot;await session.ExecuteAsync(new AddBodyCommand(recipe,kind));var after=session.Snapshot;var feature=after.Features.Values.Single();
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var storage=new CadDocumentStorage();var file=files.PathFor("expression.cadoryx");await session.SaveAsync(storage,file);
            var manifest=FormatEvolutionTests.Manifest(file);Assert.Equal("0.7.8",manifest.ApplicationVersion);Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
            Assert.Equal(43,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);Assert.Contains("cadoryx.exact-expression-profiles.1",manifest.RequiredCapabilities);
            using(var loaded=await storage.LoadAsync(file,assets))
            {
                Assert.Equal(JsonSerializer.Serialize(feature.Recipe,CadJson.Options),JsonSerializer.Serialize(loaded.Snapshot.Features[feature.Id].Recipe,CadJson.Options));
                Assert.Equal(feature.Result,loaded.Snapshot.Features[feature.Id].Result);loaded.Snapshot.Validate();
            }
            FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=42}:s).ToImmutableArray()});
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(file,assets));Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public void FoldedMixedAndEmbeddedExpressionLeavesAreRejectedWithoutAssets()
    {
        var expression=Circle(2,new(4,0),-3).Offset(3);var profile=Profile(expression);var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        Assert.Throws<CadValidationException>(()=>OcctSketchProfileGeometry.CreateFace(profile,false));Assert.Equal(0,assets.Count);
        Assert.Throws<CadValidationException>(()=>SketchCurveExpressionGeometry.FromBoundary(Edge(Circle(2,new(0,0),.1))));
        var mixed=Edge(Circle(2,new(0,0),.1)) with{Ellipse=new(new(0,0),new(2,0),new(0,2),0,Math.Tau)};
        Assert.Throws<CadValidationException>(()=>new SketchProfile([]){BoundaryCurves=[mixed]}.Validate());
    }
    [Theory][InlineData("fields")][InlineData("depth")]
    public async Task EmbeddedFeatureExpressionKeepsStrictNodeFieldsAndBudgets(string mutation)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Strict feature"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(Profile(Circle(1,new(0,0),.1)),2,RigidTransform3d.Identity),"Offset"));
            var storage=new CadDocumentStorage();var file=files.PathFor("strict.cadoryx");await session.SaveAsync(storage,file);int count=assets.Count;
            FormatEvolutionTests.RewriteSection(file,"features",bytes=>
            {
                var value=JsonNode.Parse(MessagePackSerializer.ConvertToJson(bytes))!;var expression=value[0]![0]![3]![8]![0]![8]!;
                var nodes=expression[1]!.AsArray();if(mutation=="fields")nodes[1]!.AsArray().Add(0);
                else{var node=nodes[1]!.DeepClone();while(nodes.Count<65)nodes.Add(node.DeepClone());}
                var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);WriteJson(ref writer,value);writer.Flush();return buffer.WrittenSpan.ToArray();
            });
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(file,assets));Assert.Equal(count,assets.Count);session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
    private static void WriteJson(ref MessagePackWriter writer,JsonNode? value)
    {
        if(value is null){writer.WriteNil();return;}
        if(value is JsonArray array){writer.WriteArrayHeader(array.Count);foreach(var item in array)WriteJson(ref writer,item);return;}
        using var document=JsonDocument.Parse(value.ToJsonString());var element=document.RootElement;
        switch(element.ValueKind)
        {
            case JsonValueKind.String:writer.Write(element.GetString());break;
            case JsonValueKind.Number:if(element.TryGetInt64(out long integer))writer.Write(integer);else writer.Write(element.GetDouble());break;
            case JsonValueKind.True:writer.Write(true);break;case JsonValueKind.False:writer.Write(false);break;
            default:throw new InvalidDataException("Fixture requires explicit MessagePack scalar values.");
        }
    }
    [Fact] public async Task StepRoundtripReportsActualCurveConversionAndKeepsSolidGeometry()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Offset exchange"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(Profile(Circle(1,new(0,0),.1)),3,RigidTransform3d.Identity),"Offset circle"));
            var feature=session.Snapshot.Features.Values.Single();var file=files.PathFor("offset.step");int count=assets.Count;
            var report=await kernel.ExportAsync(session.Snapshot,assets,file,new CadExportOptions());
            Assert.Contains(report.Diagnostics,d=>d.Code=="EXPORT.EXPRESSION_EXACT_CANONICALIZATION");
            Assert.Contains(report.Diagnostics,d=>d.Code=="EXPORT.EXPRESSION_AUTHOR_VALUES");Assert.Equal(count,assets.Count);
            using var imported=ShapeExchange.ReadStep(file);var solids=imported.GetSubShapes(ShapeKind.Solid);var edges=imported.GetSubShapes(ShapeKind.Edge);
            try
            {
                Assert.Single(solids);Assert.InRange(Math.Abs(Math.Abs(solids[0].InspectProperties(InspectionPropertyKind.Volume).Mass)-feature.Result.VolumeMm3),0,1e-7);
                Assert.True(imported.GetValidationReport(true,true).IsValid);Assert.Contains(edges,e=>e.GetEdgeCurveSnapshot().CurveType==CurveGeometryType.Circle);
                // The exchange copy uses an algebraically identical circle. The author tree and native asset retain the offset.
                Assert.DoesNotContain(edges,e=>e.GetEdgeCurveSnapshot().CurveType==CurveGeometryType.OffsetCurve);
                Assert.True(Circle(1,new(0,0),.1).SameValues(((ExtrudeRecipe)feature.Recipe).Profile.BoundaryCurves[0].Expression!));
            }
            finally{foreach(var solid in solids)solid.Dispose();foreach(var edge in edges)edge.Dispose();}
        }
        Assert.Equal(0,assets.Count);
    }
    public static IEnumerable<object[]> CanonicalCircles()=>from reflect in new[]{false,true} from reverse in new[]{false,true}
        from swap in new[]{false,true} select new object[]{reflect,reverse,swap};
    [Theory][MemberData(nameof(CanonicalCircles))]
    public async Task GuidedCanonicalCirclesKeepReflectionReversalAndEveryOffsetStage(bool reflect,bool reverse,bool swap)
    {
        var axes=swap?new SketchEllipseGeometry(new(0,0),new(0,1),new(reflect?1:-1,0),.25,Math.Tau):
            new SketchEllipseGeometry(new(0,0),new(1,0),new(0,reflect?-1:1),.25,Math.Tau);
        var basis=SketchCurveExpressionGeometry.FromBoundary(new(axes.At(0),axes.At(1)){Ellipse=axes});
        var expression=(reverse?basis.Reverse():basis).Offset(.1).Reverse().Offset(-.02);
        double radius=1+(reflect?-1:1)*(reverse?-1:1)*.12;
        var recipe=new SweepRecipe(new(Profile(expression),RigidTransform3d.Identity),
            new([new(new(0,0),new(0,2))],new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0))));
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var result=await kernel.EvaluateAsync(recipe,assets))using(var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets))
        {
            Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-2*Math.PI*radius*radius),0,1e-7);
            var edges=shape.GetSubShapes(ShapeKind.Edge);
            try
            {
                Assert.Contains(edges,e=>e.GetEdgeCurveSnapshot().CurveType==CurveGeometryType.Circle);
                Assert.DoesNotContain(edges,e=>e.GetEdgeCurveSnapshot().CurveType==CurveGeometryType.OffsetCurve);
            }
            finally{foreach(var edge in edges)edge.Dispose();}
        }
        Assert.True(expression.SameValues(recipe.Section.Profile.BoundaryCurves[0].Expression!));Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("step")][InlineData("iges")]
    public async Task UnsupportedFreeformExchangePreservesDestinationAuthorTreeAndAssets(string extension)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var expression=SketchCurveExpressionGeometry.FromBoundary(new(new(4,0),new(6,1)){BezierControl=new(5,0)}).Offset(.05).Trim(.1,.9);
        var edge=Edge(expression);var profile=new SketchProfile([]){BoundaryCurves=[edge,new(edge.End,edge.Start)]};
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("No implicit fit"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(profile,2,RigidTransform3d.Identity),"Exact freeform"));
            var before=session.Snapshot;int count=assets.Count;var file=files.PathFor("existing."+extension);await File.WriteAllTextAsync(file,"existing destination");
            var error=await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ExportAsync(before,assets,file,new CadExportOptions()));
            Assert.Equal("EXPORT.EXPRESSION_UNREPRESENTABLE",error.Data["CadDiagnostic.Code"]);
            Assert.Equal("existing destination",await File.ReadAllTextAsync(file));Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
            using var original=OcctGeometryBridge.ReadShape(before.Bodies.Values.Single().Geometry,assets);var edges=original.GetSubShapes(ShapeKind.Edge);
            try{Assert.Contains(edges,e=>e.GetEdgeCurveSnapshot().CurveType==CurveGeometryType.OffsetCurve);}
            finally{foreach(var nativeEdge in edges)nativeEdge.Dispose();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("step")][InlineData("iges")]
    public async Task CanonicalExchangePreservesNestedSharedDefinitionsPlacementsAndOverallColors(string extension)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var expression=Circle(1,new(0,0),.1,true).Reverse().Offset(-.02);double radius=.88;
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Canonical assembly"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(Profile(expression),3,RigidTransform3d.Translate(2,0,0)),"Expression cylinder"));
            var doc=session.Snapshot;var body=doc.Bodies.Values.Single();var part=doc.Definitions.Values.OfType<PartDefinition>().Single();
            var nestedId=DefinitionId.New();var nested=new AssemblyDefinition(nestedId,"Shared assembly",
                [new(ComponentSlotId.New(),part.Id,"Blue cylinder",RigidTransform3d.Translate(0,5,0))]);
            var root=new AssemblyDefinition(doc.RootAssemblyId,doc.Name,
                [new(ComponentSlotId.New(),nestedId,"First assembly",RigidTransform3d.Translate(10,0,0)),
                 new(ComponentSlotId.New(),nestedId,"Second assembly",RigidTransform3d.Translate(20,0,0))]);
            var snapshot=doc with{Bodies=doc.Bodies.SetItem(body.Id,body with{Appearance=new(0xFF2255AA)}),
                Definitions=doc.Definitions.Add(nestedId,nested).SetItem(root.Id,root)};snapshot.Validate();int count=assets.Count;
            var file=files.PathFor("assembly."+extension);var report=await kernel.ExportAsync(snapshot,assets,file,new CadExportOptions());
            Assert.Single(report.Diagnostics.Where(d=>d.Code=="EXPORT.EXPRESSION_EXACT_CANONICALIZATION"));
            Assert.Equal(count,assets.Count);
            using var loaded=await kernel.ImportAsync(file,assets);var parts=loaded.Snapshot.EnumerateOccurrences()
                .Where(o=>loaded.Snapshot.Definitions[o.DefinitionId] is PartDefinition).ToArray();
            var centers=new List<Vector3d>();double area=0;
            foreach(var occurrence in parts)foreach(var id in ((PartDefinition)loaded.Snapshot.Definitions[occurrence.DefinitionId]).Bodies)
            {
                using var shape=OcctGeometryBridge.ReadShape(loaded.Snapshot.Bodies[id].Geometry,assets);var faces=shape.GetSubShapes(ShapeKind.Face);
                try{foreach(var face in faces){var properties=face.InspectProperties(InspectionPropertyKind.Area);area+=Math.Abs(properties.Mass);
                    centers.Add(occurrence.WorldTransform.Apply(new(properties.CenterOfMass.X,properties.CenterOfMass.Y,properties.CenterOfMass.Z)));}}
                finally{foreach(var face in faces)face.Dispose();}
            }
            // Compare actual surface geometry; an OCCT bounding box deliberately includes edge tolerance.
            Assert.Equal(6,centers.Count);Assert.Equal(3,centers.Count(p=>Math.Abs(p.X-12)<=1e-7&&Math.Abs(p.Y-5)<=1e-7));
            Assert.Equal(3,centers.Count(p=>Math.Abs(p.X-22)<=1e-7&&Math.Abs(p.Y-5)<=1e-7));
            Assert.InRange(Math.Abs(area-2*(2*Math.PI*radius*3+2*Math.PI*radius*radius)),0,1e-7);
            Assert.All(loaded.Snapshot.Bodies.Values,b=>Assert.Equal(0xFF2255AA,b.Appearance.Argb));
            if(extension=="step")
            {
                Assert.Equal(2,parts.Length);
                Assert.Single(loaded.Snapshot.Definitions.Values.OfType<PartDefinition>());
                Assert.All(loaded.Snapshot.Bodies.Values,b=>Assert.InRange(Math.Abs(b.Geometry.VolumeMm3-3*Math.PI*radius*radius),0,1e-7));
            }
            else Assert.Contains(report.Diagnostics,d=>d.Code=="EXPORT.IGES_SURFACES");
            Assert.True(expression.SameValues(((ExtrudeRecipe)snapshot.Features.Values.Single().Recipe).Profile.BoundaryCurves[0].Expression!));
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("hole")][InlineData("island")][InlineData("island-hole")][InlineData("nested-island")]
    [InlineData("sweep-path")][InlineData("sweep-section")][InlineData("loft-channel")][InlineData("pattern-template")]
    public async Task LegacyFeature42RejectsExpressionsHiddenInEveryRecipeContainer(string container)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Legacy containers"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(Profile(Circle(1,new(0,0),.1)),3,RigidTransform3d.Identity),"Offset"));
            var storage=new CadDocumentStorage();var file=files.PathFor("legacy.cadoryx");await session.SaveAsync(storage,file);int count=assets.Count;
            FormatEvolutionTests.RewriteSection(file,"features",bytes=>
            {
                var packed=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);var feature=packed.Features[0];var recipe=feature.Recipe;
                var expression=recipe.MixedCurves![0];var plain=recipe with{MixedCurves=[]};
                var profile=new PackRecipe("extrude",recipe.Numbers,recipe.Placement,[],recipe.Profile,recipe.Operation,MixedCurves:[expression]);
                var empty=profile with{MixedCurves=[]};var island=new PackIsland([],MixedCurves:[expression]);
                var changed=container switch
                {
                    "hole"=>plain with{MixedHoles=[[expression]]},"island"=>plain with{Islands=[island]},
                    "island-hole"=>plain with{Islands=[island with{MixedCurves=[],MixedHoles=[[expression]]}]},
                    "nested-island"=>plain with{Islands=[island with{MixedCurves=[],Islands=[island]}]},
                    "sweep-path"=>plain with{Guided=new(new(empty,recipe.Placement!,[expression],recipe.Placement!,0,0))},
                    "sweep-section"=>plain with{Guided=new(new(profile,recipe.Placement!,[],recipe.Placement!,0,0))},
                    "loft-channel"=>plain with{Guided=new(Loft:new([recipe.Placement!,recipe.Placement!],
                        [new(Guid.NewGuid(),null,[empty,profile])],0,true))},
                    _=>plain with{ExtrusionPattern=new(Guid.NewGuid(),new(0,2,2,[Guid.NewGuid()],0,true,false,null,null),profile)}
                };
                return MessagePackSerializer.Serialize(packed with{Features=[feature with{Recipe=changed}]});
            });
            FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=42}:s).ToImmutableArray()});
            var error=await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(file,assets));
            Assert.Contains("Expression profiles cannot appear in a legacy feature schema",error.ToString());Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task LegacyFeature42WithOriginalEightFieldBoundariesMigratesWithoutChangingAssetsOrValues()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var profile=new SketchProfile([]){BoundaryCurves=[new(new(0,0),new(2,0),new(1,-1)),
            new(new(2,0),new(2,2)),new(new(2,2),new(0,2)),new(new(0,2),new(0,0))]};
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Legacy lines"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(profile,3,RigidTransform3d.Identity),"Square"));
            var storage=new CadDocumentStorage();var file=files.PathFor("legacy.cadoryx");await session.SaveAsync(storage,file);var before=session.Snapshot;
            FormatEvolutionTests.RewriteSection(file,"features",bytes=>
            {
                var value=JsonNode.Parse(MessagePackSerializer.ConvertToJson(bytes))!;
                foreach(var edge in value[0]![0]![3]![8]!.AsArray()){Assert.Null(edge![8]);edge.AsArray().RemoveAt(8);Assert.Equal(8,edge.AsArray().Count);}
                var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);WriteJson(ref writer,value);writer.Flush();return buffer.WrittenSpan.ToArray();
            });
            FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=42}:s).ToImmutableArray()});
            using var loaded=await storage.LoadAsync(file,assets);var old=before.Features.Values.Single();var restored=loaded.Snapshot.Features[old.Id];
            Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");Assert.Equal(old.Result,restored.Result);
            Assert.True(SketchMixedProfile.SameLoop(profile.BoundaryCurves,((ExtrudeRecipe)restored.Recipe).Profile.BoundaryCurves));
            Assert.Equal(before.ReferencedAssets().OrderBy(a=>a.Sha256),loaded.Snapshot.ReferencedAssets().OrderBy(a=>a.Sha256));
        }
        Assert.Equal(0,assets.Count);
    }
}
