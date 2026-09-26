using System.Security.Cryptography;
using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

public enum ExternalPartStatus { Current, SourceMissing, SourceChanged, LocalChanged, SourceConflict }

/// <summary>External parts are frozen body snapshots. Source files are read only on explicit link/check/refresh.</summary>
public static class ExternalPartCommands
{
    public static LinkExternalPartCommand Link(IDocumentStorage storage,string sourcePath,OccurrencePath parent,
        DefinitionId targetPartId,ComponentSlotId slotId,string name,DefinitionId? sourcePartId=null,
        string? targetDocumentPath=null)=>new(storage,sourcePath,parent,targetPartId,slotId,name,sourcePartId,targetDocumentPath);

    public static RefreshExternalPartCommand Refresh(IDocumentStorage storage,DefinitionId targetPartId,
        string? targetDocumentPath=null)=>new(storage,targetPartId,targetDocumentPath);

    public static ICadDocumentCommand Detach(DefinitionId partId)=>new EditDocumentCommand("Detach external part",document=>
        document.ExternalParts.ContainsKey(partId)?document with{ExternalParts=document.ExternalParts.Remove(partId)}:
            throw new CadValidationException("Part is not externally linked."));

    public static ExternalPartStatus Check(DocumentSnapshot document,DefinitionId id,string? targetDocumentPath=null)
    {
        var link=document.ExternalParts.TryGetValue(id,out var value)?value:
            throw new CadValidationException("Part is not externally linked.");
        if(document.Definitions[id] is not PartDefinition part||
           ExternalPartLink.Fingerprint(document,part)!=link.LocalFingerprint)return ExternalPartStatus.LocalChanged;
        string source;
        try{source=Resolve(link,targetDocumentPath);}catch{return ExternalPartStatus.SourceMissing;}
        if(!File.Exists(source))return ExternalPartStatus.SourceMissing;
        try{return Hash(source)==link.SourceSha256?ExternalPartStatus.Current:ExternalPartStatus.SourceChanged;}
        catch(IOException){return ExternalPartStatus.SourceConflict;}
    }

    private static string Resolve(ExternalPartLink link,string? targetDocumentPath)
    {
        if(Path.IsPathRooted(link.SourcePath))return Path.GetFullPath(link.SourcePath);
        if(targetDocumentPath is not null)
        {
            var relative=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(targetDocumentPath))!,link.SourcePath));
            if(File.Exists(relative))return relative;
        }
        return link.AbsolutePathHint is {} hint?Path.GetFullPath(hint):
            throw new FileNotFoundException("External source path cannot be resolved.");
    }

    private static string Hash(string path)
    {
        using var stream=File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static string SourceHint(string source,string? destination)=>destination is null?source:
        Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(destination))!,source);

    private static PartDefinition SelectPart(DocumentSnapshot source,DefinitionId? id)
    {
        var parts=source.Definitions.Values.OfType<PartDefinition>().ToArray();
        var part=id is {} selected?parts.SingleOrDefault(p=>p.Id==selected):parts.Length==1?parts[0]:null;
        if(part is null||part.Bodies.IsEmpty)
            throw new CadValidationException("Select one nonempty source part explicitly.");
        return part;
    }

    private static (PartDefinition Part,ImmutableDictionary<BodyId,CadBody> Bodies) Freeze(
        DocumentSnapshot target,DocumentSnapshot source,PartDefinition sourcePart,DefinitionId targetId,string name)
    {
        var defaultLayer=target.Layers.Keys.OrderBy(id=>id.Value).First();
        var bodies=ImmutableDictionary<BodyId,CadBody>.Empty;
        foreach(var id in sourcePart.Bodies)
        {
            var old=source.Bodies[id];var nextId=BodyId.New();
            bodies=bodies.Add(nextId,old with{Id=nextId,PartId=targetId,Producer=null,
                LayerId=defaultLayer,MaterialId=null,Appearance=old.Appearance with{ByLayer=false}});
        }
        return (new(targetId,name,bodies.Keys.OrderBy(id=>id.Value).ToImmutableArray(),[]),bodies);
    }

    private static ExternalPartLink LinkFor(DocumentSnapshot target,PartDefinition part,DocumentSnapshot source,
        PartDefinition sourcePart,string sourcePath,string sourceHash,string? targetPath)=>new(
            part.Id,source.Id,sourcePart.Id,source.StateId,sourceHash,SourceHint(sourcePath,targetPath),
            sourcePath,ExternalPartLink.Fingerprint(target,part));

    public sealed class LinkExternalPartCommand(IDocumentStorage storage,string sourcePath,OccurrencePath parent,
        DefinitionId targetPartId,ComponentSlotId slotId,string name,DefinitionId? sourcePartId,
        string? targetDocumentPath) : ICadDocumentCommand
    {
        public string Name=>"Link external part";
        public DefinitionId ResultPartId=>targetPartId;
        public OccurrencePath ResultPath=>parent.Append(slotId);
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
        {
            CadGuard.Id(targetPartId);CadGuard.Id(slotId);CadGuard.Name(name);
            var target=context.Snapshot;
            if(parent.DocumentId!=target.Id||target.Definitions.ContainsKey(targetPartId)||
               target.Definitions.Values.OfType<AssemblyDefinition>().Any(a=>a.Children.Any(s=>s.Id==slotId))||
               target.AssemblyConstraints.Values.Any(c=>c.PrimaryPath.Slots.Contains(slotId)||c.SecondaryPath?.Slots.Contains(slotId)==true))
                throw new CadValidationException("External part target or slot identity is invalid.");
            AssemblyDefinition owner;
            if(parent.Slots.IsEmpty)owner=(AssemblyDefinition)target.Definitions[target.RootAssemblyId];
            else
            {
                var resolved=OccurrencePlacement.Resolve(target,parent);
                owner=target.Definitions[resolved.Slot.DefinitionId] as AssemblyDefinition??
                    throw new CadValidationException("Choose an assembly as the parent.");
                if(target.EnumerateOccurrences().Count(o=>o.DefinitionId==owner.Id)>1)
                    throw new CadValidationException("Make the shared parent assembly independent first.");
            }
            string full=Path.GetFullPath(sourcePath);
            string hash=Hash(full);
            LoadedDocument? loaded=null;
            try
            {
                loaded=await storage.LoadAsync(full,context.Assets,cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if(Hash(full)!=hash)throw new IOException("External source changed while loading.");
                if(loaded.Snapshot.Id==target.Id)throw new CadValidationException("A document cannot link itself.");
                var sourcePart=SelectPart(loaded.Snapshot,sourcePartId);
                var (part,bodies)=Freeze(target,loaded.Snapshot,sourcePart,targetPartId,name);
                var candidate=target with{Definitions=target.Definitions.Add(targetPartId,part).SetItem(owner.Id,
                    owner with{Children=owner.Children.Add(new(slotId,targetPartId,name,RigidTransform3d.Identity))}),
                    Bodies=target.Bodies.AddRange(bodies)};
                candidate=candidate with{ExternalParts=candidate.ExternalParts.Add(part.Id,
                    LinkFor(candidate,part,loaded.Snapshot,sourcePart,full,hash,targetDocumentPath))};
                var result=new PreparedDocumentEdit(candidate.WithNewState(),[loaded]);loaded=null;return result;
            }
            finally{loaded?.Dispose();}
        }
    }

    public sealed class RefreshExternalPartCommand(IDocumentStorage storage,DefinitionId targetPartId,
        string? targetDocumentPath) : ICadDocumentCommand
    {
        public string Name=>"Refresh external part";
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
        {
            var target=context.Snapshot;
            if(!target.ExternalParts.TryGetValue(targetPartId,out var link)||
               target.Definitions[targetPartId] is not PartDefinition current)
                throw new CadValidationException("Part is not externally linked.");
            if(ExternalPartLink.Fingerprint(target,current)!=link.LocalFingerprint)
                throw new CadValidationException("Local part changed; detach or restore it before refreshing.");
            var full=Resolve(link,targetDocumentPath);
            if(!File.Exists(full))throw new FileNotFoundException("External part source is missing.",full);
            var hash=Hash(full);
            if(hash==link.SourceSha256)return new PreparedDocumentEdit(target);
            LoadedDocument? loaded=null;
            try
            {
                loaded=await storage.LoadAsync(full,context.Assets,cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if(Hash(full)!=hash)throw new IOException("External source changed while loading.");
                if(loaded.Snapshot.Id!=link.SourceDocumentId||
                   !loaded.Snapshot.Definitions.TryGetValue(link.SourcePartId,out var definition)||
                   definition is not PartDefinition sourcePart)
                    throw new CadValidationException("External source document or part identity changed.");
                if(loaded.Snapshot.StateId==link.SourceStateId)
                    throw new CadValidationException("Source bytes changed without a new document state.");
                var (part,bodies)=Freeze(target,loaded.Snapshot,sourcePart,current.Id,current.Name);
                var candidate=target with{Definitions=target.Definitions.SetItem(current.Id,part),
                    Bodies=target.Bodies.RemoveRange(current.Bodies).AddRange(bodies)};
                candidate=candidate with{ExternalParts=candidate.ExternalParts.SetItem(part.Id,
                    LinkFor(candidate,part,loaded.Snapshot,sourcePart,full,hash,targetDocumentPath))};
                var result=new PreparedDocumentEdit(candidate.WithNewState(),[loaded]);loaded=null;return result;
            }
            finally{loaded?.Dispose();}
        }
    }
}
