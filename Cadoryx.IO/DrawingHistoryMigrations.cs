using System.Buffers;
using MessagePack;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    private enum DrawingHistoryNode { Pass,Book,Sheet,Datum,Frame,Balloon,Table,Row,Hole,Size,Binding,Attachment }
    // Walk only declared attachment positions. An unrelated array of length 15 is never
    // interpreted as an attachment, and an explicit nil new key is still a new field.
    private static ReadOnlyMemory<byte> VisitDrawingHistoryOrigins(ReadOnlyMemory<byte> bytes,bool trim=false)
    {
        var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);var reader=new MessagePackReader(bytes);
        VisitDrawingHistoryNode(ref reader,ref writer,DrawingHistoryNode.Book,false,trim);
        if(!reader.End)throw new InvalidDataException("Trailing drawing history payload.");
        writer.Flush();return buffer.WrittenMemory.ToArray();
    }
    private static void VisitDrawingHistoryNode(ref MessagePackReader reader,ref MessagePackWriter writer,
        DrawingHistoryNode node,bool collection,bool trim)
    {
        if(node==DrawingHistoryNode.Pass){writer.WriteRaw(reader.ReadRaw());return;}
        if(reader.TryReadNil()){writer.WriteNil();return;}
        int count=reader.ReadArrayHeader();
        if(collection)
        {
            writer.WriteArrayHeader(count);
            for(int i=0;i<count;i++)VisitDrawingHistoryNode(ref reader,ref writer,node,false,trim);
            return;
        }
        if(node==DrawingHistoryNode.Attachment&&count>14&&!trim)
            throw new InvalidDataException("Attachment history fields in legacy drawings, including explicit nil, are not allowed.");
        int written=node==DrawingHistoryNode.Attachment&&trim?Math.Min(count,14):count;
        writer.WriteArrayHeader(written);
        for(int key=0;key<count;key++)
        {
            if(key>=written){reader.Skip();continue;}
            var (child,list)=(node,key) switch
            {
                (DrawingHistoryNode.Book,0)=>(DrawingHistoryNode.Sheet,true),
                (DrawingHistoryNode.Sheet,11)=>(DrawingHistoryNode.Datum,true),
                (DrawingHistoryNode.Sheet,12)=>(DrawingHistoryNode.Frame,true),
                (DrawingHistoryNode.Sheet,13)=>(DrawingHistoryNode.Balloon,true),
                (DrawingHistoryNode.Sheet,15)=>(DrawingHistoryNode.Table,true),
                (DrawingHistoryNode.Datum,3)=>(DrawingHistoryNode.Attachment,false),
                (DrawingHistoryNode.Datum,7)=>(DrawingHistoryNode.Size,false),
                (DrawingHistoryNode.Frame,2)=>(DrawingHistoryNode.Attachment,false),
                (DrawingHistoryNode.Frame,12)=>(DrawingHistoryNode.Size,false),
                (DrawingHistoryNode.Frame,13)=>(DrawingHistoryNode.Binding,true),
                (DrawingHistoryNode.Binding,2)=>(DrawingHistoryNode.Attachment,false),
                (DrawingHistoryNode.Balloon,2)=>(DrawingHistoryNode.Attachment,false),
                (DrawingHistoryNode.Table,7)=>(DrawingHistoryNode.Row,true),
                (DrawingHistoryNode.Row,2)=>(DrawingHistoryNode.Hole,false),
                (DrawingHistoryNode.Hole,0)=>(DrawingHistoryNode.Attachment,false),
                (DrawingHistoryNode.Size,0)=>(DrawingHistoryNode.Attachment,false),
                _=>(DrawingHistoryNode.Pass,false)
            };
            VisitDrawingHistoryNode(ref reader,ref writer,child,list,trim);
        }
    }
    internal static ReadOnlyMemory<byte> UpgradeDrawingHistoryOrigins(ReadOnlyMemory<byte> bytes)
    {
        VisitDrawingHistoryOrigins(VisitDrawingPlanarSizes(bytes));
        return VisitDrawingPlanarSizes(Serialize(Read<PackDrawingBook>(bytes)),trim:true);
    }
}
