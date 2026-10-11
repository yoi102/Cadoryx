using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.IO;
using MessagePack;
using OcctSharp;
using Cadoryx.Editor;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed partial class DrawingHoleTableTests
{
    private static async Task<DrawingHoleCoordinateBinding> AddCoordinateDatums(Fixture f,DefinitionId? owner=null)
    {
        var part=owner??f.Session.Snapshot.Bodies.Values.Single().PartId;
        var plane=new CadDatumPlane(Guid.NewGuid(),part,"Hole plane",DatumPlaneKind.Offset,DatumPlaneSource.Independent(RigidTransform3d.Translate(0,0,15)));
        var point=new CadDatumPoint(Guid.NewGuid(),part,"Hole origin",DatumPointKind.Independent,new(1,2,15));
        var axis=new CadDatumAxis(Guid.NewGuid(),part,"Hole X",DatumAxisKind.Independent,Direction:new(1,0,0));
        var book=f.Session.Snapshot.Datums;
        await f.Session.ExecuteAsync(new SetDatumsCommand(book with{Planes=book.Planes.Add(plane.Id,plane),Points=book.Points.Add(point.Id,point),Axes=book.Axes.Add(axis.Id,axis)}));
        return new(plane.Id,point.Id,axis.Id);
    }
    [Fact] public async Task NamedPlanePointAndAxisDriveCoordinatesInSameTransactionAndUndo()
    {
        await using var f=new Fixture();await f.Init();var binding=await AddCoordinateDatums(f);
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(await f.Evidence()) with{CoordinateBinding=binding}));
        Assert.Equal(new Point2d(4,4),f.Current.Rows[0].CoordinatesMm);Assert.Equal(3,f.Current.CoordinateRevisions.Length);
        var before=f.Session.Snapshot;var rowId=f.Current.Rows[0].Id;var book=before.Datums;var point=book.Points[binding.OriginPoint!.Value];
        await f.Session.ExecuteAsync(new SetDatumsCommand(book with{Points=book.Points.SetItem(point.Id,point with{Value=new(2,3,15)})}));
        Assert.Equal(new Point2d(3,3),f.Current.Rows[0].CoordinatesMm);Assert.Equal(rowId,f.Current.Rows[0].Id);
        Assert.Null(f.Current.StaleReason);Assert.NotEqual(before.DrawingSheets[f.Sheet].HoleTables[0].CoordinateRevisions[1],f.Current.CoordinateRevisions[1]);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();
        book=f.Session.Snapshot.Datums;var axis=book.Axes[binding.XAxis!.Value];
        await f.Session.ExecuteAsync(new SetDatumsCommand(book with{Axes=book.Axes.SetItem(axis.Id,axis with{Direction=new(0,1,0)})}));
        Assert.Equal(3,f.Current.Rows[0].CoordinatesMm.X,6);Assert.Equal(-3,f.Current.Rows[0].CoordinatesMm.Y,6);
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Current with{CoordinateBinding=binding with{ReverseX=true}},true));
        Assert.Equal(-3,f.Current.Rows[0].CoordinatesMm.X,6);Assert.Equal(3,f.Current.Rows[0].CoordinatesMm.Y,6);
    }
    [Theory][InlineData("plane")][InlineData("point")][InlineData("axis")][InlineData("off-plane")][InlineData("normal-axis")]
    public async Task MissingOrIncompatibleDatumRetainsOldCoordinatesAndCanDetachExplicitly(string kind)
    {
        await using var f=new Fixture();await f.Init();var binding=await AddCoordinateDatums(f);
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(await f.Evidence()) with{CoordinateBinding=binding}));
        var before=f.Current;var book=f.Session.Snapshot.Datums;
        book=kind switch
        {
            "plane"=>book with{Planes=book.Planes.Remove(binding.Plane)},
            "point"=>book with{Points=book.Points.Remove(binding.OriginPoint!.Value)},
            "axis"=>book with{Axes=book.Axes.Remove(binding.XAxis!.Value)},
            "off-plane"=>book with{Points=book.Points.SetItem(binding.OriginPoint!.Value,book.Points[binding.OriginPoint.Value] with{Value=new(1,2,16)})},
            _=>book with{Axes=book.Axes.SetItem(binding.XAxis!.Value,book.Axes[binding.XAxis.Value] with{Direction=Vector3d.UnitZ})}
        };
        await f.Session.ExecuteAsync(new SetDatumsCommand(book));
        Assert.NotNull(f.Current.StaleReason);Assert.NotNull(f.Current.Rows[0].StaleReason);
        Assert.Equal(before.LocalFrame,f.Current.LocalFrame);Assert.Equal(before.CoordinateRevisions,f.Current.CoordinateRevisions);
        Assert.Equal(before.Rows[0].CoordinatesMm,f.Current.Rows[0].CoordinatesMm);Assert.True(before.Rows[0].Evidence.SameProof(f.Current.Rows[0].Evidence));
        using var files=new TestFiles();var pdf=files.PathFor("protected.pdf");File.WriteAllText(pdf,"Old valid PDF");
        Assert.Throws<CadValidationException>(()=>TechnicalDrawingPdf.Write(f.Session.Snapshot,pdf));Assert.Equal("Old valid PDF",File.ReadAllText(pdf));
        var path=files.PathFor("stale.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(datumResolver:f.Kernel),path);
        using(var loaded=await new CadDocumentStorage(datumResolver:f.Kernel).LoadAsync(path,f.Assets))
        {Assert.NotNull(loaded.Snapshot.DrawingSheets[f.Sheet].HoleTables[0].StaleReason);Assert.Equal(binding,loaded.Snapshot.DrawingSheets[f.Sheet].HoleTables[0].CoordinateBinding);}
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Current with{CoordinateBinding=null,CoordinateRevisions=[]},true));
        Assert.Null(f.Current.CoordinateBinding);Assert.Null(f.Current.StaleReason);Assert.Null(f.Current.Rows[0].StaleReason);
        Assert.Equal(before.LocalFrame,f.Current.LocalFrame);Assert.Equal(before.Rows[0].CoordinatesMm,f.Current.Rows[0].CoordinatesMm);
        TechnicalDrawingPdf.Write(f.Session.Snapshot,pdf);
    }
    [Theory][InlineData("foreign")][InlineData("wrong-type")][InlineData("off-plane")][InlineData("normal-axis")][InlineData("missing")]
    public async Task InvalidTypedCoordinateBindingRejectsEntireEdit(string kind)
    {
        await using var f=new Fixture();await f.Init();DefinitionId? foreign=null;
        if(kind=="foreign")
        {foreign=DefinitionId.New();await f.Session.ExecuteAsync(ResourceCommands.AddPart(foreign.Value,"Other part"));}
        var binding=await AddCoordinateDatums(f,foreign);var book=f.Session.Snapshot.Datums;
        if(kind=="wrong-type")binding=binding with{Plane=binding.OriginPoint!.Value};
        if(kind=="missing")binding=binding with{XAxis=Guid.NewGuid()};
        if(kind=="off-plane")await f.Session.ExecuteAsync(new SetDatumsCommand(book with{Points=book.Points.SetItem(binding.OriginPoint!.Value,book.Points[binding.OriginPoint.Value] with{Value=new(0,0,16)})}));
        if(kind=="normal-axis")await f.Session.ExecuteAsync(new SetDatumsCommand(book with{Axes=book.Axes.SetItem(binding.XAxis!.Value,book.Axes[binding.XAxis.Value] with{Direction=Vector3d.UnitZ})}));
        var before=f.Session.Snapshot;var evidence=await f.Evidence(before.EnumerateOccurrences().First(o=>o.DefinitionId==before.Bodies.Values.Single().PartId).Path);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(evidence) with{CoordinateBinding=binding})));
        Assert.Same(before,f.Session.Snapshot);
    }
    [Fact] public async Task PlaneAloneUsesItsFullFrameAndFollowsOtherExplicitInstances()
    {
        await using var f=new Fixture();await f.Init();var b=await AddCoordinateDatums(f);var doc=f.Session.Snapshot;var part=f.Session.Snapshot.Bodies.Values.Single().PartId;
        var other=ComponentSlotId.New();await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new(doc.Id,[]),part,other,"Second",RigidTransform3d.Translate(30,0,0)));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
        var first=await f.Evidence(doc.EnumerateOccurrences().Single().Path);var second=await f.Evidence(new(doc.Id,[other]));
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(first) with{CoordinateBinding=new(b.Plane),Rows=[new(Guid.NewGuid(),1,first,new()),new(Guid.NewGuid(),2,second,new())]}));
        Assert.Equal(new Point2d(5,6),f.Current.Rows[0].CoordinatesMm);Assert.Equal(new Point2d(35,6),f.Current.Rows[1].CoordinatesMm);
        var book=f.Session.Snapshot.Datums;var plane=book.Planes[b.Plane];
        await f.Session.ExecuteAsync(new SetDatumsCommand(book with{Planes=book.Planes.SetItem(plane.Id,plane with{Primary=DatumPlaneSource.Independent(new(new(1,2,15),Quaterniond.FromEulerDegrees(0,0,90)))})}));
        Assert.Equal(4,f.Current.Rows[0].CoordinatesMm.X,6);Assert.Equal(-4,f.Current.Rows[0].CoordinatesMm.Y,6);
        Assert.Equal(-34,f.Current.Rows[1].CoordinatesMm.Y,6);Assert.Single(f.Current.CoordinateRevisions);
    }
    [Theory][InlineData("frame")][InlineData("revisions")][InlineData("missing-cache")][InlineData("wrong-type")][InlineData("hidden-cache")]
    public async Task CorruptNamedCoordinateCacheFailsBeforePublishingLoadedState(string kind)
    {
        await using var f=new Fixture();await f.Init();var binding=await AddCoordinateDatums(f);
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(await f.Evidence()) with{CoordinateBinding=binding}));
        using var files=new TestFiles();var path=files.PathFor("corrupt.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(datumResolver:f.Kernel),path);
        FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {
            var book=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);var sheet=book.Sheets[0];var table=sheet.HoleTables![0];
            table=kind switch
            {
                "frame"=>table with{Frame=table.Frame with{X=9,Y=9,Z=15}},
                "revisions"=>table with{CoordinateRevisions=[Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid()]},
                "missing-cache"=>table with{CoordinateRevisions=null},
                "wrong-type"=>table with{Coordinates=table.Coordinates! with{Plane=binding.OriginPoint!.Value}},
                _=>table with{Coordinates=null}
            };
            return MessagePackSerializer.Serialize(book with{Sheets=[sheet with{HoleTables=[table]}]});
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage(datumResolver:f.Kernel).LoadAsync(path,f.Assets));
    }
    [Fact] public async Task GenuineV8TableMigratesToExplicitNumericFrameAndRejectsNewNilKeys()
    {
        await using var f=new Fixture();await f.Init();await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(await f.Evidence())));
        using var files=new TestFiles();var path=files.PathFor("v8.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(datumResolver:f.Kernel),path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="drawings"?s with{SchemaVersion=8}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage(datumResolver:f.Kernel).LoadAsync(path,f.Assets));
        FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {
            var book=JsonNode.Parse(MessagePackSerializer.ConvertToJson(bytes))!.AsArray();var tables=book[0]![0]![15]!.AsArray();
            foreach(var node in tables){var table=node!.AsArray();table.RemoveAt(10);table.RemoveAt(9);}
            return MessagePackSerializer.ConvertFromJson(book.ToJsonString());
        });
        DrawingAnnotationTests.StripDrawingHistoryFieldsForLegacy(path);
        using var loaded=await new CadDocumentStorage(datumResolver:f.Kernel).LoadAsync(path,f.Assets);
        var actual=loaded.Snapshot.DrawingSheets[f.Sheet].HoleTables[0];Assert.Null(actual.CoordinateBinding);Assert.Empty(actual.CoordinateRevisions);
        Assert.Equal(f.Current.LocalFrame,actual.LocalFrame);Assert.Equal(f.Current.Rows[0].CoordinatesMm,actual.Rows[0].CoordinatesMm);
    }
    [Fact] public async Task ParameterDrivenDatumOriginUpdatesHoleRowsWithoutChangingTheirProof()
    {
        await using var f=new Fixture();await f.Init();var binding=await AddCoordinateDatums(f);
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(await f.Evidence()) with{CoordinateBinding=binding}));
        var before=f.Session.Snapshot;var evidence=f.Current.Rows[0].Evidence;var driver=new CadParameter(Guid.NewGuid(),"hole_origin_x",ParameterDimension.Length,"2 mm");
        await f.Session.ExecuteAsync(new SetParametersCommand(new([driver],[new(Guid.NewGuid(),driver.Id,ParameterTarget.Datum(binding.OriginPoint!.Value,"X"))])));
        Assert.Equal(new Point2d(3,4),f.Current.Rows[0].CoordinatesMm);Assert.True(evidence.SameProof(f.Current.Rows[0].Evidence));
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
    }
    [Fact] public async Task OriginalExactPlaneChangeFreezesCoordinatesUntilThatDatumIsExplicitlyReselected()
    {
        await using var f=new Fixture();await f.Init();var evidence=await f.Evidence();var part=evidence.Source.DefinitionId;
        await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,4,3,RigidTransform3d.Translate(20,0,20)),"Datum source",part));
        var feature=f.Session.Snapshot.Features.Values.Single(v=>v.Name=="Datum source");
        async Task<ExactDatumGeometry> TopEvidence()
        {
            var doc=f.Session.Snapshot;var current=doc.Features[feature.Id];using var shape=OcctGeometryBridge.ReadShape(current.Result,f.Assets);using var map=RepairSnapshot.Create(shape);
            var top=map.Topology.Single(t=>
            {
                if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);var s=face.GetFaceSurfaceSnapshot();
                return s.SurfaceType==SurfaceGeometryType.Plane&&face.EvaluateFace((s.FirstUParameter+s.LastUParameter)/2,(s.FirstVParameter+s.LastVParameter)/2).Normal.Z>.99;
            });
            return await f.Kernel.ResolveDatumGeometryAsync(doc,new(doc.Id,current.Id,current.Result.Revision,current.Result.AssetId,map.Fingerprint,top.Selection.Index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion),f.Assets);
        }
        var plane=new CadDatumPlane(Guid.NewGuid(),part,"Original plane",DatumPlaneKind.Offset,DatumPlaneSource.Face(await TopEvidence(),RigidTransform3d.Identity));
        await f.Session.ExecuteAsync(new SetDatumsCommand(f.Session.Snapshot.Datums with{Planes=f.Session.Snapshot.Datums.Planes.Add(plane.Id,plane)}));
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(evidence) with{CoordinateBinding=new(plane.Id)}));var old=f.Current;
        await f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,((BoxRecipe)feature.Recipe) with{Z=4}));
        Assert.True(f.Session.Snapshot.Datums.Planes[plane.Id].IsStale);Assert.NotNull(f.Current.StaleReason);Assert.Equal(old.LocalFrame,f.Current.LocalFrame);
        Assert.True(evidence.SameProof(f.Current.Rows[0].Evidence));
        var book=f.Session.Snapshot.Datums;plane=book.Planes[plane.Id];
        await f.Session.ExecuteAsync(new SetDatumsCommand(book with{Planes=book.Planes.SetItem(plane.Id,plane with{Primary=DatumPlaneSource.Face(await TopEvidence(),RigidTransform3d.Identity)})}));
        Assert.Null(f.Current.StaleReason);Assert.Null(f.Current.Rows[0].StaleReason);Assert.Equal(24,f.Current.LocalFrame.Translation.Z,6);
        Assert.Equal(old.Rows[0].Id,f.Current.Rows[0].Id);Assert.True(evidence.SameProof(f.Current.Rows[0].Evidence));
    }
    [Fact] public async Task LoadedAssociatedHoleFramePreservesTypedAuthorsThroughHistoryInsertion()
    {
        await using var f=new Fixture();await f.Init();var binding=await AddCoordinateDatums(f);
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(await f.Evidence()) with{CoordinateBinding=binding}));
        using var files=new TestFiles();var path=files.PathFor("associated-history.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(datumResolver:f.Kernel),path);
        using var loaded=await new CadDocumentStorage(datumResolver:f.Kernel).LoadAsync(path,f.Assets);
        await using var reopened=new CadDocumentSession(loaded.Snapshot,f.Assets,f.Kernel,new InlineSessionDispatcher());
        var before=reopened.Snapshot;var part=f.Current.CoordinateDefinition;await reopened.RollbackFeaturesAsync(part,1);
        await reopened.ExecuteAsync(new InsertFeatureAtCursorCommand(new AddBodyCommand(new CylinderRecipe(1,2,RigidTransform3d.Translate(20,0,0)),"Branch",part),reopened.FeatureCursor!));
        var current=reopened.Snapshot.DrawingSheets[f.Sheet].HoleTables[0];Assert.Equal(binding,current.CoordinateBinding);
        Assert.Equal(f.Current.LocalFrame,current.LocalFrame);Assert.Equal(f.Current.CoordinateRevisions.ToArray(),current.CoordinateRevisions.ToArray());
        Assert.Equal(f.Current.Rows[0].Id,current.Rows[0].Id);Assert.Null(current.StaleReason);Assert.Null(current.Rows[0].StaleReason);
        await reopened.UndoAsync();Assert.Same(before,reopened.Snapshot);
    }
    [Fact] public async Task ReselectedViewCannotSilentlyKeepAnExcludedCoordinateInstance()
    {
        await using var f=new Fixture();await f.Init();var evidence=await f.Evidence();var binding=await AddCoordinateDatums(f);var doc=f.Session.Snapshot;var other=ComponentSlotId.New();
        await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new(doc.Id,[]),evidence.Source.DefinitionId,other,"Second",RigidTransform3d.Translate(30,0,0)));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(evidence) with{CoordinateBinding=binding}));var old=f.Current;
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,new(doc.Id,[other])));
        Assert.NotNull(f.Current.StaleReason);Assert.Equal(old.CoordinatePath,f.Current.CoordinatePath);Assert.Equal(old.LocalFrame,f.Current.LocalFrame);
        Assert.Equal(old.Rows[0].CoordinatesMm,f.Current.Rows[0].CoordinatesMm);
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));Assert.Null(f.Current.StaleReason);Assert.Null(f.Current.Rows[0].StaleReason);
    }
    [Theory][InlineData(.5,"1:2")][InlineData(1,"1:1")][InlineData(2,"2:1")][InlineData(2.5,"2.5:1")][InlineData(.0001,"1:10000")][InlineData(10000,"10000:1")]
    public void DrawingScaleUsesPaperToModelRatioIndependentOfCulture(double scale,string expected)
    {
        var old=CultureInfo.CurrentCulture;
        try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");Assert.Equal(expected,DrawingScale.Format(scale));}
        finally{CultureInfo.CurrentCulture=old;}
        var sheet=DrawingSheetTemplate.Create("Scale");sheet=sheet with{Views=[new(Guid.NewGuid(),"Top",DrawingViewKind.Top,null,new(50,50),scale,[],[new(DrawingLineKind.Sharp,false,[new(0,0),new(1,1)])])]};
        var layout=DrawingPageLayout.Create(sheet,1,1);
        Assert.Contains(layout.Labels,l=>l.Text==expected);Assert.Contains(layout.Labels,l=>l.Text=="Top  "+expected);
    }
}
