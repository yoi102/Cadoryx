#include <windows.h>
#include <tlhelp32.h>
#include <BRepTools.hxx>
#include <BRep_Builder.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>
#include <STEPControl_Reader.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRep_Tool.hxx>
#include <Geom_Curve.hxx>
#include <Geom2d_Curve.hxx>
#include <Geom_Surface.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopoDS.hxx>
#include <NCollection_IndexedMap.hxx>
#include <TopTools_ShapeMapHasher.hxx>
#include <Standard_Failure.hxx>
#include <sstream>
#include <iostream>
#include <iomanip>
#include <cmath>
#include <algorithm>

int Count(const TopoDS_Shape& shape,TopAbs_ShapeEnum kind){NCollection_IndexedMap<TopoDS_Shape,TopTools_ShapeMapHasher> map;if(!shape.IsNull())TopExp::MapShapes(shape,kind,map);return map.Extent();}
std::string Bytes(const TopoDS_Shape& shape){std::ostringstream stream;BRepTools::Write(shape,stream);return stream.str();}
void Modules(){
 HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,GetCurrentProcessId());MODULEENTRY32W module{};module.dwSize=sizeof(module);
 if(snapshot==INVALID_HANDLE_VALUE)throw std::runtime_error("Module snapshot missing");
 if(Module32FirstW(snapshot,&module))do{std::wcout<<L"MODULE="<<module.szExePath<<L'\n';}while(Module32NextW(snapshot,&module));CloseHandle(snapshot);
}
int main(int argc,char**argv){
 SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);SetPriorityClass(GetCurrentProcess(),BELOW_NORMAL_PRIORITY_CLASS);std::cout<<std::unitbuf<<std::setprecision(17);Modules();
 try{
  if(argc!=3)throw std::runtime_error("Original BRep and unchanged writer STEP required");
  BRep_Builder builder;TopoDS_Shape source;if(!BRepTools::Read(source,argv[1],builder))throw std::runtime_error("Original BRep read failed");const auto before=Bytes(source);
  STEPControl_Reader reader;if(reader.ReadFile(argv[2])!=IFSelect_RetDone||reader.TransferRoots()!=1)throw std::runtime_error("Original single-root STEP failed to transfer");
  const auto shape=reader.OneShape();int sourceSolids=Count(source,TopAbs_SOLID),sourceFaces=Count(source,TopAbs_FACE),sourceEdges=Count(source,TopAbs_EDGE),sourceVertices=Count(source,TopAbs_VERTEX);
  int solids=Count(shape,TopAbs_SOLID),faces=Count(shape,TopAbs_FACE),edges=Count(shape,TopAbs_EDGE),vertices=Count(shape,TopAbs_VERTEX);
  bool counts=!shape.IsNull()&&sourceSolids>0&&sourceFaces>0&&sourceEdges>0&&solids==sourceSolids&&faces==sourceFaces&&edges==sourceEdges&&vertices==sourceVertices;
  std::cout<<"COVERAGE SOURCE_SOLIDS="<<sourceSolids<<" SOURCE_FACES="<<sourceFaces<<" SOURCE_EDGES="<<sourceEdges<<" SOURCE_VERTICES="<<sourceVertices
   <<" SOLIDS="<<solids<<" FACES="<<faces<<" EDGES="<<edges<<" VERTICES="<<vertices<<" COMPLETE="<<counts<<'\n';
  if(!counts){std::cout<<"FAIL=Transferred STEP shape lacks the complete original solid/face/edge/vertex scope; an empty valid compound is not an equivalent model\n";return 1;}
  bool strict=BRepCheck_Analyzer(shape,true,false,true).IsValid();double maximum=0;int checked=0;
  for(TopExp_Explorer f(shape,TopAbs_FACE);f.More();f.Next()){
   auto face=TopoDS::Face(f.Current());auto surface=BRep_Tool::Surface(face);
   for(TopExp_Explorer e(face,TopAbs_EDGE);e.More();e.Next()){
    auto edge=TopoDS::Edge(e.Current());double first,last,a,b;auto curve=BRep_Tool::Curve(edge,first,last);auto pc=BRep_Tool::CurveOnSurface(edge,face,a,b);
    if(curve.IsNull()||pc.IsNull())throw std::runtime_error("Complete original edge curve and pcurve required");
    for(int j=0;j<=100;++j){double t=first+(last-first)*j/100.;auto uv=pc->Value(t);maximum=std::max(maximum,curve->Value(t).Distance(surface->Value(uv.X(),uv.Y())));}++checked;
   }
  }
  GProp_GProps originalMass,readMass;BRepGProp::VolumeProperties(source,originalMass,1e-11,false,false);BRepGProp::VolumeProperties(shape,readMass,1e-11,false,false);
  double volumeError=std::abs(originalMass.Mass()-readMass.Mass());
  std::cout<<"COMPLETE_CHECK EDGE_USES="<<checked<<" MAX_DELTA="<<maximum<<" STRICT_VALID="<<strict<<" SOURCE_VOLUME="<<originalMass.Mass()<<" VOLUME="<<readMass.Mass()<<" VOLUME_ERROR="<<volumeError<<'\n';
  if(Bytes(source)!=before)throw std::runtime_error("Author BRep bytes changed");
  return strict&&checked>0&&maximum<=1e-7&&volumeError<=1e-7?0:1;
 }catch(const Standard_Failure& error){std::cout<<"FAIL="<<error.what()<<'\n';return 1;}
 catch(const std::exception& error){std::cout<<"FAIL="<<error.what()<<'\n';return 1;}
}
