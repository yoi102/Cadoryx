using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    // Boundaries and hatch clipping both come from original BRep booleans.
    // A sampled closed projection is never used to classify material or holes.
    private static DrawingProjectionResult ProjectSection(IReadOnlyList<Shape> shapes,
        DrawingProjection projection,Vector3d normal,double offset,
        IReadOnlyList<SectionHatchPattern> patterns,CancellationToken token)
    {
        var strokes=ImmutableArray.CreateBuilder<DrawingStroke>();
        var sections=new List<Shape>();
        var up=Math.Abs(normal.Z)<.9?Vector3d.UnitZ:new Vector3d(0,1,0);
        var u=up.Cross(normal).Normalized();var v=normal.Cross(u).Normalized();
        var origin=normal*offset;
        GpPoint World(double x,double y)
        {var p=origin+u*x+v*y;return new(p.X,p.Y,p.Z);}
        try
        {
            for(int index=0;index<shapes.Count;index++)
            {
                token.ThrowIfCancellationRequested();var shape=shapes[index];
                // Preserve all exact section edges. Only actual solid subshapes
                // contribute material, including solids inside mixed compounds.
                // Open sheet/wire components can never become filled regions.
                sections.Add(TechnicalDrawing.CreateSection(shape,
                    GpPlane.Create(new(origin.X,origin.Y,origin.Z),new(normal.X,normal.Y,normal.Z)),false));
                var pattern=patterns[index];if(!pattern.Enabled)continue;
                double spacing=pattern.SpacingMm;
                var solids=shape.GetSubShapes(ShapeKind.Solid);
                if(solids.Length==0)continue;
                try
                {
                    var b=shape.GetBoundingBox();var points=new List<Point2d>();
                    foreach(double x in new[]{b.Minimum.X,b.Maximum.X})
                    foreach(double y in new[]{b.Minimum.Y,b.Maximum.Y})
                    foreach(double z in new[]{b.Minimum.Z,b.Maximum.Z})
                    {var p=new Vector3d(x,y,z)-origin;points.Add(new(p.Dot(u),p.Dot(v)));}
                    double minX=points.Min(p=>p.X),maxX=points.Max(p=>p.X),minY=points.Min(p=>p.Y),maxY=points.Max(p=>p.Y);
                    double margin=Math.Max(1,Math.Max(maxX-minX,maxY-minY)*.01);
                    using var wire=ShapeFactory.CreatePolygonWire([World(minX-margin,minY-margin),World(maxX+margin,minY-margin),
                        World(maxX+margin,maxY+margin),World(minX-margin,maxY+margin)],true);
                    using var plane=ShapeFactory.CreatePlanarFace(wire);
                    using var material=ShapeFactory.CreateCompound(solids);
                    using var region=material.Common(plane);
                    if(region.GetTopologySummary().UniqueCounts.FaceCount==0)continue;
                    if(!region.IsValid)throw new CadValidationException("Section material region has invalid topology.");
                    double angle=pattern.AngleDegrees*Math.PI/180;
                    double dx=Math.Cos(angle),dy=Math.Sin(angle),nx=-dy,ny=dx;
                    double minQ=points.Min(p=>p.X*nx+p.Y*ny),maxQ=points.Max(p=>p.X*nx+p.Y*ny);
                    double minT=points.Min(p=>p.X*dx+p.Y*dy)-margin,maxT=points.Max(p=>p.X*dx+p.Y*dy)+margin;
                    double first=Math.Ceiling(minQ/spacing),last=Math.Floor(maxQ/spacing);
                    if(!double.IsFinite(first)||!double.IsFinite(last)||last-first+1>4096)
                        throw new CadValidationException("Section hatching exceeds 4096 lines per solid.");
                    var hatchLines=new List<Shape>();
                    try
                    {
                        for(double line=first;line<=last;line++)
                        {
                            token.ThrowIfCancellationRequested();double q=line*spacing;
                            hatchLines.Add(ShapeFactory.CreateEdge(World(nx*q+dx*minT,ny*q+dy*minT),
                                World(nx*q+dx*maxT,ny*q+dy*maxT)));
                        }
                        if(hatchLines.Count==0)continue;
                        using var candidates=ShapeFactory.CreateCompound(hatchLines);
                        using var clipped=region.Common(candidates);
                        foreach(var line in TechnicalDrawing.CopyPolylines(clipped,2))
                        {
                            token.ThrowIfCancellationRequested();
                            if(strokes.Count>=100000)throw new CadValidationException("Section hatching exceeds its stroke budget.");
                            if(line.Points.Count!=2)throw new CadValidationException("Section hatch clipping did not return straight edges.");
                            strokes.Add(new(DrawingLineKind.SectionHatch,false,[..line.Points.Select(p=>
                                new Point2d(new Vector3d(p.X,p.Y,p.Z).Dot(u),new Vector3d(p.X,p.Y,p.Z).Dot(v)))]));
                        }
                    }
                    finally{foreach(var line in hatchLines)line.Dispose();}
                }
                finally{foreach(var solid in solids)solid.Dispose();}
            }
            var contour=Project(sections,projection,token);
            if(contour.Strokes.Length+strokes.Count>100000)
                throw new CadValidationException("Section projection exceeds its combined stroke budget.");
            return contour with{Strokes=contour.Strokes.AddRange(strokes)};
        }
        finally{foreach(var section in sections)section.Dispose();}
    }
}
