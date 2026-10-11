using System.Buffers;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    // The intermediate v6-v9 representation must retain the actual old numeric-key lengths.
    // Serializing the current DTO directly would insert nil new keys and then reject our own migration.
    private static ReadOnlyMemory<byte> SerializeLegacyDrawingBook(PackDrawingBook book)=>
        VisitLegacyDrawingAnnotations(Serialize(book),trim:true);
    private static ReadOnlyMemory<byte> VisitLegacyDrawingAnnotations(ReadOnlyMemory<byte> bytes,bool trim=false)
    {
        bytes=VisitDrawingHistoryOrigins(VisitDrawingPlanarSizes(bytes,trim),trim);
        var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);var reader=new MessagePackReader(bytes);
        int bookFields=reader.ReadArrayHeader();writer.WriteArrayHeader(bookFields);
        for(int bookKey=0;bookKey<bookFields;bookKey++)
        {
            if(bookKey!=0){writer.WriteRaw(reader.ReadRaw());continue;}
            int sheets=reader.ReadArrayHeader();writer.WriteArrayHeader(sheets);
            for(int s=0;s<sheets;s++)
            {
                int sheetFields=reader.ReadArrayHeader();writer.WriteArrayHeader(sheetFields);
                for(int key=0;key<sheetFields;key++)
                {
                    if(key is not (11 or 12)||reader.TryReadNil())
                    {
                        if(key is 11 or 12)writer.WriteNil();else writer.WriteRaw(reader.ReadRaw());
                        continue;
                    }
                    int count=reader.ReadArrayHeader();writer.WriteArrayHeader(count);
                    for(int i=0;i<count;i++)
                    {
                        if(reader.TryReadNil()){writer.WriteNil();continue;}
                        int fields=reader.ReadArrayHeader(),oldLength=key==11?6:11;
                        if(!trim&&fields>oldLength)throw new InvalidDataException("Typed datum fields in legacy drawings, including explicit nil, are not allowed.");
                        int written=trim?Math.Min(fields,oldLength):fields;writer.WriteArrayHeader(written);
                        for(int field=0;field<fields;field++)
                        {if(field<written)writer.WriteRaw(reader.ReadRaw());else reader.Skip();}
                    }
                }
            }
        }
        writer.Flush();return buffer.WrittenMemory.ToArray();
    }
    internal static ReadOnlyMemory<byte> UpgradeDrawingDatumSystems(ReadOnlyMemory<byte> bytes)
    {
        VisitLegacyDrawingAnnotations(bytes);
        var old=Read<PackDrawingBook>(bytes);
        if(old.Sheets is null||old.Sheets.Any(s=>s is null||s.Datums is null||s.Frames is null))
            throw new InvalidDataException("Missing legacy manufacturing annotations.");
        var next=old with{Sheets=old.Sheets.Select(s=>s with
        {
            Datums=s.Datums!.Select(d=>d is not null?d with{FeatureType=0,Size=null}:throw new InvalidDataException("Missing legacy datum.")).ToArray(),
            Frames=s.Frames!.Select(f=>
            {
                if(f is null||f.Datums is null)throw new InvalidDataException("Missing legacy control frame.");
                var bindings=f.Datums.Select(id=>s.Datums!.SingleOrDefault(d=>d.Id==id)??throw new InvalidDataException("Missing legacy frame datum.")).ToArray();
                // No native size proof existed in v9. Preserve authors, never manufacture proof from a cylinder classification.
                string? stale=f.Stale;
                if(f.Target==(int)DrawingToleranceTarget.Axis||
                    f.Characteristic is (int)DrawingGeometricCharacteristic.CircularRunout or (int)DrawingGeometricCharacteristic.TotalRunout||
                    bindings.Any(d=>SameOriginalFeature(d.Source,f.Source))||
                    bindings.Where((d,i)=>bindings.Take(i).Any(other=>SameOriginalFeature(d.Source,other.Source))).Any()||
                    f.Characteristic==(int)DrawingGeometricCharacteristic.Flatness&&f.Source.Geometry!=(int)DrawingAttachmentGeometry.PlaneFace||
                    f.Characteristic==(int)DrawingGeometricCharacteristic.Cylindricity&&f.Source.Geometry!=(int)DrawingAttachmentGeometry.CylinderFace||
                    f.Characteristic==(int)DrawingGeometricCharacteristic.Circularity&&f.Source.Geometry is not ((int)DrawingAttachmentGeometry.CylinderFace or (int)DrawingAttachmentGeometry.CircleEdge))
                    stale??="Legacy manufacturing frame requires explicit typed feature reselection.";
                return f with{DatumSystem=[],Size=null,DatumBindings=bindings.Select(d=>new PackDrawingDatumBinding(d.Id,0,d.Source)).ToArray(),Stale=stale};
            }).ToArray()
        }).ToArray()};
        return VisitDrawingHistoryOrigins(VisitDrawingPlanarSizes(Serialize(next),trim:true),trim:true);
    }
    private static bool SameOriginalFeature(PackDrawingAttachment a,PackDrawingAttachment b)=>
        a.Slots.SequenceEqual(b.Slots)&&a.Definition==b.Definition&&a.Body==b.Body&&a.Asset==b.Asset&&a.Revision==b.Revision&&a.Kind==b.Kind&&a.Index==b.Index;
}
