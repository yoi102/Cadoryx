using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

internal static class IgesExtrusionProbe
{
    private static object Measure(Shape shape)
    {
        var faces=shape.GetFaces();
        try{return new{area=faces.Sum(f=>Math.Abs(f.InspectProperties(InspectionPropertyKind.Area).Mass)),
            faces=faces.Length,valid=shape.IsValid,extruded=faces.Count(f=>f.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.SurfaceOfExtrusion)};}
        finally{foreach(var face in faces)face.Dispose();}
    }
    private static double Area(Shape shape)
    {
        var faces=shape.GetFaces();try{return faces.Sum(f=>Math.Abs(f.InspectProperties(InspectionPropertyKind.Area).Mass));}
        finally{foreach(var face in faces)face.Dispose();}
    }
    internal static int Run(string[] args)
    {
        string directory=Path.GetFullPath(args[0]);Directory.CreateDirectory(directory);var results=new List<object>();bool passed=true;
        foreach(string profile in new[]{"rectangle","triangle","slot"})foreach(bool rotated in new[]{false,true})
        {
            string name=profile+(rotated?"-rotated":"-aligned");
            try
            {
                GpPoint[] points=profile switch
                {
                    "rectangle"=>[new(0,0,0),new(20,0,0),new(20,12,0),new(0,12,0)],
                    "triangle"=>[new(0,0,0),new(20,0,0),new(0,12,0)],
                    _=>[new(0,0,0),new(20,0,0),new(20,12,0),new(13,12,0),new(13,5,0),new(7,5,0),new(7,12,0),new(0,12,0)]
                };
                using var wire=ShapeFactory.CreatePolygonWire(points,true);using var face=ShapeFactory.CreatePlanarFace(wire);using var vector=GpVec.Create(0,0,4);
                using var prism=face.Extrude(vector);using var move=GpTrsf.Create(30,-20,10,1,2,3,rotated?.6:0);using var shape=prism.Transformed(move);
                using var before=RepairSnapshot.Create(shape);using var document=XdeDocument.Create();
                using(var transaction=document.BeginTransaction("IGES exact extrusion prerequisite")){var part=document.AddShape(shape,name);part.Color=new(1,0,0);transaction.Commit();}
                string direct=Path.Combine(directory,name+"-direct.iges"),step=Path.Combine(directory,name+"-canonical.step"),output=Path.Combine(directory,name+"-canonical.iges");
                document.WriteIges(direct);document.WriteStep(step);using var canonical=XdeDocument.ReadStep(step);canonical.WriteIges(output);
                using var reopened=ShapeExchange.ReadIges(output);using var directShape=ShapeExchange.ReadIges(direct);using var after=RepairSnapshot.Create(shape);
                double sourceArea=Area(shape),actualArea=Area(reopened);bool kept=Math.Abs(sourceArea-actualArea)<=Math.Max(1e-5,sourceArea*1e-6)&&reopened.IsValid&&before.Fingerprint==after.Fingerprint;
                using var reread=XdeDocument.ReadIges(output);var palette=reread.GetFreeShapes().SelectMany(l=>l.GetPresentationStyles()).ToArray();
                try{kept&=palette.Any(s=>s.EffectiveColor is {} c&&c.Red>.99&&c.Green<.01&&c.Blue<.01);}
                finally{foreach(var item in palette)item.Dispose();}
                if(!kept)passed=false;
                results.Add(new{name,passed=kept,source=Measure(shape),direct=Measure(directShape),canonical=Measure(reopened),sourceUnchanged=before.Fingerprint==after.Fingerprint,
                    output,sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(output)))});
            }
            catch(Exception error){passed=false;results.Add(new{name,passed=false,error=error.ToString()});}
        }
        File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{passed,results,basis="Existing locked NuGet; exact polygon extrusion, STEP representation conversion and IGES re-read; no application topology rebinding"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("IGES_EXTRUSION_PROBE_COMPLETE passed="+passed);return passed?0:1;
    }
}
