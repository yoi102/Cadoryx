using System.Text.Json;
using System.Text.Json.Serialization;
using Cadoryx.Db;

namespace Cadoryx.IO;

/// <summary>A single detached template; the format cannot carry model topology, assets or views.</summary>
public static class DrawingTemplateFile
{
    private sealed record Envelope(int Version,DrawingTemplateDefinition Template);
    private static readonly JsonSerializerOptions Options=new()
        {WriteIndented=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=16};
    public static DrawingTemplateDefinition Read(string path)
    {
        using var stream=File.OpenRead(path);
        if(stream.Length>128*1024)throw new InvalidDataException("Drawing template exceeds 128 KiB.");
        try
        {
            var envelope=JsonSerializer.Deserialize<Envelope>(stream,Options)??throw new InvalidDataException("Empty drawing template.");
            if(envelope.Version!=1||envelope.Template is null)throw new InvalidDataException("Unsupported drawing template version.");
            envelope.Template.Validate();return envelope.Template;
        }
        catch(Exception error) when(error is JsonException or CadValidationException or ArgumentException)
        {throw new InvalidDataException("Invalid drawing template.",error);}
    }
    public static void Write(DrawingTemplateDefinition entry,string path)
    {
        entry.Validate();var bytes=JsonSerializer.SerializeToUtf8Bytes(new Envelope(1,entry),Options);
        if(bytes.Length>128*1024)throw new InvalidDataException("Drawing template exceeds 128 KiB.");
        var full=Path.GetFullPath(path);var temporary=full+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temporary,bytes);File.Move(temporary,full,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
