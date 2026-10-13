#include <GeomFill_SweepSectionGenerator.hxx>
#include <Geom_Line.hxx>
#include <Geom_TrimmedCurve.hxx>
#include <gp_Trsf.hxx>
#include <cmath>
#include <iostream>
int main() {
 occ::handle<Geom_Curve> path=new Geom_TrimmedCurve(new Geom_Line(gp_Pnt(0,0,0),gp_Dir(0,0,1)),0,3);
 GeomFill_SweepSectionGenerator generator(path,.25);generator.Perform();
 if(generator.NbSections()<2)return 1;
 const double actual=generator.Transformation(generator.NbSections()-1).TranslationPart().Z();
 std::cout<<"Pinned AppSweep section defect reproduced: expected final translation Z=3; actual="<<actual<<"; sections="<<generator.NbSections()<<". No product implementation or migration credit.\n";
 return std::abs(actual)<1e-12 ? 0 : 2;
}
