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
public sealed record DocumentGridSettings(bool Visible = true, double SpacingMm = 10, bool Snap = false)
{
    public void Validate()
    {
        if (!double.IsFinite(SpacingMm) || SpacingMm is < 0.1 or > 1000)
            throw new CadValidationException("Grid spacing must be between 0.1 and 1000 mm.");
    }
}
public enum DocumentWorkPlaneKind { XY = 0, XZ = 1, YZ = 2 }
public sealed record DocumentWorkPlaneSettings(DocumentWorkPlaneKind Kind = DocumentWorkPlaneKind.XY, double OffsetMm = 0)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !double.IsFinite(OffsetMm) || Math.Abs(OffsetMm) > 1_000_000)
            throw new CadValidationException("Work plane or offset is invalid (±1,000,000 mm).");
    }
    public Quaterniond Rotation => Kind switch
    {
        DocumentWorkPlaneKind.XY => Quaterniond.Identity,
        DocumentWorkPlaneKind.XZ => Quaterniond.FromAxisAngle(new(1,0,0),-Math.PI/2),
        DocumentWorkPlaneKind.YZ => new(0.5,0.5,0.5,0.5),
        _ => throw new CadValidationException("Invalid work plane.")
    };
    public Vector3d Normal => Rotation.Rotate(Vector3d.UnitZ);
    public Vector3d Origin => Normal*OffsetMm;
    public Vector3d ToWorld(double u,double v) => Origin+Rotation.Rotate(new(u,v,0));
    public Vector3d ToLocal(Vector3d world) => Rotation.Inverse().Rotate(world-Origin);
}
public enum DocumentOriginStyle { ColorAxes = 0, SubtleAxes = 1, OriginMarker = 2 }
public sealed record DocumentOriginSettings(bool Visible = true, DocumentOriginStyle Style = DocumentOriginStyle.ColorAxes, double SizeMm = 20)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Style) || !double.IsFinite(SizeMm) || SizeMm is < 1 or > 1000)
            throw new CadValidationException("Origin axes style or size is invalid (1–1000 mm).");
    }
}
public sealed record DocumentSettings(LengthUnit DisplayUnit = LengthUnit.Millimeter, int DecimalPlaces = 3, double LinearToleranceMm = 1e-7, double AngularToleranceRad = 1e-9)
{
    public const uint DefaultBackgroundTopArgb = 0xFF578EC5;
    public const uint DefaultBackgroundBottomArgb = 0xFFDCECF8;
    public DocumentGridSettings Grid { get; init; } = new();
    public DocumentWorkPlaneSettings WorkPlane { get; init; } = new();
    public DocumentOriginSettings Origin { get; init; } = new();
    public uint BackgroundTopArgb { get; init; } = DefaultBackgroundTopArgb;
    public uint BackgroundBottomArgb { get; init; } = DefaultBackgroundBottomArgb;
    public void Validate()
    {
        if(!Enum.IsDefined(DisplayUnit) || DecimalPlaces is < 0 or > 12) throw new CadValidationException("Invalid document units.");
        CadGuard.Positive(LinearToleranceMm,AngularToleranceRad);
        (Grid ?? throw new CadValidationException("Missing document grid settings.")).Validate();
        (WorkPlane ?? throw new CadValidationException("Missing document work plane.")).Validate();
        (Origin ?? throw new CadValidationException("Missing document origin settings.")).Validate();
        if ((BackgroundTopArgb & 0xFF000000u) != 0xFF000000u ||
            (BackgroundBottomArgb & 0xFF000000u) != 0xFF000000u)
            throw new CadValidationException("Document background colors must be opaque.");
    }
    public static double MillimetersPerUnit(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeter=>1,LengthUnit.Centimeter=>10,LengthUnit.Meter=>1000,LengthUnit.Inch=>25.4,
        _=>throw new ArgumentOutOfRangeException(nameof(unit))
    };
}
