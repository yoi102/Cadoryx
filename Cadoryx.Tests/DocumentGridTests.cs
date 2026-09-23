using System.Buffers;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Settings;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class DocumentGridTests
{
    [Theory]
    [InlineData(DocumentWorkPlaneKind.XY)]
    [InlineData(DocumentWorkPlaneKind.XZ)]
    [InlineData(DocumentWorkPlaneKind.YZ)]
    public void WorkPlaneCoordinatesAreOrthonormalAndOffsetAlongNormal(DocumentWorkPlaneKind kind)
    {
        var plane=new DocumentWorkPlaneSettings(kind,17);
        plane.Validate();
        var origin=plane.Origin;var u=plane.ToWorld(1,0)-origin;var v=plane.ToWorld(0,1)-origin;
        Assert.Equal(1,u.Length,10);Assert.Equal(1,v.Length,10);
        Assert.Equal(0,u.Dot(v),10);Assert.Equal(0,u.Dot(plane.Normal),10);Assert.Equal(0,v.Dot(plane.Normal),10);
        Assert.Equal(1,u.Cross(v).Dot(plane.Normal),10);
        var world=plane.ToWorld(4,-6);var local=plane.ToLocal(world);
        Assert.Equal(4,local.X,10);Assert.Equal(-6,local.Y,10);Assert.Equal(0,local.Z,10);
        Assert.Equal(17,origin.Dot(plane.Normal),10);
    }

    [Fact]
    public async Task WorkPlaneIsDocumentScopedUndoableAndMigratesFromVersionEleven()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var storage=new CadDocumentStorage();
        await using var first=new CadDocumentSession(DocumentSnapshot.Create("First"),assets,kernel,new InlineSessionDispatcher());
        await using var second=new CadDocumentSession(DocumentSnapshot.Create("Second"),assets,kernel,new InlineSessionDispatcher());
        var vm=new CadDocumentViewModel(first,kernel,new CadMessageLog());
        try
        {
            var selected=new DocumentWorkPlaneSettings(DocumentWorkPlaneKind.XZ,25);
            vm.StartTool("Box");vm.ConstructionPointer(new(1,2,0),10,10,2,true);
            await vm.SetWorkPlaneAsync(selected);
            Assert.False(vm.IsViewportConstructing);
            Assert.Equal(selected,vm.WorkPlaneSettings);
            Assert.Equal(new(),second.Snapshot.Settings.WorkPlane);
            await first.UndoAsync();Assert.Equal(new(),vm.WorkPlaneSettings);
            await first.RedoAsync();Assert.Equal(selected,vm.WorkPlaneSettings);
            await Assert.ThrowsAsync<CadValidationException>(()=>vm.SetWorkPlaneAsync(new(DocumentWorkPlaneKind.XZ,double.NaN)));
            await Assert.ThrowsAsync<CadValidationException>(()=>vm.SetWorkPlaneAsync(new((DocumentWorkPlaneKind)99,0)));
            var path=files.PathFor("work-plane.cadoryx");await first.SaveAsync(storage,path);
            Assert.Equal(selected,(await storage.ReadSettingsAsync(path)).WorkPlane);
            using var loaded=await storage.LoadAsync(path,assets);
            Assert.Equal(selected,loaded.Snapshot.Settings.WorkPlane);
            var pack=new PackDocument(first.Snapshot.Id.Value,first.Snapshot.StateId.Value,"Old",first.Snapshot.RootAssemblyId.Value,
                0,3,1e-7,1e-9,false,5,true,0xFF335577,0xFFD1E4F1,false,2,12,2,25);
            var reader=new MessagePackReader(MessagePackSerializer.Serialize(pack));
            Assert.Equal(18,reader.ReadArrayHeader());
            var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);writer.WriteArrayHeader(16);
            for(int i=0;i<16;i++)writer.WriteRaw(reader.ReadRaw());writer.Flush();
            var migrated=new CadSectionMigrationRegistry().Migrate(
                [new(new SectionFormat("document",11,"messagepack"),buffer.WrittenMemory)],
                new Dictionary<string,SectionFormat>{{"document",CadSectionMigrationRegistry.CurrentFormats["document"]}});
            var restored=MessagePackSerializer.Deserialize<PackDocument>(migrated["document"].Bytes);
            Assert.Equal(0,restored.WorkPlaneKind);Assert.Equal(0,restored.WorkPlaneOffsetMm);
            Assert.False(restored.GridVisible);Assert.Equal(5,restored.GridSpacingMm);
        }
        finally{vm.Detach();}
    }

    [Fact]
    public async Task GridIsPerDocumentUndoableAndStored()
    {
        using var files=new TestFiles();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        await using var first=new CadDocumentSession(DocumentSnapshot.Create("First"),assets,kernel,new InlineSessionDispatcher());
        await using var second=new CadDocumentSession(DocumentSnapshot.Create("Second"),assets,kernel,new InlineSessionDispatcher());
        var vm=new CadDocumentViewModel(first,kernel,new CadMessageLog());
        try
        {
            int notifications=0;vm.ViewportSettingsChanged+=(_,_)=>notifications++;
            vm.StartTool("Box");Assert.True(vm.IsViewportConstructing);
            await vm.SetGridAsync(visible:false,spacingMm:2.5,snap:true);
            Assert.Equal(new(false,2.5,true),first.Snapshot.Settings.Grid);
            Assert.Equal(new(),second.Snapshot.Settings.Grid);
            Assert.False(vm.GridVisible);Assert.Equal(2.5,vm.GridSpacingMm);Assert.True(vm.SnapToGrid);
            Assert.True(first.IsDirty);Assert.Equal(1,notifications);
            Assert.True(vm.IsViewportConstructing);
            vm.CancelViewportConstruction();
            await first.UndoAsync();Assert.Equal(new(),first.Snapshot.Settings.Grid);Assert.Equal(2,notifications);
            await first.RedoAsync();Assert.Equal(new(false,2.5,true),first.Snapshot.Settings.Grid);Assert.Equal(3,notifications);
            await Assert.ThrowsAsync<CadValidationException>(()=>vm.SetGridAsync(spacingMm:0));
            await Assert.ThrowsAsync<CadValidationException>(()=>vm.SetGridAsync(spacingMm:double.NaN));
            string path=files.PathFor("grid.cadoryx");await first.SaveAsync(storage,path);
            Assert.Equal(first.Snapshot.Settings.Grid,(await storage.ReadSettingsAsync(path)).Grid);
            using var loaded=await storage.LoadAsync(path,assets);
            Assert.Equal(new(false,2.5,true),loaded.Snapshot.Settings.Grid);
        }
        finally{vm.Detach();}
    }

    [Fact]
    public void VersionSevenDocumentMigratesWithDefaultGrid()
    {
        var oldFormat=new SectionFormat("document",7,"messagepack");
        var doc=DocumentSnapshot.Create("Legacy grid");
        var packed=new PackDocument(doc.Id.Value,doc.StateId.Value,doc.Name,doc.RootAssemblyId.Value,0,3,1e-7,1e-9,false,2,true,0,0,false,0,0);
        var bytes=MessagePackSerializer.Serialize(packed);
        var reader=new MessagePackReader(bytes);
        Assert.Equal(18,reader.ReadArrayHeader());
        var buffer=new ArrayBufferWriter<byte>();
        var writer=new MessagePackWriter(buffer);
        writer.WriteArrayHeader(8);
        for(int index=0;index<8;index++)writer.WriteRaw(reader.ReadRaw());
        writer.Flush();
        var migrated=new CadSectionMigrationRegistry().Migrate(
            [new(oldFormat,buffer.WrittenMemory)],
            new Dictionary<string,SectionFormat>{{"document",CadSectionMigrationRegistry.CurrentFormats["document"]}});
        var restored=MessagePackSerializer.Deserialize<PackDocument>(migrated["document"].Bytes);
        Assert.True(restored.GridVisible);Assert.Equal(10,restored.GridSpacingMm);Assert.False(restored.GridSnap);
        Assert.Equal(DocumentSettings.DefaultBackgroundTopArgb,restored.BackgroundTopArgb);
        Assert.Equal(DocumentSettings.DefaultBackgroundBottomArgb,restored.BackgroundBottomArgb);
        Assert.True(restored.OriginVisible);Assert.Equal(20,restored.OriginSizeMm);
        Assert.Equal(doc.Id.Value,restored.Id);
    }

    [Fact]
    public void VersionEightPreservesGridAndAddsSkyBackground()
    {
        var doc=DocumentSnapshot.Create("Grid only");
        var packed=new PackDocument(doc.Id.Value,doc.StateId.Value,doc.Name,doc.RootAssemblyId.Value,0,3,1e-7,1e-9,false,4,true,0,0,false,0,0);
        var reader=new MessagePackReader(MessagePackSerializer.Serialize(packed));
        Assert.Equal(18,reader.ReadArrayHeader());
        var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);
        writer.WriteArrayHeader(11);
        for(int index=0;index<11;index++)writer.WriteRaw(reader.ReadRaw());
        writer.Flush();
        var migrated=new CadSectionMigrationRegistry().Migrate(
            [new(new SectionFormat("document",8,"messagepack"),buffer.WrittenMemory)],
            new Dictionary<string,SectionFormat>{{"document",CadSectionMigrationRegistry.CurrentFormats["document"]}});
        var restored=MessagePackSerializer.Deserialize<PackDocument>(migrated["document"].Bytes);
        Assert.False(restored.GridVisible);Assert.Equal(4,restored.GridSpacingMm);Assert.True(restored.GridSnap);
        Assert.Equal(DocumentSettings.DefaultBackgroundTopArgb,restored.BackgroundTopArgb);
        Assert.Equal(DocumentSettings.DefaultBackgroundBottomArgb,restored.BackgroundBottomArgb);
    }

    [Fact]
    public void VersionNinePreservesSolidBackground()
    {
        var doc=DocumentSnapshot.Create("Solid legacy");
        var packed=new PackDocument(doc.Id.Value,doc.StateId.Value,doc.Name,doc.RootAssemblyId.Value,0,3,1e-7,1e-9,true,10,false,0xFF335577,0,false,0,0);
        var reader=new MessagePackReader(MessagePackSerializer.Serialize(packed));
        Assert.Equal(18,reader.ReadArrayHeader());
        var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);
        writer.WriteArrayHeader(12);
        for(int index=0;index<12;index++)writer.WriteRaw(reader.ReadRaw());
        writer.Flush();
        var migrated=new CadSectionMigrationRegistry().Migrate(
            [new(new SectionFormat("document",9,"messagepack"),buffer.WrittenMemory)],
            new Dictionary<string,SectionFormat>{{"document",CadSectionMigrationRegistry.CurrentFormats["document"]}});
        var restored=MessagePackSerializer.Deserialize<PackDocument>(migrated["document"].Bytes);
        Assert.Equal(0xFF335577u,restored.BackgroundTopArgb);
        Assert.Equal(0xFF335577u,restored.BackgroundBottomArgb);
    }

    [Fact]
    public void VersionTenAddsDefaultOriginWithoutChangingExistingSettings()
    {
        var doc=DocumentSnapshot.Create("Legacy origin");
        var packed=new PackDocument(doc.Id.Value,doc.StateId.Value,doc.Name,doc.RootAssemblyId.Value,
            0,3,1e-7,1e-9,false,7,true,0xFF335577,0xFFE1EEFA,false,0,0);
        var reader=new MessagePackReader(MessagePackSerializer.Serialize(packed));
        Assert.Equal(18,reader.ReadArrayHeader());
        var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);
        writer.WriteArrayHeader(13);
        for(int index=0;index<13;index++)writer.WriteRaw(reader.ReadRaw());
        writer.Flush();
        var migrated=new CadSectionMigrationRegistry().Migrate(
            [new(new SectionFormat("document",10,"messagepack"),buffer.WrittenMemory)],
            new Dictionary<string,SectionFormat>{{"document",CadSectionMigrationRegistry.CurrentFormats["document"]}});
        var restored=MessagePackSerializer.Deserialize<PackDocument>(migrated["document"].Bytes);
        Assert.True(restored.OriginVisible);
        Assert.Equal((int)DocumentOriginStyle.ColorAxes,restored.OriginStyle);
        Assert.Equal(20,restored.OriginSizeMm);
        Assert.False(restored.GridVisible);Assert.Equal(7,restored.GridSpacingMm);Assert.True(restored.GridSnap);
        Assert.Equal(0xFF335577u,restored.BackgroundTopArgb);
        Assert.Equal(0xFFE1EEFAu,restored.BackgroundBottomArgb);
    }

    [Fact]
    public async Task OriginStylesArePerDocumentUndoableAndStored()
    {
        using var files=new TestFiles();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        await using var first=new CadDocumentSession(DocumentSnapshot.Create("Origin"),assets,kernel,new InlineSessionDispatcher());
        await using var second=new CadDocumentSession(DocumentSnapshot.Create("Other"),assets,kernel,new InlineSessionDispatcher());
        var vm=new CadDocumentViewModel(first,kernel,new CadMessageLog());
        try
        {
            var editor=new DocumentSettingsViewModel(vm);
            Assert.True(editor.OriginVisible);
            Assert.Equal(DocumentOriginStyle.ColorAxes,editor.SelectedOriginStyle!.Style);
            foreach(var style in Enum.GetValues<DocumentOriginStyle>())
            {
                editor.OriginVisible=style!=DocumentOriginStyle.OriginMarker;
                editor.SelectedOriginStyle=editor.OriginStyleOptions.Single(o=>o.Style==style);
                editor.OriginSizeMm=12.5+(int)style;
                Assert.True(await editor.TryApplyAsync(),editor.ValidationError);
                Assert.Equal(new(editor.OriginVisible,style,editor.OriginSizeMm),first.Snapshot.Settings.Origin);
                Assert.Equal(new(),second.Snapshot.Settings.Origin);
            }
            var expected=first.Snapshot.Settings.Origin;
            await first.UndoAsync();Assert.NotEqual(expected,first.Snapshot.Settings.Origin);
            await first.RedoAsync();Assert.Equal(expected,first.Snapshot.Settings.Origin);
            editor.OriginSizeMm=double.NaN;
            Assert.False(await editor.TryApplyAsync());Assert.Equal(expected,first.Snapshot.Settings.Origin);
            editor.OriginSizeMm=0;
            Assert.False(await editor.TryApplyAsync());Assert.Equal(expected,first.Snapshot.Settings.Origin);
            string path=files.PathFor("origin.cadoryx");await first.SaveAsync(storage,path);
            Assert.Equal(expected,(await storage.ReadSettingsAsync(path)).Origin);
            using var loaded=await storage.LoadAsync(path,assets);
            Assert.Equal(expected,loaded.Snapshot.Settings.Origin);
        }
        finally{vm.Detach();}
    }

    [Fact]
    public async Task SettingsWindowEditsBackgroundGridAndUnitsAsOneUndoableChange()
    {
        using var files=new TestFiles();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Display"),assets,kernel,new InlineSessionDispatcher());
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var editor=new DocumentSettingsViewModel(vm);
            Assert.Equal(DocumentSettings.DefaultBackgroundTopArgb,editor.BackgroundTopArgb);
            Assert.Equal(DocumentSettings.DefaultBackgroundBottomArgb,editor.BackgroundBottomArgb);
            editor.BackgroundTopArgb=0xFF314A65;
            editor.BackgroundBottomArgb=0xFFD1E4F1;
            editor.GridSpacingMm=12.5;
            editor.SelectedUnit=editor.UnitOptions.Single(option=>option.Unit==LengthUnit.Inch);
            editor.DecimalPlaces=4;
            Assert.True(await editor.TryApplyAsync(),editor.ValidationError);
            Assert.Equal(0xFF314A65u,vm.BackgroundTopArgb);
            Assert.Equal(0xFFD1E4F1u,vm.BackgroundBottomArgb);
            Assert.Equal(12.5,vm.GridSpacingMm);
            Assert.Equal(LengthUnit.Inch,session.Snapshot.Settings.DisplayUnit);
            Assert.Equal(4,session.Snapshot.Settings.DecimalPlaces);
            await session.UndoAsync();
            Assert.Equal(DocumentSettings.DefaultBackgroundTopArgb,vm.BackgroundTopArgb);
            Assert.Equal(DocumentSettings.DefaultBackgroundBottomArgb,vm.BackgroundBottomArgb);
            Assert.Equal(10,vm.GridSpacingMm);
            await session.RedoAsync();
            Assert.Equal(0xFF314A65u,vm.BackgroundTopArgb);
            Assert.Equal(0xFFD1E4F1u,vm.BackgroundBottomArgb);
            editor.BackgroundBottomArgb=0x80D1E4F1;
            Assert.False(await editor.TryApplyAsync());Assert.NotNull(editor.ValidationError);
            Assert.Equal(0xFFD1E4F1u,vm.BackgroundBottomArgb);
            string path=files.PathFor("display.cadoryx");await session.SaveAsync(storage,path);
            var read=await storage.ReadSettingsAsync(path);
            Assert.Equal(0xFF314A65u,read.BackgroundTopArgb);
            Assert.Equal(0xFFD1E4F1u,read.BackgroundBottomArgb);
            using var loaded=await storage.LoadAsync(path,assets);
            Assert.Equal(session.Snapshot.Settings,loaded.Snapshot.Settings);
        }
        finally {vm.Detach();}
    }
}
