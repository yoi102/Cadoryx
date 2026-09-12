using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.ViewModels;

namespace Cadoryx.wpf.Controls;

/// <summary>DIP-based XY authoring view; owns drawing and pointer gestures, never document state.</summary>
public sealed class SketchCanvas:FrameworkElement
{
    private SketchEditorViewModel? editor;
    private double scale=8;
    private Vector offset=new(90,350);
    private Point? pending;
    private SketchEntityId? pendingId;
    private SketchEntityId? dragging;
    private Point2d? draggedPosition;
    private Point pointer;
    private Point panStart;
    private bool panning;
    private bool initialized;
    public SketchCanvas()
    {
        Focusable=true;ClipToBounds=true;
        Loaded+=(_,_)=>Attach();Unloaded+=(_,_)=>Detach();DataContextChanged+=(_,_)=>{Detach();if(IsLoaded)Attach();};
    }
    private void Attach()
    {
        if(editor is not null||DataContext is not SketchEditorViewModel vm)return;
        editor=vm;vm.DrawingChanged+=OnChanged;vm.PropertyChanged+=OnPropertyChanged;
        if(!initialized){initialized=true;Fit();}InvalidateVisual();
    }
    private void Detach(){if(editor is not null){editor.DrawingChanged-=OnChanged;editor.PropertyChanged-=OnPropertyChanged;}editor=null;CancelGesture();}
    private void OnChanged(object? sender,EventArgs e)=>InvalidateVisual();
    private void OnPropertyChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(SketchEditorViewModel.Tool))CancelGesture();InvalidateVisual();}
    public Point ToScreen(Point2d point)=>new(offset.X+point.X*scale,offset.Y-point.Y*scale);
    public Point2d ToWorld(Point point)=>new((point.X-offset.X)/scale,(offset.Y-point.Y)/scale);
    public void Fit()
    {
        var sketch=editor?.Sketch;if(sketch is null||sketch.Points.IsEmpty){scale=8;offset=new(80,Math.Max(100,ActualHeight-80));InvalidateVisual();return;}
        double minX=sketch.Points.Min(p=>p.Position.X),maxX=sketch.Points.Max(p=>p.Position.X),minY=sketch.Points.Min(p=>p.Position.Y),maxY=sketch.Points.Max(p=>p.Position.Y);
        foreach(var c in sketch.Circles){var p=sketch.Points.Single(p=>p.Id==c.Center).Position;minX=Math.Min(minX,p.X-c.Radius);maxX=Math.Max(maxX,p.X+c.Radius);minY=Math.Min(minY,p.Y-c.Radius);maxY=Math.Max(maxY,p.Y+c.Radius);}
        scale=Math.Clamp(Math.Min(Math.Max(100,ActualWidth-100)/Math.Max(10,maxX-minX),Math.Max(100,ActualHeight-100)/Math.Max(10,maxY-minY)),0.01,5000);
        offset=new(ActualWidth/2-(minX+maxX)/2*scale,ActualHeight/2+(minY+maxY)/2*scale);InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(31,40,54)),null,new Rect(RenderSize));if(editor is null)return;
        var grid=new Pen(new SolidColorBrush(Color.FromRgb(46,58,74)),1);double step=editor.GridStep;
        if(!double.IsFinite(step)||step<=0)step=1;
        step*=Math.Pow(10,Math.Max(0,Math.Ceiling(Math.Log10(24/(scale*step)))));
        double pixel=step*scale;
        if(double.IsFinite(pixel)&&pixel>=1)
        {
            for(double x=((offset.X%pixel)+pixel)%pixel;x<ActualWidth;x+=pixel)dc.DrawLine(grid,new(x,0),new(x,ActualHeight));
            for(double y=((offset.Y%pixel)+pixel)%pixel;y<ActualHeight;y+=pixel)dc.DrawLine(grid,new(0,y),new(ActualWidth,y));
        }
        dc.DrawLine(new(Brushes.IndianRed,1),new(0,offset.Y),new(ActualWidth,offset.Y));dc.DrawLine(new(Brushes.SeaGreen,1),new(offset.X,0),new(offset.X,ActualHeight));
        var s=editor.Sketch;var points=s.Points.ToDictionary(p=>p.Id,p=>p.Id==dragging&&draggedPosition is {} pos?pos:p.Position);
        var conflicts=editor.Report?.ConflictingConstraints.ToHashSet()??[];
        var targets=s.Constraints.Where(c=>conflicts.Contains(c.Id)).SelectMany(SketchConstraintEditing.Targets).ToHashSet();
        Brush ColorFor(SketchEntityId id)=>targets.Contains(id)?Brushes.OrangeRed:editor.SelectedEntities.Contains(id)?Brushes.Gold:Brushes.LightSkyBlue;
        Pen PenFor(SketchEntityId id,bool construction){var pen=new Pen(ColorFor(id),2);if(construction)pen.DashStyle=DashStyles.Dash;return pen;}
        foreach(var l in s.Lines)dc.DrawLine(PenFor(l.Id,l.IsConstruction),ToScreen(points[l.Start]),ToScreen(points[l.End]));
        foreach(var c in s.Circles)dc.DrawEllipse(null,PenFor(c.Id,c.IsConstruction),ToScreen(points[c.Center]),c.Radius*scale,c.Radius*scale);
        foreach(var p in s.Points)dc.DrawEllipse(ColorFor(p.Id),null,ToScreen(points[p.Id]),editor.SelectedEntities.Contains(p.Id)?5:3.5,editor.SelectedEntities.Contains(p.Id)?5:3.5);
        foreach(var c in s.Constraints.Where(c=>c.IsEnabled))
        {
            var value=SketchConstraintEditing.Value(c);if(value is null||c is FixPointConstraint)continue;Point anchor;
            var ids=SketchConstraintEditing.Targets(c);
            if(c is RadiusConstraint r){var circle=s.Circles.Single(x=>x.Id==r.Circle);var center=points[circle.Center];anchor=ToScreen(new(center.X+circle.Radius,center.Y));}
            else if(c is LengthConstraint length){var line=s.Lines.Single(l=>l.Id==length.Line);var a=points[line.Start];var b=points[line.End];anchor=ToScreen(new((a.X+b.X)/2,(a.Y+b.Y)/2));}
            else {var a=points[ids[0]];var b=points[ids[1]];anchor=ToScreen(new((a.X+b.X)/2,(a.Y+b.Y)/2));}
            string prefix=c switch{RadiusConstraint=>"R ",OffsetXConstraint=>"X ",OffsetYConstraint=>"Y ",_=>""};
            Text(dc,prefix+value.Value.ToString("G6",CultureInfo.CurrentCulture),anchor+new Vector(6,-22),conflicts.Contains(c.Id)?Brushes.OrangeRed:Brushes.LightGreen);
        }
        if(pending is {} first)
        {
            var pen=new Pen(Brushes.White,1){DashStyle=DashStyles.Dash};var end=ToScreen(SnapPoint(pointer).Position);
            if(editor.Tool=="Circle"){double radius=(end-first).Length;dc.DrawEllipse(null,pen,first,radius,radius);}
            else if(editor.Tool=="Rectangle")dc.DrawRectangle(null,pen,new Rect(first,end));else dc.DrawLine(pen,first,end);
        }
        var world=ToWorld(pointer);Text(dc,$"X {world.X:F3}   Y {world.Y:F3} mm",new(12,Math.Max(0,ActualHeight-26)),Brushes.LightGray);
    }
    private void Text(DrawingContext dc,string text,Point at,Brush brush)=>dc.DrawText(new FormattedText(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip),at);
    private (Point2d Position,SketchEntityId? Id) SnapPoint(Point screen)
    {
        var position=ToWorld(screen);if(editor is null||!editor.Snap)return(position,null);
        var point=editor.Sketch.Points.OrderBy(p=>(ToScreen(p.Position)-screen).Length).FirstOrDefault();
        if(point is not null&&(ToScreen(point.Position)-screen).Length<=9)return(point.Position,point.Id);
        double grid=editor.GridStep;if(double.IsFinite(grid)&&grid>0)position=new(Math.Round(position.X/grid)*grid,Math.Round(position.Y/grid)*grid);
        return(position,null);
    }
    private SketchEntityId? Hit(Point at)
    {
        if(editor is null)return null;var s=editor.Sketch;var p=s.Points.OrderBy(p=>(ToScreen(p.Position)-at).Length).FirstOrDefault();
        if(p is not null&&(ToScreen(p.Position)-at).Length<9)return p.Id;
        var points=s.Points.ToDictionary(p=>p.Id,p=>ToScreen(p.Position));
        foreach(var c in s.Circles)if(Math.Abs((at-points[c.Center]).Length-c.Radius*scale)<7)return c.Id;
        foreach(var l in s.Lines)
        {
            var a=points[l.Start];var v=points[l.End]-a;double t=Math.Clamp(Vector.Multiply(at-a,v)/v.LengthSquared,0,1);
            if((at-(a+t*v)).Length<7)return l.Id;
        }
        return null;
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);Focus();pointer=e.GetPosition(this);
        if(e.ChangedButton==MouseButton.Middle){pending=null;pendingId=null;panning=true;panStart=pointer;CaptureMouse();e.Handled=true;return;}
        if(e.ChangedButton!=MouseButton.Left||editor is not {CanEdit:true})return;
        if(editor.Tool=="Select")
        {
            var id=Hit(pointer);bool extend=(Keyboard.Modifiers&ModifierKeys.Control)!=0;editor.Select(id,extend);
            if(!extend&&id is {} point&&editor.Sketch.Points.Any(p=>p.Id==point)){dragging=point;draggedPosition=null;CaptureMouse();}
        }
        else
        {
            var snap=SnapPoint(pointer);
            if(editor.Tool=="Point")editor.Mutate(d=>d.AddPoint(snap.Position,editor.Construction));
            else if(pending is null){pending=ToScreen(snap.Position);pendingId=snap.Id;}
            else
            {
                var start=ToWorld(pending.Value);var startId=pendingId;
                if(editor.Tool=="Line")editor.Mutate(d=>d.AddLine(start,snap.Position,startId,snap.Id,editor.Construction));
                else if(editor.Tool=="Rectangle")editor.Mutate(d=>d.AddRectangle(start,snap.Position,editor.Construction));
                else if(editor.Tool=="Circle")editor.Mutate(d=>d.AddCircle(start,double.Hypot(start.X-snap.Position.X,start.Y-snap.Position.Y),startId,editor.Construction));
                pending=null;pendingId=null;
            }
        }
        e.Handled=true;InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);pointer=e.GetPosition(this);
        if(panning){offset+=pointer-panStart;panStart=pointer;}
        else if(dragging is not null){draggedPosition=SnapPoint(pointer).Position;}
        InvalidateVisual();
    }
    protected override async void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if(e.ChangedButton==MouseButton.Middle){panning=false;ReleaseMouseCapture();return;}
        if(e.ChangedButton==MouseButton.Left&&dragging is {} point)
        {
            var position=draggedPosition;dragging=null;draggedPosition=null;ReleaseMouseCapture();
            if(position is {} value&&editor is {CanEdit:true}){editor.MovePoint(point,value);await editor.PreviewCommand.ExecuteAsync(null);}
        }
    }
    protected override void OnLostMouseCapture(MouseEventArgs e){base.OnLostMouseCapture(e);dragging=null;draggedPosition=null;panning=false;InvalidateVisual();}
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);var at=e.GetPosition(this);var before=ToWorld(at);scale=Math.Clamp(scale*Math.Pow(1.15,e.Delta/120d),0.01,5000);
        offset=new(at.X-before.X*scale,at.Y+before.Y*scale);pending=null;pendingId=null;InvalidateVisual();e.Handled=true;
    }
    public bool CancelGesture()
    {
        bool active=pending is not null||dragging is not null||panning;pending=null;pendingId=null;dragging=null;draggedPosition=null;panning=false;
        if(IsMouseCaptured)ReleaseMouseCapture();InvalidateVisual();return active;
    }
}
