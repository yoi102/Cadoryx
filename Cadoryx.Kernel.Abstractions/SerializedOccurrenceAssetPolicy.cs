using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Bounded envelope and integrity only. Complete path/native identity proof belongs to the actual SDK decoder.</summary>
public static class SerializedOccurrenceAssetPolicy
{
    public const int MaximumBytes=64*1024*1024,MaximumOccurrences=512,MaximumDepth=64;
    public static void VerifyGeometry(GeometryAssetRef geometry,IAssetStore assets)
    {
        geometry.Validate();if(geometry.SerializedOccurrences is not {} occurrences)return;
        using var boundary=assets.Acquire(geometry.AssetId);using var table=assets.Acquire(occurrences.TableAssetId);
        VerifyBinding(boundary.Content.Span,table.Content.Span);
    }
    public static void VerifyBinding(ReadOnlySpan<byte> brep,ReadOnlySpan<byte> table)
    {
        if(brep.Length is <1 or >MaximumBytes||table.Length is <80 or >MaximumBytes||!table.StartsWith("OCROCC26"u8))
            throw new InvalidDataException("Invalid bounded native occurrence envelope.");
        AssetFormatPolicy.RequireBRep(AssetFormatPolicy.Inspect(brep));
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData(brep);hash.AppendData(table[..^32]);
        if(!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),table[^32..]))throw new InvalidDataException("BRep/occurrence table integrity mismatch.");
        var reader=new TableReader(table[..^32]);
        int version=reader.Integer(),sourceCount=reader.Integer(),rows=reader.Integer(),pathValues=reader.Integer();
        int rootKind=reader.Integer(),orientation=reader.Integer(),shapes=reader.Integer(),locations=reader.Integer(),bytes=reader.Integer();
        if(version!=1||sourceCount is <1 or >1000000||rows is <1 or >MaximumOccurrences||pathValues>MaximumOccurrences*MaximumDepth||
            rootKind>7||orientation>3||shapes is <1 or >1000000||locations>1000000||bytes!=brep.Length)
            throw new InvalidDataException("Invalid native occurrence dimensions.");
        reader.Token(shapes,locations);int actualPaths=0;var paths=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<rows;i++)
        {
            int source=reader.Integer(),kind=reader.Integer(),direction=reader.Integer(),depth=reader.Integer(),same=reader.Integer(),exact=reader.Integer();
            if(source>=sourceCount||kind>7||direction>3||depth>MaximumDepth||same<1||same>rows||exact<1||exact>same)
                throw new InvalidDataException("Invalid native occurrence row.");
            int[] path=new int[depth];for(int j=0;j<depth;j++)path[j]=reader.Integer();actualPaths+=depth;
            if(!paths.Add(string.Join('/',path)))throw new InvalidDataException("Repeated occurrence path.");
            reader.Token(shapes,locations);
        }
        if(!reader.AtEnd||actualPaths!=pathValues)throw new InvalidDataException("Trailing or incomplete native occurrence envelope.");
    }
    private ref struct TableReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> data=bytes;private int offset=8;
        public bool AtEnd=>offset==data.Length;
        public int Integer()
        {
            if(data.Length-offset<4)throw new InvalidDataException("Truncated native occurrence table.");
            int value=BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset,4));offset+=4;
            if(value<0)throw new InvalidDataException("Negative native occurrence integer.");return value;
        }
        public void Token(int shapes,int locations)
        {
            int length=Integer();if(length is <4 or >64||length>data.Length-offset)throw new InvalidDataException("Invalid native occurrence token length.");
            var token=data.Slice(offset,length);offset+=length;
            if(!"+-iIeE"u8.Contains(token[0])||token.ContainsAnyExceptInRange((byte)32,(byte)126))throw new InvalidDataException("Invalid native occurrence token bytes.");
            var parts=Encoding.ASCII.GetString(token[1..]).Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length!=2||!int.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out int shape)||shape<1||shape>shapes||
                !int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out int location)||location>locations)
                throw new InvalidDataException("Native occurrence token outside its declared set.");
        }
    }
}
