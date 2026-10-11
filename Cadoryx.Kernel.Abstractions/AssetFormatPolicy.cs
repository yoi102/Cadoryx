using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Managed checks before native decoding. Unknown provenance is never replaced with today's kernel.</summary>
public static class AssetFormatPolicy
{
    private static string CurrentWriterVersion=>System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(AssetFormatPolicy).Assembly)
        .Single(a=>a.Key=="OcctSharpPackageVersion").Value??throw new InvalidOperationException("Missing native writer package metadata.");
    public static AssetFormat CurrentBRep {get;}=new(AssetFormat.BRepMediaType,"brep-ascii",3,"OCCT","8.0.1","OcctSharp",CurrentWriterVersion);
    public static AssetFormat CurrentXde {get;}=new(AssetFormat.XdeMediaType,"binxcaf",12,"OCCT","8.0.1","OcctSharp",CurrentWriterVersion);
    public static AssetFormat CurrentReferenceTable {get;}=new(AssetFormat.ReferenceTableMediaType,"occt-reference-table",1,"OCCT","8.0.1","OcctSharp",CurrentWriterVersion);
    public static AssetFormat CurrentOccurrenceTable {get;}=new(AssetFormat.OccurrenceTableMediaType,"occt-occurrence-table",1,"OCCT","8.0.1","OcctSharp",CurrentWriterVersion);
    public static void RequireOccurrenceTable(AssetFormat format)
    {
        format.Validate();RequireKernel(format);
        if(format.MediaType!=AssetFormat.OccurrenceTableMediaType||format.Encoding!="occt-occurrence-table"||format.FormatVersion!=1)
            throw new NotSupportedException("Unsupported native occurrence table format.");
    }
    public static void RequireReferenceTable(AssetFormat format)
    {
        format.Validate();RequireKernel(format);
        if(format.MediaType!=AssetFormat.ReferenceTableMediaType||format.Encoding!="occt-reference-table"||format.FormatVersion!=1)
            throw new NotSupportedException("Unsupported native reference table format.");
    }
    public static void RequireRole(AssetFormat format,string role)
    {
        if(role==AssetFormat.BRepMediaType)RequireBRep(format);
        else if(role==AssetFormat.XdeMediaType)RequireXde(format);
        else if(role==AssetFormat.ReferenceTableMediaType)RequireReferenceTable(format);
        else if(role==AssetFormat.OccurrenceTableMediaType)RequireOccurrenceTable(format);
        else throw new NotSupportedException("Unsupported native asset role.");
    }
    public static void RequireBRep(AssetFormat format)
    {
        format.Validate();RequireKernel(format);
        if(format.MediaType!=AssetFormat.BRepMediaType||format.Encoding!="brep-ascii"||format.FormatVersion is <1 or >3)
            throw new NotSupportedException("Unsupported BRep media type, encoding or version.");
    }
    public static void RequireXde(AssetFormat format)
    {
        format.Validate();RequireKernel(format);
        if(format.MediaType!=AssetFormat.XdeMediaType||format.Encoding!="binxcaf"||format.FormatVersion!=12)
            throw new NotSupportedException("Unsupported XDE media type, encoding or version.");
    }
    private static void RequireKernel(AssetFormat format)
    {
        if(format.Kernel is not (null or "OCCT")||format.KernelVersion is not (null or "8.0.1"))
            throw new NotSupportedException("Asset kernel version has not been validated by this reader: "+format.Kernel+" "+format.KernelVersion);
    }
    public static AssetFormat Inspect(ReadOnlySpan<byte> bytes)
    {
        var prefix=bytes[..Math.Min(bytes.Length,4096)];
        if(prefix.StartsWith("OCRREF25"u8))return new(AssetFormat.ReferenceTableMediaType,"occt-reference-table",1,"OCCT");
        if(prefix.StartsWith("OCROCC26"u8))return new(AssetFormat.OccurrenceTableMediaType,"occt-occurrence-table",1,"OCCT");
        if(prefix.StartsWith("DBRep_DrawableShape"u8))
        {
            string text=Encoding.ASCII.GetString(prefix);const string marker="CASCADE Topology V";
            int start=text.IndexOf(marker,StringComparison.Ordinal);
            if(start>=0)
            {
                start+=marker.Length;int end=text.IndexOf(',',start);
                if(end>start&&int.TryParse(text.AsSpan(start,end-start),NumberStyles.None,CultureInfo.InvariantCulture,out int version))
                    return new(AssetFormat.BRepMediaType,"brep-ascii",version,"OCCT");
            }
            throw new InvalidDataException("Malformed BRep header.");
        }
        if(prefix.StartsWith("BINFILE"u8))
        {
            // OCCT FSD binary header: magic, endian marker, section offsets, object count, version string.
            if(prefix.Length<67||!prefix.Slice(7,4).SequenceEqual(new byte[]{1,2,3,4}))throw new NotSupportedException("Unsupported XDE byte order/header.");
            int length=BinaryPrimitives.ReadInt32LittleEndian(prefix.Slice(63,4));
            if(length is <1 or >8||67+length>prefix.Length||!int.TryParse(Encoding.ASCII.GetString(prefix.Slice(67,length)),NumberStyles.None,CultureInfo.InvariantCulture,out int version)||
                prefix.IndexOf("FILE_FORMAT: BinXCAF"u8)<0)throw new InvalidDataException("Malformed BinXCAF header.");
            return new(AssetFormat.XdeMediaType,"binxcaf",version,"OCCT");
        }
        return AssetFormat.Opaque;
    }
    public static void VerifyPayload(AssetFormat declared,ReadOnlySpan<byte> bytes)
    {
        declared.Validate();
        if(declared.MediaType==AssetFormat.OccurrenceTableMediaType)
        {
            RequireOccurrenceTable(declared);
            if(bytes.Length is <80 or >SerializedOccurrenceAssetPolicy.MaximumBytes||!bytes.StartsWith("OCROCC26"u8)||
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8,4))!=1)throw new InvalidDataException("Malformed native occurrence table header.");
            return;
        }
        if(declared.MediaType==AssetFormat.ReferenceTableMediaType)
        {
            RequireReferenceTable(declared);
            if(bytes.Length is <72 or >SerializedReferenceAssetPolicy.MaximumBytes||!bytes.StartsWith("OCRREF25"u8)||
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8,4))!=1)throw new InvalidDataException("Malformed native reference table header.");
            return;
        }
        if(declared.MediaType is not (AssetFormat.BRepMediaType or AssetFormat.XdeMediaType))return;
        if(declared.MediaType==AssetFormat.BRepMediaType)RequireBRep(declared);else RequireXde(declared);
        var actual=Inspect(bytes);
        if(actual.MediaType!=declared.MediaType||actual.Encoding!=declared.Encoding||actual.FormatVersion!=declared.FormatVersion)
            throw new InvalidDataException("Asset bytes do not match their declared format.");
    }
}
