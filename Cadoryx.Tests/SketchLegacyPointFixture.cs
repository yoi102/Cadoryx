using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.IO.Compression;
using Cadoryx.Db;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

// Explicit fixture conversion, never part of the production migration. It
// removes only provenance; every existing MessagePack value is copied raw.
internal static class SketchLegacyPointFixture
{
    internal static byte[] WithoutOrigins(byte[] bytes)
    {
        var reader=new MessagePackReader(bytes);var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);
        Assert.Equal(1,reader.ReadArrayHeader());writer.WriteArrayHeader(1);
        int count=reader.ReadArrayHeader();writer.WriteArrayHeader(count);
        for(int i=0;i<count;i++)
        {
            int fields=reader.ReadArrayHeader();writer.WriteArrayHeader(fields);
            for(int k=0;k<4;k++)writer.WriteRaw(reader.ReadRaw());
            int points=reader.ReadArrayHeader();writer.WriteArrayHeader(points);
            for(int j=0;j<points;j++)
            {
                int pointFields=reader.ReadArrayHeader();Assert.Contains(pointFields,new[]{4,5});writer.WriteArrayHeader(4);
                for(int k=0;k<4;k++)writer.WriteRaw(reader.ReadRaw());
                if(pointFields==5)reader.Skip();
            }
            for(int k=5;k<fields;k++)writer.WriteRaw(reader.ReadRaw());
        }
        Assert.True(reader.End);writer.Flush();return buffer.WrittenSpan.ToArray();
    }
    internal static void Prepare(string path)
    {
        FormatEvolutionTests.RewriteSection(path,"sketches",WithoutOrigins);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{
            RequiredCapabilities=m.RequiredCapabilities.Where(c=>c!="cadoryx.sketch-point-origins.1").ToImmutableArray(),
            Sections=m.Sections.Select(s=>s.Kind=="document"&&s.SchemaVersion is 57 or 58 or 59 or 60?s with{SchemaVersion=56}:
                s.Kind=="sketches"&&s.SchemaVersion==22?s with{SchemaVersion=21}:s).ToImmutableArray()});
    }
    internal static string GeometryHash(string path)
    {
        var section=FormatEvolutionTests.Manifest(path).Sections.Single(s=>s.Kind=="sketches");
        using var zip=ZipFile.OpenRead(path);using var stream=zip.GetEntry(section.Path)!.Open();using var buffer=new MemoryStream();stream.CopyTo(buffer);
        return Convert.ToHexString(SHA256.HashData(WithoutOrigins(buffer.ToArray())));
    }
    internal static void SamePoints(CadSketch before,CadSketch after)
    {
        Assert.Equal(before.Points.Select(p=>(p.Id,p.Position,p.IsConstruction)),after.Points.Select(p=>(p.Id,p.Position,p.IsConstruction)));
        Assert.All(after.Points,p=>Assert.Equal(SketchPointOrigin.Unknown,p.Origin));
    }
}
