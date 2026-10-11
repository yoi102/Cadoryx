using Cadoryx.Db;

namespace Cadoryx.Editor;

/// <summary>Compatibility entry point for one finite-line minor-arc fillet.</summary>
public static class SketchFillet
{
    public static CadSketch Create(CadSketch s,SketchEntityId first,SketchEntityId second,double radius)=>
        SketchCornerEditing.Lines(s,first,second,true,radius,0,0).Sketch;
}