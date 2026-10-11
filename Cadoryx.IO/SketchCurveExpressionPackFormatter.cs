using MessagePack;
using MessagePack.Formatters;

namespace Cadoryx.IO;

/// <summary>Apply the geometry schema's strict shape budgets even when nested in a feature recipe.</summary>
public sealed class SketchCurveExpressionPackFormatter:IMessagePackFormatter<PackSketchCurveExpression?>
{
    public void Serialize(ref MessagePackWriter writer,PackSketchCurveExpression? value,MessagePackSerializerOptions options)
    {
        if(value is null){writer.WriteNil();return;}
        if(value.SchemaVersion!=SketchCurveExpressionStorage.SchemaVersion||value.Nodes is not {Length:>=1 and <=64})
            throw new MessagePackSerializationException("Invalid expression geometry schema or node budget.");
        writer.WriteArrayHeader(2);writer.Write(value.SchemaVersion);writer.WriteArrayHeader(value.Nodes.Length);
        var formatter=options.Resolver.GetFormatterWithVerify<PackSketchExpressionNode>();
        foreach(var node in value.Nodes)formatter.Serialize(ref writer,node,options);
    }
    public PackSketchCurveExpression? Deserialize(ref MessagePackReader reader,MessagePackSerializerOptions options)
    {
        if(reader.TryReadNil())return null;
        var shape=reader;SketchCurveExpressionStorage.CheckMessagePackValue(ref shape);
        options.Security.DepthStep(ref reader);
        try
        {
            reader.ReadArrayHeader();int version=reader.ReadInt32();int count=reader.ReadArrayHeader();var nodes=new PackSketchExpressionNode[count];
            var formatter=options.Resolver.GetFormatterWithVerify<PackSketchExpressionNode>();
            for(int i=0;i<count;i++)nodes[i]=formatter.Deserialize(ref reader,options);
            return new(version,nodes);
        }
        finally{reader.Depth--;}
    }
}
