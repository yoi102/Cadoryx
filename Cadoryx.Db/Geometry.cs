using System.Text.Json.Serialization;

namespace Cadoryx.Db;

public readonly record struct Vector3d(double X, double Y, double Z)
{
    public static Vector3d Zero => new(0, 0, 0);
    public static Vector3d UnitZ => new(0, 0, 1);
    [JsonIgnore] public double Length => Math.Sqrt(Dot(this));
    public void Validate() => CadGuard.Finite(X, Y, Z);
    public double Dot(Vector3d v) => X*v.X + Y*v.Y + Z*v.Z;
    public Vector3d Cross(Vector3d v) => new(Y*v.Z-Z*v.Y, Z*v.X-X*v.Z, X*v.Y-Y*v.X);
    public Vector3d Normalized() { Validate(); CadGuard.Positive(Length); return this / Length; }
    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static Vector3d operator *(Vector3d a, double b) => new(a.X*b,a.Y*b,a.Z*b);
    public static Vector3d operator /(Vector3d a, double b) => new(a.X/b,a.Y/b,a.Z/b);
}
public readonly record struct Quaterniond(double X, double Y, double Z, double W)
{
    public static Quaterniond Identity => new(0,0,0,1);
    public void Validate()
    {
        CadGuard.Finite(X,Y,Z,W);
        if (Math.Abs(X*X+Y*Y+Z*Z+W*W-1) > 1e-10) throw new CadValidationException("Rotation must be a unit quaternion.");
    }
    public static Quaterniond FromAxisAngle(Vector3d axis, double angle)
    {
        CadGuard.Finite(angle);
        var n = axis.Normalized(); var s = Math.Sin(angle/2);
        return new(n.X*s,n.Y*s,n.Z*s,Math.Cos(angle/2));
    }
    public Quaterniond Inverse() => new(-X,-Y,-Z,W);
    public Vector3d Rotate(Vector3d v)
    {
        var q = new Vector3d(X,Y,Z); var t = q.Cross(v)*2;
        return v + t*W + q.Cross(t);
    }
    public static Quaterniond operator *(Quaterniond a, Quaterniond b) => new(
        a.W*b.X+a.X*b.W+a.Y*b.Z-a.Z*b.Y, a.W*b.Y-a.X*b.Z+a.Y*b.W+a.Z*b.X,
        a.W*b.Z+a.X*b.Y-a.Y*b.X+a.Z*b.W, a.W*b.W-a.X*b.X-a.Y*b.Y-a.Z*b.Z);
}
public readonly record struct RigidTransform3d(Vector3d Translation, Quaterniond Rotation)
{
    public static RigidTransform3d Identity => new(Vector3d.Zero, Quaterniond.Identity);
    public static RigidTransform3d Translate(double x, double y, double z) => new(new(x,y,z),Quaterniond.Identity);
    public void Validate() { Translation.Validate(); Rotation.Validate(); }
    public Vector3d Apply(Vector3d point) => Rotation.Rotate(point) + Translation;
    public RigidTransform3d Inverse() { var r=Rotation.Inverse(); return new(r.Rotate(Translation)*-1,r); }
    public static RigidTransform3d operator *(RigidTransform3d parent, RigidTransform3d local) =>
        new(parent.Apply(local.Translation),parent.Rotation*local.Rotation);
}
public readonly record struct Bounds3d(Vector3d Min, Vector3d Max)
{
    public void Validate() { Min.Validate(); Max.Validate(); if(Min.X>Max.X || Min.Y>Max.Y || Min.Z>Max.Z) throw new CadValidationException("Inverted bounds."); }
}
public enum LengthUnit { Millimeter=0, Centimeter=1, Meter=2, Inch=3 }
public sealed record DocumentSettings(LengthUnit DisplayUnit = LengthUnit.Millimeter, int DecimalPlaces = 3, double LinearToleranceMm = 1e-7, double AngularToleranceRad = 1e-9)
{
    public void Validate()
    {
        if(!Enum.IsDefined(DisplayUnit) || DecimalPlaces is < 0 or > 12) throw new CadValidationException("Invalid document units.");
        CadGuard.Positive(LinearToleranceMm,AngularToleranceRad);
    }
    public static double MillimetersPerUnit(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeter=>1,LengthUnit.Centimeter=>10,LengthUnit.Meter=>1000,LengthUnit.Inch=>25.4,
        _=>throw new ArgumentOutOfRangeException(nameof(unit))
    };
}
