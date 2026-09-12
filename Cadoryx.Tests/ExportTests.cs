using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;
namespace Cadoryx.Tests;
public sealed class ExportTests
{
    [Theory]
    [InlineData("step")][InlineData("stp")][InlineData("iges")][InlineData("igs")]
    public async Task ExchangeRoundTripRespectsNestedPlacementsAndVisibility(string extension)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Assembly"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var snapshot=Instances(session.Snapshot);string path=files.PathFor("assembly."+extension);
            await kernel.ExportAsync(snapshot,assets,path,new CadExportOptions());
            Assert.True(new FileInfo(path).Length>100);
            using var loaded=await kernel.ImportAsync(path,assets);var bounds=WorldBounds(loaded.Snapshot);
            Assert.Equal(100,bounds.Min.X,3);Assert.Equal(110,bounds.Max.X,3);
            Assert.Equal(20,bounds.Min.Y,3);Assert.Equal(40,bounds.Max.Y,3);Assert.Equal(30,bounds.Max.Z,3);
            await kernel.ExportAsync(snapshot,assets,path,new CadExportOptions(VisibleOnly:false));
            using var all=await kernel.ImportAsync(path,assets);var allBounds=WorldBounds(all.Snapshot);
            Assert.Equal(510,allBounds.Max.X,3);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task StlUsesMillimeterCoordinatesAndChosenEncoding(bool binary)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("STL"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
        string path=files.PathFor("mesh.stl");
        var report=await kernel.ExportAsync(Instances(session.Snapshot),assets,path,new CadExportOptions(BinaryStl:binary));
        Assert.Contains(report.Diagnostics,d=>d.Code=="EXPORT.STL_SEMANTICS");
        var vertices=new List<Vector3d>();
        if(binary)
        {
            using var input=new BinaryReader(File.OpenRead(path));input.ReadBytes(80);uint count=input.ReadUInt32();Assert.Equal(12u,count);
            Assert.Equal(84L+count*50,new FileInfo(path).Length);
            for(uint i=0;i<count;i++){input.ReadBytes(12);for(int v=0;v<3;v++)vertices.Add(new(input.ReadSingle(),input.ReadSingle(),input.ReadSingle()));input.ReadUInt16();}
        }
        else
        {
            foreach(var line in File.ReadLines(path))
            {
                var values=line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
                if(values.Length==4&&values[0]=="vertex")vertices.Add(new(Parse(values[1]),Parse(values[2]),Parse(values[3])));
            }
            Assert.Equal(36,vertices.Count);
        }
        Assert.Equal(100,vertices.Min(v=>v.X),4);Assert.Equal(110,vertices.Max(v=>v.X),4);
        Assert.Equal(20,vertices.Min(v=>v.Y),4);Assert.Equal(40,vertices.Max(v=>v.Y),4);
    }
    [Theory][InlineData("step")][InlineData("iges")][InlineData("stl")]
    public async Task FailedOrCanceledExportPreservesTarget(string extension)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        string path=files.PathFor("existing."+extension);await File.WriteAllTextAsync(path,"existing");
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ExportAsync(DocumentSnapshot.Create("Empty"),assets,path));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>kernel.ExportAsync(DocumentSnapshot.Create("Canceled"),assets,path,cancel.Token));
        Assert.Equal("existing",await File.ReadAllTextAsync(path));
    }
    private static double Parse(string value)=>double.Parse(value,System.Globalization.CultureInfo.InvariantCulture);
    private static DocumentSnapshot Instances(DocumentSnapshot doc)
    {
        var part=doc.Definitions.Values.OfType<PartDefinition>().Single();var assemblyId=DefinitionId.New();
        var assembly=new AssemblyDefinition(assemblyId,"Nested",[new(ComponentSlotId.New(),part.Id,"Translated box",RigidTransform3d.Translate(0,20,0))]);
        var root=new AssemblyDefinition(doc.RootAssemblyId,doc.Name,[
            new(ComponentSlotId.New(),assemblyId,"Visible",RigidTransform3d.Translate(100,0,0)),
            new(ComponentSlotId.New(),assemblyId,"Hidden",RigidTransform3d.Translate(500,0,0),false)]);
        return doc with{Definitions=doc.Definitions.Add(assemblyId,assembly).SetItem(root.Id,root)};
    }
    private static Bounds3d WorldBounds(DocumentSnapshot doc)
    {
        var points=new List<Vector3d>();
        foreach(var o in doc.EnumerateOccurrences())if(doc.Definitions[o.DefinitionId] is PartDefinition p)
            foreach(var id in p.Bodies)
            {
                var b=doc.Bodies[id].Geometry.Bounds;
                foreach(double x in new[]{b.Min.X,b.Max.X})foreach(double y in new[]{b.Min.Y,b.Max.Y})foreach(double z in new[]{b.Min.Z,b.Max.Z})points.Add(o.WorldTransform.Apply(new(x,y,z)));
            }
        Assert.NotEmpty(points);
        return new(new(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)),new(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z)));
    }
}
