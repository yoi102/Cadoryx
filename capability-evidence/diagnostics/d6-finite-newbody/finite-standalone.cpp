#include <windows.h>
#include <tlhelp32.h>
#include <BRepFeat_MakePrism.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepGProp.hxx>
#include <BRepTools.hxx>
#include <BRep_Tool.hxx>
#include <BRep_Builder.hxx>
#include <BRepBuilderAPI_Copy.hxx>
#include <TopoDS_Compound.hxx>
#include <Geom_Surface.hxx>
#include <GProp_GProps.hxx>
#include <TopExp_Explorer.hxx>
#include <TopoDS.hxx>
#include <gp_Sphere.hxx>
#include <gp_Cylinder.hxx>
#include <gp_Pln.hxx>
#include <gp_Circ.hxx>
#include <iostream>
#include <iomanip>
#include <sstream>
#include <numbers>
#include <cmath>
#include <string>
#include <array>
#include <fstream>
void Require(bool value,const char* why){if(!value)throw std::runtime_error(why);}
double Volume(const TopoDS_Shape& shape){GProp_GProps p;BRepGProp::VolumeProperties(shape,p,1e-11,true,false);return p.Mass();}
double Area(const TopoDS_Shape& shape){GProp_GProps p;BRepGProp::SurfaceProperties(shape,p,false,false);return p.Mass();}
std::string Bytes(const TopoDS_Shape& shape){std::ostringstream out;BRepTools::Write(shape,out);return out.str();}
int main(int argc,char** argv){
 SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);std::cout<<std::unitbuf<<std::setprecision(17);
 try {
  Require(argc==4,"kind plane/sphere/cylinder/small, mode 1/2, seed 0/1 required");
  std::string kind=argv[1];int mode=std::stoi(argv[2]);bool synthetic=std::stoi(argv[3])!=0;const double pi=std::numbers::pi;
  TopoDS_Face profile;
  if(kind=="sphere") {BRepBuilderAPI_MakeEdge edge(gp_Circ(gp_Ax2(gp_Pnt(5,5,5),gp_Dir(0,0,1)),2));profile=BRepBuilderAPI_MakeFace(BRepBuilderAPI_MakeWire(edge.Edge()).Wire()).Face();}
  else {BRepBuilderAPI_MakePolygon polygon;for(auto p:{gp_Pnt(3,3,5),gp_Pnt(7,3,5),gp_Pnt(7,7,5),gp_Pnt(3,7,5)})polygon.Add(p);polygon.Close();profile=BRepBuilderAPI_MakeFace(polygon.Wire()).Face();}
  TopoDS_Face stop;
  if(kind=="sphere")stop=BRepBuilderAPI_MakeFace(gp_Sphere(gp_Ax3(gp_Pnt(5,5,18),gp_Dir(0,0,1)),6),0,2*pi,-pi/2,0).Face();
  else if(kind=="cylinder")stop=BRepBuilderAPI_MakeFace(gp_Cylinder(gp_Ax3(gp_Pnt(5,0,18),gp_Dir(0,1,0),gp_Dir(1,0,0)),6),0,pi,0,10).Face();
  else {double extent=kind=="small"?.5:5;stop=BRepBuilderAPI_MakeFace(gp_Pln(gp_Ax3(gp_Pnt(5,5,12),gp_Dir(0,0,1))),-extent,extent,-extent,extent).Face();}
  TopoDS_Shape basis;TopoDS_Face support;
  if(synthetic) {BRepPrimAPI_MakePrism seed(profile,gp_Vec(0,0,-1),false,true);Require(seed.IsDone(),"Exact rear seed failed");basis=seed.Shape();support=profile;}
  else {basis=BRepPrimAPI_MakeBox(10,10,5).Shape();for(TopExp_Explorer ex(basis,TopAbs_FACE);ex.More();ex.Next()){auto face=TopoDS::Face(ex.Current());double u0,u1,v0,v1;BRepTools::UVBounds(face,u0,u1,v0,v1);auto surface=BRep_Tool::Surface(face);if(surface->Value((u0+u1)/2,(v0+v1)/2).Z()>4.999)support=face;}Require(!support.IsNull(),"Exact support unavailable");}
  const auto before=Bytes(basis),stopBefore=Bytes(stop);
  // Same ownership contract as LimitedFeaturePlan: copy the entire graph in
  // one operation, retaining support membership, before the mutable builder.
  BRep_Builder graphBuilder;TopoDS_Compound graph;graphBuilder.MakeCompound(graph);
  std::array<TopoDS_Shape,4> arguments{basis,profile,support,stop};
  std::array<bool,4> freeFlags{};for(size_t i=0;i<arguments.size();++i)freeFlags[i]=arguments[i].Free();
  for(const auto& shape:arguments)graphBuilder.Add(graph,shape);
  // Aggregate assembly marks each argument TShape non-Free. Restore those
  // bookkeeping flags before copying; no geometry or topology is replaced.
  for(size_t i=0;i<arguments.size();++i)arguments[i].Free(freeFlags[i]);
  Require(Bytes(basis)==before&&Bytes(stop)==stopBefore,"Graph assembly altered more than restored Free flags");
  BRepBuilderAPI_Copy owned(graph,true,false);Require(owned.IsDone(),"Complete prerequisite graph copy failed");
  auto privateBasis=owned.ModifiedShape(basis);auto privateProfile=TopoDS::Face(owned.ModifiedShape(profile));
  auto privateSupport=TopoDS::Face(owned.ModifiedShape(support));auto privateStop=TopoDS::Face(owned.ModifiedShape(stop));
  BRepFeat_MakePrism feature(privateBasis,privateProfile,privateSupport,gp_Dir(0,0,1),mode,true);feature.Perform(privateStop);
  Require(feature.IsDone(),"Native finite prism not done");auto result=feature.Shape();Require(BRepCheck_Analyzer(result,true,false,true).IsValid(),"Native finite result strict invalid");
  TopoDS_Shape material=result;
  if(mode==1){BRepAlgoAPI_Cut cut(result,privateBasis);Require(cut.IsDone(),"Exact material extraction failed");material=cut.Shape();}
  Require(BRepCheck_Analyzer(material,true,false,true).IsValid(),"Extracted material strict invalid");
  double expected=kind=="sphere"?pi*4*13-2*pi/3*(216-std::pow(32,1.5)):kind=="cylinder"?4*(52-(2*std::sqrt(32)+36*std::asin(1./3))):112;
  double cap=0,outsideCap=0;int caps=0;
  for(TopExp_Explorer ex(material,TopAbs_FACE);ex.More();ex.Next()){auto face=TopoDS::Face(ex.Current());BRepAlgoAPI_Common common(face,privateStop);Require(common.IsDone(),"Exact finite cap comparison failed");double covered=Area(common.Shape());if(covered>1e-7){cap+=covered;outsideCap+=std::abs(Area(face)-covered);++caps;}}
  std::ofstream("before-basis.brep")<<before;std::ofstream("after-basis.brep")<<Bytes(basis);
  std::ofstream("before-stop.brep")<<stopBefore;std::ofstream("after-stop.brep")<<Bytes(stop);
  Require(Bytes(basis)==before&&Bytes(stop)==stopBefore,"Native altered prerequisite source representation");
  std::cout<<"KIND="<<kind<<" MODE="<<mode<<" SYNTHETIC="<<synthetic<<" MATERIAL="<<Volume(material)<<" EXPECTED="<<expected<<" ERROR="<<std::abs(Volume(material)-expected)<<" CAP="<<cap<<" CAP_OUTSIDE="<<outsideCap<<" CAPS="<<caps<<" SOURCE_KEPT=1\n";
  HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,GetCurrentProcessId());MODULEENTRY32W m{};m.dwSize=sizeof(m);Require(snapshot!=INVALID_HANDLE_VALUE,"Modules unavailable");if(Module32FirstW(snapshot,&m))do{std::wcout<<L"MODULE="<<m.szExePath<<L'\n';}while(Module32NextW(snapshot,&m));CloseHandle(snapshot);
  if(kind=="small"){Require(cap<1e-7||outsideCap>1e-7,"Small plane unexpectedly completely covered");return 0;}
  Require(std::abs(Volume(material)-expected)<=1e-7&&cap>1e-7&&outsideCap<=1e-7,"Complete exact native material/finite cap mismatch");return 0;
 }catch(const Standard_Failure& e){std::cout<<"FAIL="<<e.what()<<'\n';return 1;}catch(const std::exception& e){std::cout<<"FAIL="<<e.what()<<'\n';return 1;}
}
