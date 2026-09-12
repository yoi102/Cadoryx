namespace Cadoryx.Db;

/// <summary>Format identity and original writer provenance, independent of the current reader.</summary>
public sealed record AssetFormat(string MediaType,string Encoding,int FormatVersion,
    string? Kernel=null,string? KernelVersion=null,string? Writer=null,string? WriterVersion=null)
{
    public const string BRepMediaType="application/vnd.opencascade.brep";
    public const string XdeMediaType="application/vnd.opencascade.xde";
    public static AssetFormat Opaque {get;}=new("application/octet-stream","opaque",1);
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(MediaType)||!MediaType.Contains('/')||MediaType.Length>128||
            string.IsNullOrWhiteSpace(Encoding)||Encoding.Length>64||FormatVersion<1)
            throw new CadValidationException("Invalid asset format identity.");
        foreach(var value in new[]{Kernel,KernelVersion,Writer,WriterVersion})
            if(value is not null&&(string.IsNullOrWhiteSpace(value)||value.Length>128))throw new CadValidationException("Invalid asset provenance.");
        if(KernelVersion is not null&&Kernel is null||WriterVersion is not null&&Writer is null)
            throw new CadValidationException("Version requires an identified producer.");
    }
}
