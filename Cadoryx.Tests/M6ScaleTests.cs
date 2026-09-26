using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class M6ScaleTests
{
    [Fact] public async Task RepeatedPartInstancesKeepOneUniqueGeometryPayload()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Repeated"),assets,kernel,
            new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,5,6,RigidTransform3d.Identity),"Box"));
        var part=Assert.Single(session.Snapshot.Definitions.Values.OfType<PartDefinition>());
        var before=DocumentScaleReport.Measure(session.Snapshot,assets);
        var root=new OccurrencePath(session.Snapshot.Id,[]);
        for(int i=0;i<10;i++)await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,part.Id,
            ComponentSlotId.New(),"Copy",RigidTransform3d.Translate(i*10,0,0)));
        var after=DocumentScaleReport.Measure(session.Snapshot,assets);
        Assert.Equal(before.UniqueAssets,after.UniqueAssets);
        Assert.Equal(before.AssetBytes,after.AssetBytes);
        Assert.Equal(before.Occurrences+10,after.Occurrences);
        Assert.Equal(before.Features,after.Features);
        Assert.Equal(after.Occurrences,CadScene.FromDocument(session.Snapshot).Items.Length);
    }

    [Fact] public async Task TreeDefersNestedRowsAndRetainsExpansionAfterEdit()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Tree"),assets,kernel,
            new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,2,2,RigidTransform3d.Identity),"B"));
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        var tree=new ModelTreeToolboxViewModel(new Icons());
        try
        {
            tree.Bind(vm);
            var part=Assert.Single(tree.Items);
            Assert.Single(part.Children); // expansion placeholder, not materialized body/history rows
            Assert.False(part.IsExpanded);
            part.IsExpanded=true;
            Assert.Contains(part.Children,n=>n.Target is not null);
            await session.ExecuteAsync(AssemblyOccurrenceCommands.RenameOccurrence(part.Path!,"Renamed"));
            var refreshed=Assert.Single(tree.Items);
            Assert.True(refreshed.IsExpanded);
            Assert.Contains(refreshed.Children,n=>n.Target is not null);
        }
        finally{tree.Bind(null);vm.Detach();}
    }

    [Theory]
    [InlineData(10,2,10)]
    [InlineData(10,1,20)]
    [InlineData(10,0.1,200)]
    public void AdaptiveDisplayGridUsesOneTwoFiveMultiples(double model,double pixelsPerMm,double expected)
    {
        Assert.Equal(expected,AdaptiveGridScale.Choose(model,pixelsPerMm));
        Assert.Equal(10,model); // display selection never changes document snap interval
    }

    private sealed class Icons:IToolboxIconProvider
    {public object ModelTree=>"";public object Properties=>"";public object Modeling=>"";public object Messages=>"";}
}
