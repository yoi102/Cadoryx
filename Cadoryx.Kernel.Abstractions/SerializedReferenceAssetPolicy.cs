using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Managed envelope/integrity guards only. Native graph membership must be queried by the decoder.</summary>
public static class SerializedReferenceAssetPolicy
{
    public const int MaximumBytes=64*1024*1024,MaximumReferences=512;
    public static void VerifyBinding(ReadOnlySpan<byte> brep,ReadOnlySpan<byte> table)
    {
        if(brep.Length is <1 or >MaximumBytes||table.Length is <72 or >MaximumBytes||!table.StartsWith("OCRREF25"u8))
            throw new InvalidDataException("Invalid bounded native reference envelope.");
        AssetFormatPolicy.RequireBRep(AssetFormatPolicy.Inspect(brep));
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData(brep);hash.AppendData(table[..^32]);
        if(!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),table[^32..]))
            throw new InvalidDataException("BRep/reference table integrity mismatch.");
        var reader=new TableReader(table[..^32]);
        int version=reader.Integer(),sourceCount=reader.Integer(),rootKind=reader.Integer(),orientation=reader.Integer();
        int rows=reader.Integer(),shapes=reader.Integer(),locations=reader.Integer(),bytes=reader.Integer();
        if(version!=1||sourceCount is <1 or >1000000||rootKind>7||orientation>3||rows>MaximumReferences||
            shapes is <1 or >1000000||locations>1000000||bytes!=brep.Length)
            throw new InvalidDataException("Invalid native reference dimensions.");
        reader.Token(shapes,locations);
        for(int i=0;i<rows;i++)
        {
            if(reader.Integer()>=sourceCount||reader.Integer()>7||reader.Integer()>3)
                throw new InvalidDataException("Invalid declared native reference row.");
            reader.Token(shapes,locations);
        }
        if(!reader.AtEnd)throw new InvalidDataException("Trailing native reference bytes.");
    }
    public static void VerifyGeometry(GeometryAssetRef geometry,IAssetStore assets)
    {
        geometry.Validate();if(geometry.SerializedReferences is not {} references)return;
        using var boundary=assets.Acquire(geometry.AssetId);using var table=assets.Acquire(references.TableAssetId);
        VerifyBinding(boundary.Content.Span,table.Content.Span);
    }
    private ref struct TableReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> data=bytes;
        private int offset=8;
        public bool AtEnd=>offset==data.Length;
        public int Integer()
        {
            if(data.Length-offset<4)throw new InvalidDataException("Truncated native reference table.");
            int value=BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset,4));offset+=4;
            if(value<0)throw new InvalidDataException("Negative native reference table integer.");return value;
        }
        public void Token(int shapes,int locations)
        {
            int length=Integer();if(length is <4 or >64||length>data.Length-offset)throw new InvalidDataException("Invalid native reference token length.");
            var token=data.Slice(offset,length);offset+=length;
            if(!"+-iIeE"u8.Contains(token[0])||token.ContainsAnyExceptInRange((byte)32,(byte)126))
                throw new InvalidDataException("Invalid native reference token bytes.");
            var parts=Encoding.ASCII.GetString(token[1..]).Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length!=2||!int.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out int shape)||shape<1||shape>shapes||
                !int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out int location)||location>locations)
                throw new InvalidDataException("Native reference token outside its declared set.");
        }
    }
}
