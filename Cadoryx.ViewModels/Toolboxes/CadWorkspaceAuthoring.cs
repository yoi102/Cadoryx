using System.Collections.ObjectModel;
using System.Text.Json;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal sealed record CadWorkspaceExpectation(Guid WorkspaceStateId, Guid? ActiveSessionId, DocumentId? DocumentId, DocumentStateId? StateId);

/// <summary>UI-thread workspace authoring. Only synchronous, committed tool transitions preserve toolbox conversations.</summary>
internal sealed class CadWorkspaceAuthoring
{
    private readonly CadWorkspace workspace;
    private readonly ObservableCollection<CadDocumentViewModel> documents;
    private readonly Func<CadDocumentViewModel?> active;
    private readonly Func<CadDocumentSession, CadDocumentViewModel> attach;
    private readonly Action<CadDocumentViewModel> activate;
    private readonly Func<bool> available;
    private readonly IAssetStore assets;
    private readonly IDocumentStorage storage;
    private readonly IExchangeImportService importer;
    private Guid workspaceStateId = Guid.NewGuid();
    private bool operationPending;
    internal bool IsTransitioning { get; private set; }
    internal CadDocumentViewModel? ActiveDocument => active();

    internal CadWorkspaceAuthoring(CadWorkspace workspace, ObservableCollection<CadDocumentViewModel> documents,
        Func<CadDocumentViewModel?> active, Func<CadDocumentSession, CadDocumentViewModel> attach,
        Action<CadDocumentViewModel> activate, Func<bool> available, IAssetStore assets,
        IDocumentStorage storage, IExchangeImportService importer)
    {
        this.workspace = workspace; this.documents = documents; this.active = active; this.attach = attach;
        this.activate = activate; this.available = available; this.assets = assets; this.storage = storage; this.importer = importer;
        documents.CollectionChanged += (_, _) => NotifyChanged();
    }

    internal void NotifyChanged() => workspaceStateId = Guid.NewGuid();
    internal CadWorkspaceExpectation Capture()
    {
        var doc = active();
        return new(workspaceStateId, doc?.Session.SessionId, doc?.Session.Snapshot.Id, doc?.Session.Snapshot.StateId);
    }
    private void Validate(CadWorkspaceExpectation expected)
    {
        if (expected != Capture()) throw new InvalidOperationException("Workspace or active document changed. Inspect workspace again.");
        if (!available()) throw new InvalidOperationException("Workspace is busy or shutting down.");
        if (active() is { } doc && (doc.IsDetached || doc.IsClosingRequested || doc.Session.IsClosing))
            throw new InvalidOperationException("Active document is closing or detached.");
    }
    private static object Identity(CadDocumentViewModel doc) => new
    {
        sessionId = doc.Session.SessionId, documentId = doc.Session.Snapshot.Id.ToString(), stateId = doc.Session.Snapshot.StateId.ToString(),
        name = doc.Session.Snapshot.Name, path = doc.Session.FilePath, dirty = doc.Session.IsDirty,
        readOnly = doc.IsReadOnly, closing = doc.IsClosingRequested || doc.Session.IsClosing
    };
    internal string List(JsonElement args)
    {
        RequireFields(args, ["action", "offset", "limit"]);
        int Page(string key, int fallback, int min, int max) => !args.TryGetProperty(key, out var value) ? fallback :
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= min && n <= max ? n : throw new ArgumentException("Invalid " + key + ".");
        var offset = Page("offset", 0, 0, 100000); var limit = Page("limit", 8, 1, 50);
        return JsonSerializer.Serialize(new { success = true, expected = ExpectedJson(Capture()), activeSessionId = active()?.Session.SessionId,
            total = documents.Count, offset, limit, sessions = documents.Skip(offset).Take(limit).Select(d => new { active = ReferenceEquals(d, active()), identity = Identity(d) }) });
    }
    private static object ExpectedJson(CadWorkspaceExpectation expected) => new { workspaceStateId = expected.WorkspaceStateId,
        activeSessionId = expected.ActiveSessionId, documentId = expected.DocumentId?.ToString(), stateId = expected.StateId?.ToString() };
    internal static CadWorkspaceExpectation ParseExpectation(JsonElement args)
    {
        if (!args.TryGetProperty("expected", out var value)) throw new ArgumentException("Mutation requires expected from workspace list.");
        RequireFields(value, ["workspaceStateId", "activeSessionId", "documentId", "stateId"]);
        Guid? Nullable(string key) => !value.TryGetProperty(key, out var field) ? throw new ArgumentException("Missing expected." + key) :
            field.ValueKind == JsonValueKind.Null ? null : RequiredGuid(value, key);
        var session = Nullable("activeSessionId"); var document = Nullable("documentId"); var state = Nullable("stateId");
        if ((session is null) != (document is null) || (session is null) != (state is null)) throw new ArgumentException("Empty workspace identity requires all three active identity fields to be null.");
        return new(RequiredGuid(value, "workspaceStateId"), session, document is {} d ? new(d) : null, state is {} s ? new(s) : null);
    }
    internal async Task<string> ApplyAsync(JsonElement args, CadWorkspaceExpectation expected, bool tool, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var action = RequiredString(args, "action", 16);
        var fields = action switch { "new" => new[] { "action", "name" }, "open" => ["action", "path"], "activate" => ["action", "target"], _ => throw new ArgumentException("Unknown workspace action.") };
        RequireFields(args, tool ? [.. fields, "expected"] : fields);
        Validate(expected);
        if (operationPending) throw new InvalidOperationException("Another workspace author operation is pending.");
        operationPending = true;
        try
        {
            if (action == "activate")
            {
                if (!args.TryGetProperty("target", out var target)) throw new ArgumentException("Missing target identity.");
                RequireFields(target, ["sessionId", "documentId", "stateId"]);
                var doc = documents.SingleOrDefault(d => d.Session.SessionId == RequiredGuid(target, "sessionId")) ?? throw new InvalidOperationException("Target session is not open.");
                new DocumentCommandExpectation(new(RequiredGuid(target, "documentId")), new(RequiredGuid(target, "stateId"))).Validate(doc.Session.Snapshot);
                return Activate(doc);
            }
            if (action == "new")
            {
                var name = RequiredString(args, "name", 200); CadGuard.Name(name);
                return await AttachAsync(DocumentSnapshot.Create(name), null, [], token);
            }
            var path = RequiredString(args, "path", 4096);
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Open requires an absolute path.");
            path = Path.GetFullPath(path);
            var native = Path.GetExtension(path).Equals(".cadoryx", StringComparison.OrdinalIgnoreCase);
            if (native)
            {
                var matches = documents.Where(d => string.Equals(d.Session.FilePath, path, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1) throw new InvalidOperationException("File path matches multiple sessions. Activate an exact session instead.");
                if (matches.Length == 1) return Activate(matches[0]);
            }
            if (!File.Exists(path)) throw new FileNotFoundException("Requested input file does not exist.", path);
            using var loaded = await Task.Run(() => native ? storage.LoadAsync(path, assets, token) : importer.ImportAsync(path, assets, token), token);
            // The importer can finish after cancellation or a foreground edit/switch/close.
            token.ThrowIfCancellationRequested(); Validate(expected);
            if (loaded.Diagnostics.Any(d => d.IsError)) throw new InvalidOperationException("Input has error diagnostics: " + loaded.Diagnostics.First(d => d.IsError).Message);
            if (documents.Any(d => d.Session.Snapshot.Id == loaded.Snapshot.Id))
                throw new InvalidOperationException("This document identity is already open from another path. Activate its exact session instead.");
            return await AttachAsync(loaded.Snapshot, native ? path : null, loaded.Diagnostics, token);
        }
        finally { operationPending = false; }

        string Activate(CadDocumentViewModel doc)
        {
            token.ThrowIfCancellationRequested(); Validate(expected);
            if (doc.IsDetached || doc.IsClosingRequested || doc.Session.IsClosing) throw new InvalidOperationException("Target session is closing or detached.");
            var changed = !ReferenceEquals(doc, active());
            Transition(() => activate(doc));
            return Receipt(doc, changed, false, []);
        }
        async Task<string> AttachAsync(DocumentSnapshot snapshot, string? path, IEnumerable<CadDiagnostic> diagnostics, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Validate(expected);
            CadDocumentSession? candidate = null;
            try
            {
                candidate = workspace.Attach(snapshot, path);
                CadDocumentViewModel? attached = null;
                Transition(() => attached = attach(candidate));
                if (attached is null || !documents.Contains(attached) || !ReferenceEquals(attached, active()))
                    throw new InvalidOperationException("Workspace attachment did not activate the new session.");
                return Receipt(attached, true, true, diagnostics);
            }
            catch { if (candidate is not null) await workspace.CloseAsync(candidate); throw; }
        }
    }
    private void Transition(Action action)
    {
        IsTransitioning = true;
        try { action(); }
        finally { IsTransitioning = false; }
    }
    private string Receipt(CadDocumentViewModel doc, bool changed, bool opened, IEnumerable<CadDiagnostic> diagnostics)
    {
        var rows = diagnostics.ToArray();
        return JsonSerializer.Serialize(new { success = true, changed = false, workspaceChanged = changed, opened, active = Identity(doc),
            expected = ExpectedJson(Capture()), diagnostics = rows.Take(8).Select(d => new { code = Clip(d.Code, 100), message = Clip(d.Message, 400), d.IsError }),
            diagnosticCount = rows.Length, diagnosticsTruncated = rows.Length > 8 || rows.Any(d => d.Code.Length > 100 || d.Message.Length > 400) });
    }
    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + " [truncated]";
    internal static string Contract() => JsonSerializer.Serialize(new { success = true, actions = new[] { "list", "new", "open", "activate" },
        identity = "Copy expected from list; activeSessionId/documentId/stateId are explicit null with no active document. Session ID distinguishes open instances. Terminal captures expected.",
        @new = "name required, max 200 chars; creates and activates a new empty native document.",
        open = "Absolute existing path; .cadoryx uses native storage, exchange uses production importer. Already open native path activates its unique session. Duplicate document identity from another path is refused.",
        activate = "target object requires exact sessionId/documentId/stateId from list. No names or indices.",
        transitions = "Successful tool transitions retain the running conversation. Foreground user transitions cancel it. Async reads recheck workspace and active state before attaching; cancellation releases candidate assets.",
        list = "offset 0..100000, limit 1..50 (default8). No close/discard action; dirty documents remain open. No undo entry." });
}

internal sealed class CadWorkspaceCommandContext(CadWorkspaceAuthoring workspace) : ICadWorkspaceCommandContext
{
    private readonly CadWorkspaceExpectation expected = workspace.Capture();
    public async Task<CadCommandResult> ExecuteWorkspaceAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        using var parsed = JsonDocument.Parse(requestJson); var args = parsed.RootElement;
        var action = RequiredString(args, "action", 16);
        if (action == "list") return new(true, workspace.List(args));
        if (action == "contract") { RequireFields(args, ["action"]); return new(true, CadWorkspaceAuthoring.Contract()); }
        return new(true, await workspace.ApplyAsync(args, expected, false, cancellationToken));
    }
}
