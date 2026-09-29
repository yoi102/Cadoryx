using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

public enum ExternalDependencyState
{ Current, Changed, Missing, IdentityConflict, LocalConflict, Cycle, Invalid }
public sealed record ExternalDependencyEntry(DocumentId Consumer,DefinitionId TargetPart,
    DocumentId Source,DefinitionId SourcePart,DocumentStateId FrozenState,string SourceSha256,
    string? ResolvedPath,ExternalDependencyState State,string Explanation);
public sealed record ExternalDependencyPreview(ImmutableArray<ExternalDependencyEntry> Entries,
    ImmutableArray<DocumentId> RefreshOrder,bool CanRefresh);

/// <summary>Read-only traversal of frozen external links. Opening a document never invokes it;
/// the graph is inspected explicitly before link or refresh.</summary>
public static class ExternalDependencyGraph
{
    public static async Task<ExternalDependencyPreview> PreviewAsync(DocumentSnapshot target,string? targetPath,
        IDocumentStorage storage,IAssetStore assets,CancellationToken token=default)
    {
        var entries=ImmutableArray.CreateBuilder<ExternalDependencyEntry>();
        var order=ImmutableArray.CreateBuilder<DocumentId>();
        var active=new HashSet<DocumentId>();var seen=new Dictionary<DocumentId,string>();
        await Visit(target,targetPath,0).ConfigureAwait(false);
        return new(entries.ToImmutable(),order.ToImmutable(),entries.All(e=>e.State is
            ExternalDependencyState.Current or ExternalDependencyState.Changed));

        async Task Visit(DocumentSnapshot document,string? path,int depth)
        {
            token.ThrowIfCancellationRequested();
            if(depth>32)throw new CadValidationException("External dependency depth exceeds 32.");
            if(!active.Add(document.Id))return;
            foreach(var link in document.ExternalParts.Values.OrderBy(l=>l.TargetPartId.Value))
            {
                token.ThrowIfCancellationRequested();
                if(entries.Count>=4096)throw new CadValidationException("External dependency preview exceeds 4096 links.");
                string? full=null;
                ExternalDependencyState state;string explanation;
                try { full=ExternalPartCommands.Resolve(link,path); }
                catch { /* A missing path keeps the frozen local bodies usable. */ }
                if(full is null||!File.Exists(full))
                {state=ExternalDependencyState.Missing;explanation="Source file is missing; frozen geometry remains available.";}
                else if(document.Definitions.GetValueOrDefault(link.TargetPartId) is not PartDefinition local||
                    ExternalPartLink.Fingerprint(document,local)!=link.LocalFingerprint)
                {state=ExternalDependencyState.LocalConflict;explanation="Frozen local part has changed.";}
                else
                {
                    string hash;
                    try { hash=ExternalPartCommands.Hash(full); }
                    catch(IOException) {hash="";}
                    if(hash.Length!=64)
                    {state=ExternalDependencyState.Invalid;explanation="Source bytes could not be read.";}
                    else
                    {
                        LoadedDocument? loaded=null;
                        try
                        {
                            loaded=await storage.LoadAsync(full,assets,token).ConfigureAwait(false);
                            if(ExternalPartCommands.Hash(full)!=hash)
                                throw new IOException("Source changed during dependency inspection.");
                            var source=loaded.Snapshot;
                            if(source.Id!=link.SourceDocumentId||
                               source.Definitions.GetValueOrDefault(link.SourcePartId) is not PartDefinition)
                            {state=ExternalDependencyState.IdentityConflict;explanation="Source document or part identity differs.";}
                            else if(active.Contains(source.Id))
                            {state=ExternalDependencyState.Cycle;explanation="External document dependency cycle.";}
                            else if(seen.TryGetValue(source.Id,out var previous)&&
                                !string.Equals(previous,hash,StringComparison.OrdinalIgnoreCase))
                            {state=ExternalDependencyState.IdentityConflict;explanation="One document ID resolves to different source bytes.";}
                            else
                            {
                                seen[source.Id]=hash;
                                state=hash==link.SourceSha256&&source.StateId==link.SourceStateId?
                                    ExternalDependencyState.Current:ExternalDependencyState.Changed;
                                explanation=state==ExternalDependencyState.Current?"Frozen source version is current.":
                                    "Source version changed; preview a refresh before committing.";
                                await Visit(source,full,depth+1).ConfigureAwait(false);
                            }
                        }
                        catch(IOException)
                        {state=ExternalDependencyState.Invalid;explanation="Source changed during inspection.";}
                        finally {loaded?.Dispose();}
                    }
                }
                entries.Add(new(document.Id,link.TargetPartId,link.SourceDocumentId,link.SourcePartId,
                    link.SourceStateId,link.SourceSha256,full,state,explanation));
            }
            active.Remove(document.Id);order.Add(document.Id);
        }
    }

    internal static async Task<ExternalDependencyPreview> RequireAcyclicAsync(DocumentSnapshot source,string sourcePath,
        IDocumentStorage storage,IAssetStore assets,DocumentId target,CancellationToken token)
    {
        if(source.Id==target)throw new CadValidationException("An external part cannot depend on its own document.");
        var preview=await PreviewAsync(source,sourcePath,storage,assets,token).ConfigureAwait(false);
        if(preview.Entries.Any(e=>e.Source==target||e.State==ExternalDependencyState.Cycle))
            throw new CadValidationException("External document dependency cycle; link or refresh was not committed.");
        if(preview.Entries.Any(e=>e.State is ExternalDependencyState.IdentityConflict or ExternalDependencyState.Invalid))
            throw new CadValidationException("External dependency identity is ambiguous or invalid.");
        if(preview.Entries.Any(e=>e.State!=ExternalDependencyState.Current))
            throw new CadValidationException("Refresh or repair lower-level source dependencies first.");
        return preview;
    }
}
