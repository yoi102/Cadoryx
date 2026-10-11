using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel : ITechnicalDrawingKernel
{
    internal Action? DrawingNativeStarted {get;set;}
    public Task<DrawingProjectionResult> ProjectAsync(IReadOnlyList<GeometryInstance> instances,
        DrawingViewKind kind,Vector3d? sectionNormal,double sectionOffsetMm,
        IAssetStore assets,CancellationToken token=default,double sectionHatchSpacingMm=2.5,
        IReadOnlyList<SectionHatchPattern>? sectionHatchPatterns=null)
    {
        if(!Enum.IsDefined(kind)||!double.IsFinite(sectionOffsetMm)||Math.Abs(sectionOffsetMm)>1e9)
            throw new CadValidationException("Invalid drawing projection.");
        if(kind==DrawingViewKind.Section&&sectionNormal is not {} normal)
            throw new CadValidationException("Section projection needs a cutting plane.");
        if(sectionNormal is {} n&&Math.Abs(n.Length-1)>1e-8)
            throw new CadValidationException("Section normal must be unit length.");
        if(!double.IsFinite(sectionHatchSpacingMm)||sectionHatchSpacingMm<=0||sectionHatchSpacingMm>1e9)
            throw new CadValidationException("Section hatch spacing must be finite and positive.");
        var inputs=ReviewInputs(instances,1,256);
        var patterns=sectionHatchPatterns?.ToArray()??inputs.Select((_,index)=>
            new SectionHatchPattern(index%2==0?45:135,sectionHatchSpacingMm)).ToArray();
        if(patterns.Length!=inputs.Length||patterns.Any(p=>p is null||
           !double.IsFinite(p.AngleDegrees)||p.AngleDegrees is <0 or >=180||
           !double.IsFinite(p.SpacingMm)||p.SpacingMm<=0||p.SpacingMm>1e9))
            throw new CadValidationException("Invalid source-aligned section hatch patterns.");
        return Run(() =>
        {
            if(drawingExecutor is {} executor)
                return executor.ProjectAsync(inputs,kind,sectionNormal,sectionOffsetMm,assets,token,sectionHatchSpacingMm,patterns).GetAwaiter().GetResult();
            var shapes=new List<Shape>();
            try
            {
                foreach(var input in inputs)
                {token.ThrowIfCancellationRequested();shapes.Add(WorldShape(input,assets));}
                DrawingProjection projection=kind switch
                {
                    DrawingViewKind.Front=>DrawingProjection.Front,
                    DrawingViewKind.Top=>DrawingProjection.Top,
                    DrawingViewKind.Right=>DrawingProjection.Right,
                    DrawingViewKind.Isometric=>DrawingProjection.Isometric,
                    DrawingViewKind.Section=>new(new(0,0,0),new(sectionNormal!.Value.X,sectionNormal.Value.Y,sectionNormal.Value.Z),
                        Math.Abs(sectionNormal.Value.Z)<.9?new(0,0,1):new(0,1,0)),
                    _=>throw new CadValidationException("Unsupported drawing projection.")
                };
                if(kind==DrawingViewKind.Section)
                {
                    DrawingNativeStarted?.Invoke();
                    return ProjectSection(shapes,projection,sectionNormal!.Value,
                        sectionOffsetMm,patterns,token);
                }
                DrawingNativeStarted?.Invoke();return Project(shapes,projection,token);
            }
            finally{foreach(var shape in shapes)shape.Dispose();}
        },token);
    }

    private static DrawingProjectionResult Project(IReadOnlyList<Shape> shapes,DrawingProjection projection,CancellationToken token)
    {
        using var view=TechnicalDrawing.CreateView(shapes,projection,new DrawingOptions
        {Algorithm=DrawingAlgorithm.Exact,IsoparameterCount=0,SamplesPerCurve=32});
        var strokes=ImmutableArray.CreateBuilder<DrawingStroke>();
        double minX=double.PositiveInfinity,minY=double.PositiveInfinity,maxX=double.NegativeInfinity,maxY=double.NegativeInfinity;
        int points=0;
        foreach(var layer in view.Layers)
        {
            token.ThrowIfCancellationRequested();
            var kind=layer.Category switch
            {
                DrawingEdgeCategory.Sharp=>DrawingLineKind.Sharp,
                DrawingEdgeCategory.Smooth=>DrawingLineKind.Smooth,
                DrawingEdgeCategory.Sewn=>DrawingLineKind.Sewn,
                DrawingEdgeCategory.Outline=>DrawingLineKind.Outline,
                _=>DrawingLineKind.Isoparameter
            };
            foreach(var line in TechnicalDrawing.CopyPolylines(layer.Shape,32))
            {
                if(line.Points.Count<2)continue;
                points+=line.Points.Count;
                if(strokes.Count>=100000||points>1000000)throw new CadValidationException("Drawing projection exceeds its polyline budget.");
                var coordinates=ImmutableArray.CreateBuilder<Point2d>(line.Points.Count);
                foreach(var point in line.Points)
                {
                    if(!double.IsFinite(point.X)||!double.IsFinite(point.Y))
                        throw new CadValidationException("Drawing projection contains a non-finite point.");
                    coordinates.Add(new(point.X,point.Y));
                    minX=Math.Min(minX,point.X);maxX=Math.Max(maxX,point.X);
                    minY=Math.Min(minY,point.Y);maxY=Math.Max(maxY,point.Y);
                }
                strokes.Add(new(kind,layer.Visibility==DrawingVisibility.Hidden,coordinates.ToImmutable(),line.Closed));
            }
        }
        if(strokes.Count==0)throw new CadValidationException("Projection has no visible or hidden geometry.");
        return new(strokes.ToImmutable(),new(minX,minY),new(maxX,maxY));
    }
}
