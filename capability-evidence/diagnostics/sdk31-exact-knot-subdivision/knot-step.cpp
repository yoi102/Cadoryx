#include <windows.h>
#include <tlhelp32.h>
#include <BRepTools.hxx>
#include <BRep_Builder.hxx>
#include <BRep_Tool.hxx>
#include <BRepBuilderAPI_Copy.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <Geom_OffsetCurve.hxx>
#include <Geom_TrimmedCurve.hxx>
#include <Geom_BSplineCurve.hxx>
#include <Geom_BezierCurve.hxx>
#include <Geom_SurfaceOfLinearExtrusion.hxx>
#include <Geom_RectangularTrimmedSurface.hxx>
#include <Geom_OffsetSurface.hxx>
#include <GeomConvert.hxx>
#include <TopExp.hxx>
#include <TopoDS.hxx>
#include <NCollection_IndexedMap.hxx>
#include <NCollection_Array1.hxx>
#include <TopTools_ShapeMapHasher.hxx>
#include <STEPControl_Writer.hxx>
#include <Standard_Failure.hxx>
#include <OcctSharp.ExactStepOffsets.Generated.h>
#include <sstream>
#include <iostream>
#include <iomanip>
#include <unordered_set>
#include <vector>
#include <algorithm>
#include <cmath>

void Require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
std::string Bytes(const TopoDS_Shape& shape){std::ostringstream stream;BRepTools::Write(shape,stream);return stream.str();}
void Modules(){
 HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,GetCurrentProcessId());MODULEENTRY32W module{};module.dwSize=sizeof(module);Require(snapshot!=INVALID_HANDLE_VALUE,"Module snapshot required");
 if(Module32FirstW(snapshot,&module))do{std::wcout<<L"MODULE="<<module.szExePath<<L'\n';}while(Module32NextW(snapshot,&module));CloseHandle(snapshot);
}
struct Diagnostic {
 int segments,multiplicity,offsets=0,inserted=0;double maxPoint=0,maxDerivative=0;
 std::unordered_set<const Geom_Curve*> curves;std::unordered_set<const Geom_Surface*> surfaces;
 void Curve(const occ::handle<Geom_Curve>& curve){
  if(curve.IsNull()||!curves.insert(curve.get()).second)return;
  if(auto trim=occ::down_cast<Geom_TrimmedCurve>(curve);!trim.IsNull()){Curve(trim->BasisCurve());return;}
  auto offset=occ::down_cast<Geom_OffsetCurve>(curve);if(offset.IsNull())return;
  auto basis=offset->BasisCurve();Curve(basis);
  if(segments==0)return;
  if(occ::down_cast<Geom_BSplineCurve>(basis).IsNull()&&occ::down_cast<Geom_BezierCurve>(basis).IsNull())return;
  auto original=occ::down_cast<Geom_Curve>(offset->Copy());
  auto exact=GeomConvert::CurveToBSplineCurve(basis);exact=occ::down_cast<Geom_BSplineCurve>(exact->Copy());
  Require(!exact->IsPeriodic(),"This exact subdivision diagnostic has no periodic contract");
  const double first=exact->FirstParameter(),last=exact->LastParameter();
  Require(std::isfinite(first)&&std::isfinite(last)&&first<last,"Finite original basis parameter range required");
  Require(multiplicity>0&&multiplicity<=exact->Degree(),"Legal exact knot multiplicity required");
  const auto oldDegree=exact->Degree();const auto oldKnots=exact->NbKnots();
  NCollection_Array1<double> knots(1,segments-1);NCollection_Array1<int> mults(1,segments-1);
  for(int k=1;k<segments;++k){knots(k)=first+(last-first)*k/segments;mults(k)=multiplicity;}
  // Native knot insertion is an exact basis identity, not curve approximation.
  // Original curve range and all original knots remain; no tolerance is raised.
  exact->InsertKnots(knots,mults,0.0,false);
  Require(exact->FirstParameter()==first&&exact->LastParameter()==last&&exact->Degree()==oldDegree,"Exact basis range or degree changed");
  offset->SetBasisCurve(exact,false);
  for(int k=0;k<=512;++k){
   const double u=first+(last-first)*k/512;gp_Pnt p,q;gp_Vec a,b;
   original->D1(u,p,a);offset->D1(u,q,b);maxPoint=std::max(maxPoint,p.Distance(q));maxDerivative=std::max(maxDerivative,(a-b).Magnitude());
  }
  Require(maxPoint<1e-10&&maxDerivative<1e-10,"Exact subdivision identity diagnostics exceed1e-10");
  inserted+=exact->NbKnots()-oldKnots;++offsets;
 }
 void Surface(const occ::handle<Geom_Surface>& surface){
  if(surface.IsNull()||!surfaces.insert(surface.get()).second)return;
  if(auto extrusion=occ::down_cast<Geom_SurfaceOfLinearExtrusion>(surface);!extrusion.IsNull()){Curve(extrusion->BasisCurve());return;}
  if(auto trim=occ::down_cast<Geom_RectangularTrimmedSurface>(surface);!trim.IsNull()){Surface(trim->BasisSurface());return;}
  if(auto offset=occ::down_cast<Geom_OffsetSurface>(surface);!offset.IsNull())Surface(offset->BasisSurface());
 }
};
int main(int argc,char** argv){
 SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);SetPriorityClass(GetCurrentProcess(),BELOW_NORMAL_PRIORITY_CLASS);
 std::cout<<std::unitbuf<<std::setprecision(17);
 Modules();
 try{
  Require(argc==5,"Source BRep, output STEP, segment count and knot multiplicity required");
  Diagnostic diagnostic{};diagnostic.segments=std::stoi(argv[3]);diagnostic.multiplicity=std::stoi(argv[4]);
  Require(diagnostic.segments==0||diagnostic.segments==4||diagnostic.segments==8||diagnostic.segments==16,"Fixed diagnostic subdivision matrix required");
  BRep_Builder builder;TopoDS_Shape source;Require(BRepTools::Read(source,argv[1],builder),"Source BRep read failed");
  Require(BRepCheck_Analyzer(source,true,false,true).IsValid(),"Source strict BRep invalid");const auto before=Bytes(source);
  BRepBuilderAPI_Copy owned(source,true,false);Require(owned.IsDone(),"Private complete graph copy failed");auto copy=owned.Shape();
  NCollection_IndexedMap<TopoDS_Shape,TopTools_ShapeMapHasher> faces,edges;TopExp::MapShapes(copy,TopAbs_FACE,faces);TopExp::MapShapes(copy,TopAbs_EDGE,edges);
  std::vector<double> faceTolerance,edgeTolerance;
  for(int i=1;i<=faces.Extent();++i){auto face=TopoDS::Face(faces(i));faceTolerance.push_back(BRep_Tool::Tolerance(face));TopLoc_Location location;diagnostic.Surface(BRep_Tool::Surface(face,location));}
  for(int i=1;i<=edges.Extent();++i){auto edge=TopoDS::Edge(edges(i));edgeTolerance.push_back(BRep_Tool::Tolerance(edge));double first,last;TopLoc_Location location;diagnostic.Curve(BRep_Tool::Curve(edge,location,first,last));}
  Require(BRepCheck_Analyzer(copy,true,false,true).IsValid(),"Private exact knot copy strict BRep invalid");
  for(int i=1;i<=faces.Extent();++i)Require(BRep_Tool::Tolerance(TopoDS::Face(faces(i)))==faceTolerance[i-1],"Face tolerance changed");
  for(int i=1;i<=edges.Extent();++i)Require(BRep_Tool::Tolerance(TopoDS::Edge(edges(i)))==edgeTolerance[i-1],"Edge tolerance changed");
  if(diagnostic.segments)Require(diagnostic.offsets>0,"No eligible exact offset basis encountered");
  STEPControl_Writer writer;Require(OcctSharp_TransferExactStepOffsets(writer,copy),"Existing generated exact-offset writer route required");
  Require(writer.Write(argv[2])==IFSelect_RetDone,"Exact STEP write failed");Require(Bytes(source)==before,"Author BRep bytes changed");
  std::cout<<"EXACT_KNOT_OFFSETS="<<diagnostic.offsets<<" INSERTED_KNOTS="<<diagnostic.inserted<<" POINT_DELTA="<<diagnostic.maxPoint<<" DERIVATIVE_DELTA="<<diagnostic.maxDerivative<<" SOURCE_KEPT=1 FACES="<<faces.Extent()<<" EDGES="<<edges.Extent()<<'\n';
  HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,GetCurrentProcessId());MODULEENTRY32W module{};module.dwSize=sizeof(module);Require(snapshot!=INVALID_HANDLE_VALUE,"Module snapshot required");
  if(Module32FirstW(snapshot,&module))do{std::wcout<<L"MODULE="<<module.szExePath<<L'\n';}while(Module32NextW(snapshot,&module));CloseHandle(snapshot);return 0;
 }catch(const Standard_Failure& error){std::cout<<"FAIL="<<error.what()<<'\n';return 1;}
 catch(const std::exception& error){std::cout<<"FAIL="<<error.what()<<'\n';return 1;}
}
