using Cadoryx.Db;
using MessagePack;
using System.Buffers;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    // Intermediate migrations must write the declared legacy point layout. Only
    // the final provenance migration may introduce the fifth field.
    private static byte[] SerializeLegacySketches(PackSketches sketches)
    {
        if(sketches.Sketches.Any(s=>s.Points.Any(p=>p.Origin!=0)))
            throw new InvalidDataException("Point origin in legacy sketch schema.");
        var reader=new MessagePackReader(Serialize(sketches));
        var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);
        writer.WriteArrayHeader(reader.ReadArrayHeader());
        int count=reader.ReadArrayHeader();writer.WriteArrayHeader(count);
        for(int i=0;i<count;i++)
        {
            int fields=reader.ReadArrayHeader();writer.WriteArrayHeader(fields);
            for(int k=0;k<4;k++)writer.WriteRaw(reader.ReadRaw());
            int points=reader.ReadArrayHeader();writer.WriteArrayHeader(points);
            for(int j=0;j<points;j++)
            {
                if(reader.ReadArrayHeader()!=5)throw new InvalidDataException("Invalid current point layout.");
                writer.WriteArrayHeader(4);
                for(int k=0;k<4;k++)writer.WriteRaw(reader.ReadRaw());
                if(reader.ReadInt32()!=0)throw new InvalidDataException("Point origin in legacy sketch schema.");
            }
            for(int k=5;k<fields;k++)writer.WriteRaw(reader.ReadRaw());
        }
        if(!reader.End)throw new InvalidDataException("Trailing sketch data.");
        writer.Flush();return buffer.WrittenSpan.ToArray();
    }
    internal static byte[] UpgradeSketchPointOrigins(ReadOnlyMemory<byte> bytes)
    {
        RequireSketchPointFields(bytes,4);
        var old=Read<PackSketches>(bytes);
        if(old.Sketches.Any(s=>s.Points.Any(p=>p.Origin!=0)))
            throw new InvalidDataException("Point origin in legacy sketch schema.");
        // Missing provenance remains unknown; coordinates cannot prove an owner.
        return Serialize(old);
    }
    internal static void RequireSketchPointFields(ReadOnlyMemory<byte> bytes,int expected)
    {
        var reader=new MessagePackReader(bytes);
        if(reader.ReadArrayHeader()!=1)throw new InvalidDataException("Invalid sketch table fields.");
        int sketches=reader.ReadArrayHeader();
        if(sketches>10000)throw new InvalidDataException("Sketch registry exceeds its table budget.");
        for(int i=0;i<sketches;i++)
        {
            int fields=reader.ReadArrayHeader();
            if(fields<5||fields>16||expected==5&&fields!=16)throw new InvalidDataException("Invalid sketch author fields.");
            for(int k=0;k<4;k++)reader.Skip();
            int points=reader.ReadArrayHeader();
            if(points>10000)throw new InvalidDataException("Sketch points exceed their table budget.");
            for(int j=0;j<points;j++)
            {
                if(reader.ReadArrayHeader()!=expected)throw new InvalidDataException("Sketch point field count does not match its declared schema.");
                for(int k=0;k<4;k++)reader.Skip();
                if(expected==5&&!Enum.IsDefined((SketchPointOrigin)reader.ReadInt32()))
                    throw new InvalidDataException("Unknown sketch point origin.");
            }
            for(int k=5;k<fields;k++)reader.Skip();
        }
        if(!reader.End)throw new InvalidDataException("Trailing sketch point-origin data.");
    }
}
