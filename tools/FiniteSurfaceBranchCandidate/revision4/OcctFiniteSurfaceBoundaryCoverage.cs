using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    // Subtract the union of the explicit original permitted face domains.
    // Summing intersections would count overlaps twice; a single Common with
    // a mixed compound has not proved these complete boundary domains.
    private static double FiniteSurfaceUncoveredBoundary(Shape face,
        IReadOnlyList<Shape> permitted,double budget,CancellationToken token)
    {
        using var snapshot=RepairSnapshot.Create(face);
        Shape remainder=snapshot.CopyShape();
        try
        {
            foreach(var domain in permitted)
            {
                token.ThrowIfCancellationRequested();
                using var domainSnapshot=RepairSnapshot.Create(domain);
                using var independentDomain=domainSnapshot.CopyShape();
                var next=remainder.Cut(independentDomain);
                remainder.Dispose();remainder=next;
                RequireClear(remainder,token);
                double area=FiniteSurfaceArea(remainder);
                CadGuard.Finite(area);
                if(area<=budget)return area;
            }
            token.ThrowIfCancellationRequested();
            double uncovered=FiniteSurfaceArea(remainder);
            CadGuard.Finite(uncovered);
            return uncovered;
        }
        finally{remainder.Dispose();}
    }
}
