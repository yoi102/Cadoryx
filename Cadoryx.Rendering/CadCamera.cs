using Cadoryx.Db;
namespace Cadoryx.Rendering;

public sealed record CadCamera(Vector3d Eye,Vector3d Target,Vector3d Up,double Aspect,double Scale,double FieldOfViewY,double NearPlane,double FarPlane,bool Perspective,bool AutoFitDepth);
