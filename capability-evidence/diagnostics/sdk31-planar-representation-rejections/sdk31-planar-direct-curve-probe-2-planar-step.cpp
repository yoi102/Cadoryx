#include <windows.h>
#include <tlhelp32.h>
#include <BRepTools.hxx>
#include <BRep_Builder.hxx>
#include <BRep_Tool.hxx>
#include <BRepBuilderAPI_Copy.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <Geom_Plane.hxx>
#include <Geom_BSplineSurface.hxx>
#include <Geom2d_Curve.hxx>
#include <NCollection_Array1.hxx>
#include <NCollection_Array2.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopoDS.hxx>
#include <NCollection_IndexedMap.hxx>
#include <TopTools_ShapeMapHasher.hxx>
#include <STEPControl_Writer.hxx>
#include <StepData_StepModel.hxx>
#include <StepGeom_SurfaceCurve.hxx>
#include <StepShape_EdgeCurve.hxx>
#include <OcctSharp.ExactStepOffsets.Generated.h>
#include <fstream>
#include <sstream>
#include <iostream>
#include <iomanip>
#include <vector>
#include <cmath>
void Require(bool ok,const char* why){if(!ok)throw std::runtime_error(why);}
std::string Bytes(const TopoDS_Shape& shape){std::ostringstream out;BRepTools::Write(shape,out);return out.str();}
struct ParameterCurve {TopoDS_Edge edge;occ::handle<Geom2d_Curve> curve;double first,last,tolerance;};
int main(int argc,char** argv){
 SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);std::cout<<std::unitbuf<<std::setprecision(17);
 try{
  Require(argc==4,"Source BRep, output STEP and exact plane mode required");BRep_Builder builder;TopoDS_Shape source;
  Require(BRepTools::Read(source,argv[1],builder),"Source BRep read failed");Require(BRepCheck_Analyzer(source,true,false,true).IsValid(),"Source strict BRep invalid");const auto before=Bytes(source);
  BRepBuilderAPI_Copy owned(source,true,false);Require(owned.IsDone(),"Private exact copy failed");auto copy=owned.Shape();int converted=0;double maxPoint=0,maxDerivative=0;
  NCollection_IndexedMap<TopoDS_Shape,TopTools_ShapeMapHasher> faces;TopExp::MapShapes(copy,TopAbs_FACE,faces);
  if(std::string(argv[3])!="0")for(int i=1;i<=faces.Extent();++i){
   auto face=TopoDS::Face(faces(i));TopLoc_Location location;const auto surface=BRep_Tool::Surface(face,location);const auto plane=occ::down_cast<Geom_Plane>(surface);if(plane.IsNull())continue;
   double u0,u1,v0,v1;BRepTools::UVBounds(face,u0,u1,v0,v1);Require(std::isfinite(u0)&&std::isfinite(u1)&&std::isfinite(v0)&&std::isfinite(v1)&&u0<u1&&v0<v1,"Finite original UV face bounds required");
   // A degree-one tensor product represents P(u,v)=O+uX+vY exactly.
   // The original UV coordinates are used as knots; no parameter rescaling.
   NCollection_Array2<gp_Pnt> poles(1,2,1,2);poles(1,1)=plane->Value(u0,v0);poles(1,2)=plane->Value(u0,v1);poles(2,1)=plane->Value(u1,v0);poles(2,2)=plane->Value(u1,v1);
   NCollection_Array1<double> uknots(1,2),vknots(1,2);uknots(1)=u0;uknots(2)=u1;vknots(1)=v0;vknots(2)=v1;
   NCollection_Array1<int> multiplicities(1,2);multiplicities(1)=multiplicities(2)=2;
   occ::handle<Geom_BSplineSurface> exact=new Geom_BSplineSurface(poles,uknots,vknots,multiplicities,multiplicities,1,1,false,false);
   for(int a=0;a<=10;++a)for(int b=0;b<=10;++b){
    double u=u0+(u1-u0)*a/10.,v=v0+(v1-v0)*b/10.;gp_Pnt p,q;gp_Vec pu,pv,qu,qv;plane->D1(u,v,p,pu,pv);exact->D1(u,v,q,qu,qv);
    maxPoint=std::max(maxPoint,p.Distance(q));maxDerivative=std::max({maxDerivative,(pu-qu).Magnitude(),(pv-qv).Magnitude()});
   }
   std::vector<ParameterCurve> curves;
   for(TopExp_Explorer ex(face,TopAbs_EDGE);ex.More();ex.Next()){
    auto edge=TopoDS::Edge(ex.Current());double first,last;const auto pc=BRep_Tool::CurveOnSurface(edge,face,first,last);Require(!pc.IsNull(),"Original exact planar pcurve required");
    Require(!BRep_Tool::IsClosed(edge,face),"An explicit seam pair is required");curves.push_back({edge,pc,first,last,BRep_Tool::Tolerance(edge)});
   }
   const double tolerance=BRep_Tool::Tolerance(face);builder.UpdateFace(face,exact,location,tolerance);
   for(const auto& pc:curves){builder.UpdateEdge(pc.edge,pc.curve,face,pc.tolerance);builder.Range(pc.edge,face,pc.first,pc.last);}
   Require(BRep_Tool::Tolerance(face)==tolerance,"Face tolerance changed");++converted;
  }
  Require(BRepCheck_Analyzer(copy,true,false,true).IsValid(),"Exact planar private copy strict BRep invalid");
  STEPControl_Writer writer;Require(OcctSharp_TransferExactStepOffsets(writer,copy),"Expected exact-offset writer route");
  if(std::string(argv[3])=="2"){
   const auto model=writer.Model();int removed=0;
   for(int i=1;i<=model->NbEntities();++i){const auto edge=occ::down_cast<StepShape_EdgeCurve>(model->Value(i));if(edge.IsNull())continue;
    const auto curve=occ::down_cast<StepGeom_SurfaceCurve>(edge->EdgeGeometry());if(curve.IsNull())continue;
    Require(!curve->Curve3d().IsNull(),"Explicit 3D edge geometry required");edge->SetEdgeGeometry(curve->Curve3d());++removed;
   }
   std::cout<<"DIRECT_EDGE_CURVES="<<removed<<'\n';
  }
  Require(writer.Write(argv[2])==IFSelect_RetDone,"Exact STEP write failed");
  Require(Bytes(source)==before,"Author BRep changed");std::cout<<"PLANAR_CONVERTED="<<converted<<" EXACT_SURFACE_POINT_DELTA="<<maxPoint<<" DERIVATIVE_DELTA="<<maxDerivative<<" SOURCE_KEPT=1\n";
  HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,GetCurrentProcessId());MODULEENTRY32W m{};m.dwSize=sizeof(m);Require(snapshot!=INVALID_HANDLE_VALUE,"Module snapshot required");
  if(Module32FirstW(snapshot,&m))do{std::wcout<<L"MODULE="<<m.szExePath<<L'\n';}while(Module32NextW(snapshot,&m));CloseHandle(snapshot);return 0;
 }catch(const std::exception& e){std::cout<<"FAIL="<<e.what()<<'\n';return 1;}
}
