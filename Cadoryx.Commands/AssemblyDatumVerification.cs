using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

internal static class AssemblyDatumVerification
{
    public static async Task VerifyAsync(DocumentCommandContext context,
        IEnumerable<AssemblyDatumReference> datums,CancellationToken token)
    {
        var distinct=datums.Distinct().ToArray();
        if(distinct.Length==0)return;
        if(context.Kernel is not IAssemblyDatumResolver resolver)
            throw new CadValidationException("The geometry kernel cannot verify assembly BRep datums.");
        foreach(var stored in distinct)
        {
            token.ThrowIfCancellationRequested();
            stored.Validate(context.Snapshot.Id);
            var fresh=await resolver.ResolveAssemblyDatumAsync(context.Snapshot,stored.Path,stored.BodyId,
                stored.FullTopologyIndex,stored.Fingerprint,context.Assets,token).ConfigureAwait(false);
            if(stored.DefinitionId!=fresh.DefinitionId||stored.FeatureId!=fresh.FeatureId||
               stored.Revision!=fresh.Revision||stored.Asset!=fresh.Asset||stored.Geometry!=fresh.Geometry||
               (stored.LocalPoint-fresh.LocalPoint).Length>1e-6||
               stored.LocalAxis.Dot(fresh.LocalAxis)<1-1e-8||
               Math.Abs(stored.RadiusMm-fresh.RadiusMm)>1e-6)
                throw new CadValidationException("Stored assembly datum differs from its original BRep; reselect it.");
        }
    }
}
