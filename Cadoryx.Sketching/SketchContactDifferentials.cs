using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Sketching;

/// <summary>Analytic chain-rule derivatives with respect to normalized solver coordinates.</summary>
internal static class SketchContactDifferentials
{
    internal readonly record struct Scalar(double Value,double[] Gradient)
    {
        internal static Scalar Constant(double value,int count)=>new(value,new double[count]);
        internal static Scalar Variable(double value,int index,int count)
        {var gradient=new double[count];gradient[index]=1;return new(value,gradient);}
        internal Scalar Compose(double value,double derivative)=>Map(value,derivative);
        private Scalar Map(double value,double factor)=>new(value,Gradient.Select(d=>d*factor).ToArray());
        public static Scalar operator +(Scalar a,Scalar b)=>new(a.Value+b.Value,a.Gradient.Zip(b.Gradient,(x,y)=>x+y).ToArray());
        public static Scalar operator -(Scalar a,Scalar b)=>new(a.Value-b.Value,a.Gradient.Zip(b.Gradient,(x,y)=>x-y).ToArray());
        public static Scalar operator -(Scalar a)=>a.Map(-a.Value,-1);
        public static Scalar operator +(Scalar a,double b)=>new(a.Value+b,a.Gradient);
        public static Scalar operator -(Scalar a,double b)=>new(a.Value-b,a.Gradient);
        public static Scalar operator *(Scalar a,double b)=>a.Map(a.Value*b,b);
        public static Scalar operator *(double a,Scalar b)=>b*a;
        public static Scalar operator /(Scalar a,double b)=>a*(1/b);
        public static Scalar operator *(Scalar a,Scalar b)=>new(a.Value*b.Value,a.Gradient.Zip(b.Gradient,(x,y)=>x*b.Value+a.Value*y).ToArray());
        public static Scalar operator /(Scalar a,Scalar b)=>new(a.Value/b.Value,a.Gradient.Zip(b.Gradient,(x,y)=>(x-a.Value/b.Value*y)/b.Value).ToArray());
        internal static Scalar Sqrt(Scalar a){double value=Math.Sqrt(a.Value);return a.Map(value,.5/value);}
        internal static Scalar Sin(Scalar a)=>a.Map(Math.Sin(a.Value),Math.Cos(a.Value));
        internal static Scalar Cos(Scalar a)=>a.Map(Math.Cos(a.Value),-Math.Sin(a.Value));
        internal static Scalar Atan2(Scalar y,Scalar x)
        {double square=x.Value*x.Value+y.Value*y.Value;return new(Math.Atan2(y.Value,x.Value),y.Gradient.Zip(x.Gradient,(dy,dx)=>(x.Value*dy-y.Value*dx)/square).ToArray());}
    }
    internal readonly record struct Vector(Scalar X,Scalar Y)
    {
        public static Vector operator +(Vector a,Vector b)=>new(a.X+b.X,a.Y+b.Y);
        public static Vector operator -(Vector a,Vector b)=>new(a.X-b.X,a.Y-b.Y);
        public static Vector operator *(Vector a,double b)=>new(a.X*b,a.Y*b);
        public static Vector operator *(Vector a,Scalar b)=>new(a.X*b,a.Y*b);
        public static Vector operator /(Vector a,Scalar b)=>new(a.X/b,a.Y/b);
    }
    internal sealed record Geometry(Vector Position,Vector Unit,Scalar Curvature,Scalar Speed);

    internal static Geometry Evaluate(CadSketch sketch,SketchEntityId curve,double parameter,double[] x,
        IReadOnlyDictionary<SketchEntityId,int> points,IReadOnlyDictionary<SketchEntityId,int> radii,
        Point2d origin,double scale,
        IReadOnlyDictionary<(SketchEntityId Curve,double Parameter),ImmutableArray<(SketchEntityId Point,double Position,double First,double Second)>> weights,
        FiniteContactContinuity continuity=FiniteContactContinuity.Tangent)
        =>EvaluateWithChart(sketch,curve,parameter,x,points,radii,origin,scale,weights,continuity,null);

    internal static Geometry EvaluateWithChart(CadSketch sketch,SketchEntityId curve,double parameter,double[] x,
        IReadOnlyDictionary<SketchEntityId,int> points,IReadOnlyDictionary<SketchEntityId,int> radii,Point2d origin,double scale,
        IReadOnlyDictionary<(SketchEntityId Curve,double Parameter),ImmutableArray<(SketchEntityId Point,double Position,double First,double Second)>> weights,
        FiniteContactContinuity continuity,SketchRegionSolveChart? regionChart)
        =>EvaluateCore(sketch,curve,Scalar.Constant(parameter,x.Length),x,points,radii,origin,scale,weights,
            continuity==FiniteContactContinuity.Curvature?2:1,continuity==FiniteContactContinuity.Curvature,regionChart);

    /// <summary>Parameter chain rule for an explicit G1/G2 moving parameter.
    /// G1 needs D2; G2 also needs a proved D3. Curvature gradients are consumed only for G2.</summary>
    internal static Geometry EvaluateParameter(CadSketch sketch,SketchEntityId curve,Scalar parameter,double[] x,
        IReadOnlyDictionary<SketchEntityId,int> points,IReadOnlyDictionary<SketchEntityId,int> radii,
        Point2d origin,double scale,FiniteContactContinuity continuity)
        =>EvaluateParameterWithChart(sketch,curve,parameter,x,points,radii,origin,scale,continuity,null);

    internal static Geometry EvaluateParameterWithChart(CadSketch sketch,SketchEntityId curve,Scalar parameter,double[] x,
        IReadOnlyDictionary<SketchEntityId,int> points,IReadOnlyDictionary<SketchEntityId,int> radii,
        Point2d origin,double scale,FiniteContactContinuity continuity,SketchRegionSolveChart? regionChart)
    {
        if(!Enum.IsDefined(continuity)||parameter.Gradient.Length!=x.Length)
            throw new CadValidationException("Invalid contact differential order or parameter coordinate count.");
        return EvaluateCore(sketch,curve,parameter,x,points,radii,origin,scale,
            new Dictionary<(SketchEntityId,double),ImmutableArray<(SketchEntityId,double,double,double)>>(),
            continuity==FiniteContactContinuity.Curvature?3:2,continuity==FiniteContactContinuity.Curvature,regionChart);
    }
    private static Geometry EvaluateCore(CadSketch sketch,SketchEntityId curve,Scalar parameter,double[] x,
        IReadOnlyDictionary<SketchEntityId,int> points,IReadOnlyDictionary<SketchEntityId,int> radii,
        Point2d origin,double scale,
        IReadOnlyDictionary<(SketchEntityId Curve,double Parameter),ImmutableArray<(SketchEntityId Point,double Position,double First,double Second)>> weights,
        int requiredOrder,bool includeCurvature,SketchRegionSolveChart? regionChart)
    {
        if(requiredOrder>1&&(!double.IsFinite(parameter.Value)||parameter.Value<=0||parameter.Value>=1))
            throw new CadValidationException("Moving contact parameters must be strictly inside (0,1).");
        if(sketch.ExpressionCurves.Any(c=>c.Id==curve))
            return SketchExpressionDifferentials.EvaluateRegionContact(sketch,curve,parameter,x,points,radii,origin,scale,requiredOrder,includeCurvature,regionChart);
        Scalar C(double value)=>Scalar.Constant(value,x.Length);
        Vector P(SketchEntityId id){int i=points[id];return new(Scalar.Variable(x[i],i,x.Length),Scalar.Variable(x[i+1],i+1,x.Length));}
        Vector Constant(Point2d value)=>new(C(value.X),C(value.Y));
        Vector position,first,second;
        if(requiredOrder>1&&SketchFiniteParametricContact.HasControlWeights(sketch,curve))
        {
            var controls=requiredOrder==3?SketchFiniteParametricContact.ThirdDerivativeWeights(sketch,curve,parameter.Value):
                SketchFiniteParametricContact.DerivativeWeights(sketch,curve,parameter.Value).Select(w=>(w.Point,w.Position,w.First,w.Second,Third:0d)).ToImmutableArray();
            var anchor=P(controls[0].Point);position=anchor;first=second=Constant(default);
            foreach(var w in controls)
            {
                var delta=P(w.Point)-anchor;
                position+=delta*parameter.Compose(w.Position,w.First);
                first+=delta*parameter.Compose(w.First,w.Second);
                second+=delta*parameter.Compose(w.Second,w.Third);
            }
        }
        else if(weights.TryGetValue((curve,parameter.Value),out var controls))
        {
            var anchor=P(controls[0].Point);position=anchor;first=second=Constant(default);
            foreach(var w in controls)
            {
                var delta=P(w.Point)-anchor;position+=delta*w.Position;first+=delta*w.First;second+=delta*w.Second;
            }
        }
        else if(sketch.Circles.SingleOrDefault(c=>c.Id==curve) is {} circle)
        {
            int i=radii[circle.Id];Circular(P(circle.Center),Scalar.Variable(x[i],i,x.Length),C(0),C(Math.Tau),out position,out first,out second);
        }
        else if(sketch.Arcs.SingleOrDefault(c=>c.Id==curve) is {} arc)
        {
            var start=P(arc.Start);var u=P(arc.Middle)-start;var v=P(arc.End)-start;
            var us=u.X*u.X+u.Y*u.Y;var vs=v.X*v.X+v.Y*v.Y;
            var cross=u.X*v.Y-u.Y*v.X;var denominator=cross*2;
            var delta=new Vector((us*v.Y-vs*u.Y)/denominator,(vs*u.X-us*v.X)/denominator);
            var center=start+delta;var radius=Scalar.Sqrt(delta.X*delta.X+delta.Y*delta.Y);
            var from=start-center;var to=P(arc.End)-center;
            var angle=Scalar.Atan2(from.Y,from.X);var sweep=Scalar.Atan2(to.Y,to.X)-angle;
            // The middle point fixes the directed arc. Unwrapping adds a constant;
            // its analytic derivative remains valid through an atan2 display seam.
            if(cross.Value>0&&sweep.Value<=0)sweep+=Math.Tau;
            if(cross.Value<0&&sweep.Value>=0)sweep-=Math.Tau;
            Circular(center,radius,angle,sweep,out position,out first,out second);
        }
        else
        {
            if(requiredOrder==3)
            {
                var p=SketchFiniteParametricContact.EvaluateThirdDerivatives(sketch,curve,parameter.Value);
                position=new(parameter.Compose((p.Position.X-origin.X)/scale,p.First.X/scale),parameter.Compose((p.Position.Y-origin.Y)/scale,p.First.Y/scale));
                first=new(parameter.Compose(p.First.X/scale,p.Second.X/scale),parameter.Compose(p.First.Y/scale,p.Second.Y/scale));
                second=new(parameter.Compose(p.Second.X/scale,p.Third.X/scale),parameter.Compose(p.Second.Y/scale,p.Third.Y/scale));
            }
            else
            {
                var p=SketchFiniteParametricContact.EvaluateDerivatives(sketch,curve,parameter.Value,requiredOrder);
                position=new(parameter.Compose((p.Position.X-origin.X)/scale,p.First.X/scale),parameter.Compose((p.Position.Y-origin.Y)/scale,p.First.Y/scale));
                first=new(parameter.Compose(p.First.X/scale,p.Second.X/scale),parameter.Compose(p.First.Y/scale,p.Second.Y/scale));
                second=Constant(new(p.Second.X/scale,p.Second.Y/scale));
            }
        }
        var speed=Scalar.Sqrt(first.X*first.X+first.Y*first.Y);
        var unit=first/speed;var curvature=(first.X*second.Y-first.Y*second.X)/(speed*speed*speed);
        return new(position,unit,curvature,speed);

        void Circular(Vector center,Scalar radius,Scalar start,Scalar sweep,out Vector p,out Vector d1,out Vector d2)
        {
            var angle=start+sweep*parameter;var cosine=Scalar.Cos(angle);var sine=Scalar.Sin(angle);
            p=new(center.X+radius*cosine,center.Y+radius*sine);
            d1=new(-radius*sine*sweep,radius*cosine*sweep);
            d2=new(-radius*cosine*sweep*sweep,-radius*sine*sweep*sweep);
        }
    }
}
