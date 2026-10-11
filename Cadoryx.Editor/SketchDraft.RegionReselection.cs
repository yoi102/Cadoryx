using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

public sealed partial class SketchDraft
{
    public void ReselectRegionSources(Guid group,ImmutableArray<ImmutableArray<SketchEntityId>> slots,IExactSketchExpressionRegions kernel)
    {
        regionEvaluation??=new(kernel);
        Change(s=>SketchRegionSourceReselection.Apply(s,group,slots,kernel),true);
    }
    public void ApplyPreparedRegionSources(SketchRegionSourceReselection.Prepared prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if(!ReferenceEquals(prepared.Original,Value))throw new CadValidationException("The sketch changed after source reselection was prepared.");
        // Only the opaque complete-replay preparation may skip a second native
        // operation. Managed edit/identity guards and final command replay remain.
        Accept(prepared.Candidate,regionEdit:true,refresh:false);
    }
}
