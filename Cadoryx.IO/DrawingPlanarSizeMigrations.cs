using System.Buffers;
using MessagePack;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    private enum DrawingPlanarNode { Pass,Book,Sheet,Datum,Frame,Binding }
    private static ReadOnlyMemory<byte> VisitDrawingPlanarSizes(ReadOnlyMemory<byte> bytes,bool trim=false)
    {
        var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);var reader=new MessagePackReader(bytes);
        Visit(ref reader,ref writer,DrawingPlanarNode.Book,false);
        if(!reader.End)throw new InvalidDataException("Trailing planar size drawing payload.");
        writer.Flush();return buffer.WrittenMemory.ToArray();
        void Visit(ref MessagePackReader input,ref MessagePackWriter output,DrawingPlanarNode node,bool collection)
        {
            if(node==DrawingPlanarNode.Pass){output.WriteRaw(input.ReadRaw());return;}
            if(input.TryReadNil()){output.WriteNil();return;}
            int count=input.ReadArrayHeader();
            if(collection)
            {
                output.WriteArrayHeader(count);for(int i=0;i<count;i++)Visit(ref input,ref output,node,false);return;
            }
            int oldLength=node switch{DrawingPlanarNode.Datum=>8,DrawingPlanarNode.Frame=>14,DrawingPlanarNode.Binding=>3,_=>int.MaxValue};
            if(!trim&&count>oldLength)throw new InvalidDataException("Planar size fields in legacy drawings, including explicit nil, are not allowed.");
            int written=trim?Math.Min(count,oldLength):count;output.WriteArrayHeader(written);
            for(int key=0;key<count;key++)
            {
                if(key>=written){input.Skip();continue;}
                if((node,key) is (DrawingPlanarNode.Datum,6) or (DrawingPlanarNode.Frame,6) or (DrawingPlanarNode.Binding,1))
                {
                    int value=input.ReadInt32();if(value is <0 or >1)throw new InvalidDataException("New planar size enum value in legacy drawings.");
                    output.Write(value);continue;
                }
                var (child,list)=(node,key) switch
                {
                    (DrawingPlanarNode.Book,0)=>(DrawingPlanarNode.Sheet,true),
                    (DrawingPlanarNode.Sheet,11)=>(DrawingPlanarNode.Datum,true),
                    (DrawingPlanarNode.Sheet,12)=>(DrawingPlanarNode.Frame,true),
                    (DrawingPlanarNode.Frame,13)=>(DrawingPlanarNode.Binding,true),
                    _=>(DrawingPlanarNode.Pass,false)
                };
                Visit(ref input,ref output,child,list);
            }
        }
    }
    internal static ReadOnlyMemory<byte> UpgradeDrawingPlanarSizes(ReadOnlyMemory<byte> bytes)
    {
        VisitDrawingPlanarSizes(bytes);
        return Serialize(Read<PackDrawingBook>(bytes));
    }
}
