using OcctSharp;
using System.Text.RegularExpressions;

// Maintenance-only source fixture generator, independent of the Cadoryx adapters.
// Normal tests consume the checked-in exchange text and never run this generator.
string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
using(var document=XdeDocument.Create())
{
    using var box=ShapeFactory.CreateBox(1,2,3);
    using(var transaction=document.BeginTransaction("Analytic unit box"))
    {
        var label=document.AddShape(box,"UnitBox");label.Color=new(1,0,0);transaction.Commit();
    }
    document.WriteStep(Path.Combine(output,"unit-mm.step"));
    document.WriteIges(Path.Combine(output,"unit-mm.iges"));
}
// Change only the declared source unit; numeric coordinates remain 1 x 2 x 3.
// This intentionally does not pass through Cadoryx's exporter or any unit conversion.
string millimeters=File.ReadAllText(Path.Combine(output,"unit-mm.step")).Replace("\r\n","\n");
File.WriteAllText(Path.Combine(output,"unit-meter.step"),millimeters.Replace("SI_UNIT(.MILLI.,.METRE.)","SI_UNIT($,.METRE.)"));
string inches=Regex.Replace(millimeters,@"#(\d+) = \( LENGTH_UNIT\(\) NAMED_UNIT\(\*\) SI_UNIT\(\.MILLI\.,\.METRE\.\) \);",
    m=>$"#{m.Groups[1]} = ( CONVERSION_BASED_UNIT('INCH',#9001) LENGTH_UNIT() NAMED_UNIT(#9002) );");
inches=inches.Replace("ENDSEC;\nEND-ISO", "#9001 = LENGTH_MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4),#9003);\n#9002 = DIMENSIONAL_EXPONENTS(1.,0.,0.,0.,0.,0.,0.);\n#9003 = ( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) );\nENDSEC;\nEND-ISO");
File.WriteAllText(Path.Combine(output,"unit-inch.step"),inches);
foreach(var (name,flag,unit) in new[]{("inch",1,"IN"),("meter",6,"M")})
{
    var lines=File.ReadAllLines(Path.Combine(output,"unit-mm.iges"));
    for(int i=0;i<lines.Length;i++)if(lines[i][72]=='G')
        lines[i]=lines[i][..72].Replace(",1.,2,2HMM,",$",1.,{flag},{unit.Length}H{unit},").TrimEnd().PadRight(72)+lines[i][72..];
    File.WriteAllLines(Path.Combine(output,$"unit-{name}.iges"),lines);
}
using(var document=XdeDocument.Create())
{
    using var box=ShapeFactory.CreateBox(10,20,30);
    using(var transaction=document.BeginTransaction("Rotated repeated assembly"))
    {
        var root=document.AddAssembly("Root");var nested=document.AddAssembly("Nested");
        var part=document.AddShape(box,"ColoredBox");part.Color=new(1,0,0);
        Add(nested,part,"LocalY90",5,0,0,0,1,0,Math.PI/2);
        Add(root,nested,"ParentZ90",100,0,0,0,0,1,Math.PI/2);
        Add(root,nested,"ParentZ180",0,200,0,0,0,1,Math.PI);
        transaction.Commit();
    }
    document.WriteStep(Path.Combine(output,"rotated-colors.step"));
    document.WriteIges(Path.Combine(output,"rotated-colors.iges"));
    void Add(XdeLabel parent,XdeLabel child,string name,double x,double y,double z,double ax,double ay,double az,double angle)
    {
        using var transform=GpTrsf.Create(x,y,z,ax,ay,az,angle);
        using var location=TopLocLocation.FromTransform(transform);
        document.AddComponent(parent,child,location).Name=name;
    }
}

// Add explicit face styles to the fixed STEP source: +Z green, -Y blue, body red.
string colored=File.ReadAllText(Path.Combine(output,"rotated-colors.step")).Replace("\r\n","\n");
var faces=Regex.Matches(colored,@"#(\d+) = ADVANCED_FACE").Select(m=>m.Groups[1].Value).ToArray();
var styledItems=new List<string>();var extra=new System.Text.StringBuilder();
for(int i=0;i<2;i++)
{
    int n=9100+i*10;styledItems.Add($"#{n}");
    string face=faces[i==0?5:2];string color=i==0?"green":"blue";
    extra.AppendLine($"#{n} = STYLED_ITEM('face',(# {n+1}),#{face});".Replace("# ","#"));
    extra.AppendLine($"#{n+1} = PRESENTATION_STYLE_ASSIGNMENT((#{n+2}));");
    extra.AppendLine($"#{n+2} = SURFACE_STYLE_USAGE(.BOTH.,#{n+3});");
    extra.AppendLine($"#{n+3} = SURFACE_SIDE_STYLE('',(#{n+4}));");
    extra.AppendLine($"#{n+4} = SURFACE_STYLE_FILL_AREA(#{n+5});");
    extra.AppendLine($"#{n+5} = FILL_AREA_STYLE('',(#{n+6}));");
    extra.AppendLine($"#{n+6} = FILL_AREA_STYLE_COLOUR('',#{n+7});");
    extra.AppendLine($"#{n+7} = DRAUGHTING_PRE_DEFINED_COLOUR('{color}');");
}
colored=Regex.Replace(colored,@"(MECHANICAL_DESIGN_GEOMETRIC_PRESENTATION_REPRESENTATION\('',\()(#\d+)(\))",m=>m.Groups[1].Value+m.Groups[2].Value+","+string.Join(",",styledItems)+m.Groups[3].Value);
colored=colored.Replace("ENDSEC;\nEND-ISO",extra+"ENDSEC;\nEND-ISO");
File.WriteAllText(Path.Combine(output,"rotated-colors.step"),colored);
