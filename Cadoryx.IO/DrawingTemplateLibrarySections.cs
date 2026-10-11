using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject] public sealed record PackDrawingCustomField([property:Key(0)] Guid Id,[property:Key(1)] string Caption,
    [property:Key(2)] string Value,[property:Key(3)] double[] Position,[property:Key(4)] double Width,[property:Key(5)] double Height);
[MessagePackObject] public sealed record PackDrawingNote([property:Key(0)] Guid Id,[property:Key(1)] string Text,
    [property:Key(2)] double[] Position,[property:Key(3)] double Width);
[MessagePackObject] public sealed record PackDrawingZones([property:Key(0)] int Columns,[property:Key(1)] int Rows,
    [property:Key(2)] bool ReverseColumns,[property:Key(3)] bool ReverseRows);
[MessagePackObject] public sealed record PackDrawingTemplateEntry([property:Key(0)] Guid Id,[property:Key(1)] string Name,
    [property:Key(2)] double Width,[property:Key(3)] double Height,[property:Key(4)] int Unit,[property:Key(5)] int Standard,
    [property:Key(6)] string Title,[property:Key(7)] string? Author,[property:Key(8)] PackSheetTemplate Template);

internal static partial class MessagePackSections
{
    private static ImmutableDictionary<Guid,DrawingTemplateDefinition> Library(PackDrawingTemplateEntry[]? entries)
    {
        if(entries is null||entries.Length>64)throw new InvalidDataException("Malformed drawing template library.");
        var library=ImmutableDictionary.CreateBuilder<Guid,DrawingTemplateDefinition>();
        foreach(var t in entries)
        {
            if(t is null||t.Template is null)throw new InvalidDataException("Missing drawing template entry.");
            var entry=new DrawingTemplateDefinition(t.Id,t.Name,t.Width,t.Height,(LengthUnit)t.Unit,(DrawingStandard)t.Standard,
                t.Title,t.Author,Template(t.Template)!);
            if(!library.TryAdd(t.Id,entry))throw new InvalidDataException("Duplicate drawing template ID.");
        }
        return library.ToImmutable();
    }
    internal static ReadOnlyMemory<byte> UpgradeDrawingTemplateLibrary(ReadOnlyMemory<byte> bytes)
    {
        VisitLegacyDrawingAnnotations(bytes);
        var old=Read<PackDrawingBook>(bytes);
        if(old.Templates is not null||old.Sheets is null||old.Sheets.Any(s=>s is null||s.Template is {} t&&
            (t.Fields is not null||t.Notes is not null||t.Zones is not null)))
            throw new InvalidDataException("New template library or sheet content in legacy drawing section.");
        return SerializeLegacyDrawingBook(old with{Templates=[],Sheets=old.Sheets.Select(s=>s with
            {Template=s.Template is {} t?t with{Fields=[],Notes=[]}:null}).ToArray()});
    }
}
