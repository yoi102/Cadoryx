using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

public interface ISessionDispatcher { Task InvokeAsync(Action action); }
/// <summary>For headless applications; WPF must provide its Dispatcher implementation.</summary>
public sealed class InlineSessionDispatcher : ISessionDispatcher
{
    public Task InvokeAsync(Action action){action();return Task.CompletedTask;}
}
public sealed class StaleDocumentException() : InvalidOperationException("The document changed while this operation was being prepared.");

public sealed class DocumentCapture(DocumentSnapshot snapshot,DocumentAssetLease lease,bool isDirty=false,string? filePath=null) : IDisposable
{
    public DocumentSnapshot Snapshot { get; }=snapshot;
    public bool IsDirty {get;}=isDirty;
    public string? FilePath {get;}=filePath;
    public void Dispose()=>lease.Dispose();
}
public sealed class CadDocumentSession : IAsyncDisposable
{
    private readonly object gate=new();
    private readonly IGeometryKernel kernel;
    private readonly ISessionDispatcher dispatcher;
    private readonly CancellationTokenSource lifetime=new();
    private readonly SemaphoreSlim saveQueue=new(1,1);
    private readonly List<HistoryEntry> undo=[];
    private readonly List<HistoryEntry> redo=[];
    private DocumentSnapshot snapshot;
    private DocumentAssetLease currentLease;
    private DocumentStateId? savedState;
    private long generation;
    private bool closing;
    private int operations;
    private TaskCompletionSource drained=CompletedSource();
    private Task? disposeTask;
    public IAssetStore Assets { get; }
    public Guid SessionId {get;}=Guid.NewGuid();
    public string? RecoveryOriginPath {get;internal set;}
    public int HistoryLimit { get; set; }=50;
    public event EventHandler<DocumentChangeSet>? Changed;
    public event EventHandler? StatusChanged;
    public event EventHandler<Exception>? ObserverFailed;
    public string? FilePath { get; private set; }
    public DocumentSnapshot Snapshot {get{lock(gate)return snapshot;}}
    public long Generation {get{lock(gate)return generation;}}
    public bool IsDirty {get{lock(gate)return snapshot.StateId!=savedState;}}
    public bool CanUndo {get{lock(gate)return !closing&&undo.Count>0;}}
    public bool CanRedo {get{lock(gate)return !closing&&redo.Count>0;}}
    public bool IsClosing {get{lock(gate)return closing;}}
    public bool IsBusy {get{lock(gate)return operations>0;}}

    public CadDocumentSession(DocumentSnapshot initial,IAssetStore assets,IGeometryKernel kernel,ISessionDispatcher dispatcher,string? path=null)
    {
        initial.Validate();Assets=assets;this.kernel=kernel;this.dispatcher=dispatcher;snapshot=initial;
        currentLease=new(initial,assets);FilePath=path;savedState=path is null?null:initial.StateId;
    }
    public DocumentCapture Capture()
    {
        lock(gate){ThrowIfClosing();return new(snapshot,new(snapshot,Assets),snapshot.StateId!=savedState,FilePath);}
    }
    public async Task ExecuteAsync(ICadDocumentCommand command,CancellationToken cancellationToken=default)
    {
        DocumentCommandContext context;DocumentAssetLease inputLease;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
        lock(gate)
        {
            ThrowIfClosing();if(!snapshot.Extensions.IsDefaultOrEmpty)throw new NotSupportedException("This document contains unsupported extension data and is read-only.");inputLease=new(snapshot,Assets);
            context=new(snapshot,generation,Assets,kernel);StartOperation();
        }
        try
        {
            using(inputLease)
            using(var prepared=await command.PrepareAsync(context,linked.Token).ConfigureAwait(false))
            {
                linked.Token.ThrowIfCancellationRequested();prepared.Snapshot.Validate();
                await dispatcher.InvokeAsync(()=>
                {
                    DocumentChangeSet? change=null;
                    lock(gate)
                    {
                        linked.Token.ThrowIfCancellationRequested();ThrowIfClosing();
                        if(context.Generation!=generation||context.Snapshot.Id!=snapshot.Id)throw new StaleDocumentException();
                        var next=prepared.Snapshot;
                        if(ReferenceEquals(next,snapshot)||next.StateId==snapshot.StateId)return;
                        if(next.Id!=snapshot.Id)throw new CadValidationException("A command cannot replace document identity.");
                        using var candidate=new CommitResources(snapshot,next,Assets);
                        var entry=candidate.CreateEntry(command.Name);
                        var nextLease=candidate.TakeCurrentLease();
                        var previous=snapshot;snapshot=next;generation++;
                        currentLease.Dispose();currentLease=nextLease;
                        foreach(var old in redo)old.Dispose();redo.Clear();
                        undo.Add(entry);while(undo.Count>Math.Max(1,HistoryLimit)){undo[0].Dispose();undo.RemoveAt(0);}
                        change=DocumentChangeSet.Between(previous,next,generation);
                    }
                    if(change is not null)Publish(change);
                }).ConfigureAwait(false);
            }
        }
        finally{lock(gate)EndOperation();await dispatcher.InvokeAsync(()=>Notify(StatusChanged));}
    }
    public Task UndoAsync()=>MoveHistoryAsync(undo,redo,false);
    public Task RedoAsync()=>MoveHistoryAsync(redo,undo,true);
    private Task MoveHistoryAsync(List<HistoryEntry> from,List<HistoryEntry> to,bool forward)=>dispatcher.InvokeAsync(()=>
    {
        DocumentChangeSet? change=null;
        lock(gate)
        {
            ThrowIfClosing();if(from.Count==0)return;
            var entry=from[^1];var next=forward?entry.After:entry.Before;
            var lease=new DocumentAssetLease(next,Assets);var previous=snapshot;
            from.RemoveAt(from.Count-1);to.Add(entry);snapshot=next;generation++;
            currentLease.Dispose();currentLease=lease;
            change=DocumentChangeSet.Between(previous,next,generation);
        }
        Publish(change);
    });
    public async Task SaveAsync(IDocumentStorage storage,string path,CancellationToken cancellationToken=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
        lock(gate){ThrowIfClosing();StartOperation();}
        bool entered=false;
        try
        {
            await saveQueue.WaitAsync(linked.Token).ConfigureAwait(false);entered=true;
            using var capture=Capture();
            await storage.SaveAsync(capture.Snapshot,Assets,path,linked.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(()=>
            {
                lock(gate){if(closing)return;FilePath=Path.GetFullPath(path);savedState=capture.Snapshot.StateId;}
                Notify(StatusChanged);
            }).ConfigureAwait(false);
        }
        finally {if(entered)saveQueue.Release();lock(gate)EndOperation();}
    }
    public ValueTask DisposeAsync()
    {
        lock(gate)
        {
            if(disposeTask is not null)return new(disposeTask);
            closing=true;generation++;lifetime.Cancel();
            return new(disposeTask=DisposeCoreAsync(drained.Task));
        }
    }
    private async Task DisposeCoreAsync(Task pending)
    {
        await pending.ConfigureAwait(false);
        await dispatcher.InvokeAsync(()=>
        {
            lock(gate){currentLease.Dispose();foreach(var e in undo)e.Dispose();foreach(var e in redo)e.Dispose();undo.Clear();redo.Clear();}
            Changed=null;StatusChanged=null;
        }).ConfigureAwait(false);
        lifetime.Dispose();saveQueue.Dispose();
    }
    private void StartOperation(){if(operations++==0)drained=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    private void EndOperation(){if(--operations==0)drained.TrySetResult();}
    private static TaskCompletionSource CompletedSource(){var s=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);s.SetResult();return s;}
    private void ThrowIfClosing()=>ObjectDisposedException.ThrowIf(closing,this);
    private void Publish(DocumentChangeSet change)
    {
        foreach(var observer in Changed?.GetInvocationList()??[])
            try{((EventHandler<DocumentChangeSet>)observer)(this,change);}catch(Exception ex){ReportObserver(ex);}
        Notify(StatusChanged);
    }
    private void Notify(EventHandler? handlers)
    {
        foreach(var observer in handlers?.GetInvocationList()??[])
            try{((EventHandler)observer)(this,EventArgs.Empty);}catch(Exception ex){ReportObserver(ex);}
    }
    private void ReportObserver(Exception ex){try{ObserverFailed?.Invoke(this,ex);}catch{/* An observer cannot roll back a committed document. */}}
    private sealed class HistoryEntry(string name,DocumentSnapshot before,DocumentSnapshot after,DocumentAssetLease a,DocumentAssetLease b) : IDisposable
    {
        public string Name {get;}=name;
        public DocumentSnapshot Before {get;}=before;
        public DocumentSnapshot After {get;}=after;
        public void Dispose(){a.Dispose();b.Dispose();}
    }
    private sealed class CommitResources : IDisposable
    {
        private DocumentAssetLease? before;private DocumentAssetLease? after;private DocumentAssetLease? current;
        private readonly DocumentSnapshot a,b;
        public CommitResources(DocumentSnapshot a,DocumentSnapshot b,IAssetStore store)
        {
            this.a=a;this.b=b;
            try{before=new(a,store);after=new(b,store);current=new(b,store);}catch{Dispose();throw;}
        }
        public HistoryEntry CreateEntry(string name){var e=new HistoryEntry(name,a,b,before!,after!);before=null;after=null;return e;}
        public DocumentAssetLease TakeCurrentLease(){var result=current!;current=null;return result;}
        public void Dispose(){before?.Dispose();after?.Dispose();current?.Dispose();}
    }
}
