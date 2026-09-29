using System.Collections.ObjectModel;
using AvalonDock.Core;
using Cadoryx.Agent;
using Cadoryx.Agent.Codex;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels.Services.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels.Toolboxes;

public sealed record CadChatEntry(string Role, string Content);
public sealed record CadChatAttachment(string Name, AiChatContentPart ContentPart);

public partial class AiAssistantToolboxViewModel : CadToolboxViewModelBase, IDisposable
{
    private readonly IAiAssistantSettingsStore store;
    private readonly IAgentRunner runner;
    private readonly IAiChatClient lmClient;
    private readonly ICodexAgentClient codex;
    private readonly ICadCommandLineService commands;
    private AgentConversation conversation = new();
    private CadDocumentViewModel? document;
    private CancellationTokenSource? running;
    private Task resetTask = Task.CompletedTask;
    private CancellationTokenSource? modelsCancellation;
    private readonly SynchronizationContext? uiContext;
    private int conversationVersion;
    private AiAssistantSettings? settingsDraftBaseline;
    private string? settingsDraftStatus;
    private string[]? settingsDraftModels;
    private bool restoringSettings;
    [ObservableProperty] private string userInput = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string connectionStatus = "";
    [ObservableProperty] private AiAssistantProvider provider;
    [ObservableProperty] private string endpoint = AiAssistantSettings.DefaultEndpoint;
    [ObservableProperty] private string model = "";
    [ObservableProperty] private string codexExecutablePath = "codex";
    [ObservableProperty] private string codexModel = "";
    [ObservableProperty] private string reasoningEffort = "medium";
    [ObservableProperty] private bool enableCadTools = true;
    public Array Providers { get; } = Enum.GetValues<AiAssistantProvider>();
    public IReadOnlyList<string> ReasoningEffortOptions { get; } =
        ["none", "minimal", "low", "medium", "high", "xhigh"];
    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<CadChatEntry> Messages { get; } = [];
    public ObservableCollection<CadChatAttachment> Attachments { get; } = [];

    public AiAssistantToolboxViewModel(IAiAssistantSettingsStore store, IAgentRunner runner,
        IAiChatClient lmClient, ICodexAgentClient codex, ICadCommandLineService commands, IToolboxIconProvider icons)
        : base("toolbox.ai-assistant", Strings.ResourceManager.GetString("AiAssistant") ?? "AI Assistant",
            DockZone.RightBottom, "", false)
    {
        this.store = store; this.runner = runner; this.lmClient = lmClient;
        this.codex = codex; this.commands = commands; Icon = icons.Assistant;
        uiContext = SynchronizationContext.Current;
        var settings = store.Load();
        Provider = settings.Provider; Endpoint = settings.Endpoint; Model = settings.Model;
        CodexExecutablePath = settings.CodexExecutablePath; CodexModel = settings.CodexModel;
        ReasoningEffort = settings.CodexReasoningEffort; EnableCadTools = settings.EnableCadTools;
        ConnectionStatus = Provider == AiAssistantProvider.Codex ? "Codex" : "LM Studio";
        Messages.Add(new CadChatEntry("System", "Configure a provider, then ask about the active CAD document. Type HELP in the command toolbox to see available CAD actions."));
    }

    public void Bind(CadDocumentViewModel? value)
    {
        if (ReferenceEquals(document, value)) return;
        running?.Cancel(); conversationVersion++; document = value; conversation = new AgentConversation();
        resetTask = codex.ResetConversationAsync();
        Attachments.Clear();
        Messages.Clear();
        Messages.Add(new CadChatEntry("System", value is null ? "No active document." : $"Active document: {value.Session.Snapshot.Name}"));
    }

    partial void OnProviderChanged(AiAssistantProvider value)
    {
        modelsCancellation?.Cancel();
        Models.Clear();
        if (settingsDraftBaseline is not null || restoringSettings) return;
        conversationVersion++; conversation = new AgentConversation();
        if (codex is not null) resetTask = codex.ResetConversationAsync();
        ConnectionStatus = value == AiAssistantProvider.Codex ? "Codex" : "LM Studio";
    }

    public void BeginSettingsEdit()
    {
        if (settingsDraftBaseline is not null) return;
        settingsDraftBaseline = CurrentSettings();
        settingsDraftStatus = ConnectionStatus;
        settingsDraftModels = Models.ToArray();
    }

    public void CancelSettingsEdit()
    {
        if (settingsDraftBaseline is not { } baseline) return;
        modelsCancellation?.Cancel();
        restoringSettings = true;
        try
        {
            ApplySettings(baseline);
            Models.Clear();
            foreach (var modelName in settingsDraftModels ?? []) Models.Add(modelName);
            ConnectionStatus = settingsDraftStatus ?? "";
        }
        finally
        {
            restoringSettings = false;
            settingsDraftBaseline = null;
            settingsDraftStatus = null;
            settingsDraftModels = null;
        }
    }

    [RelayCommand]
    private void SaveSettings() => TrySaveSettings();

    public bool TrySaveSettings()
    {
        if (IsBusy) return false;
        try
        {
            var settings = CurrentSettings(); store.Save(settings);
            var baseline = settingsDraftBaseline;
            if (baseline is not null && (baseline.Provider != settings.Provider ||
                !string.Equals(baseline.Endpoint, settings.Endpoint, StringComparison.Ordinal) ||
                !string.Equals(baseline.Model, settings.Model, StringComparison.Ordinal) ||
                !string.Equals(baseline.CodexModel, settings.CodexModel, StringComparison.Ordinal) ||
                !string.Equals(baseline.CodexExecutablePath, settings.CodexExecutablePath, StringComparison.Ordinal)))
            {
                conversationVersion++;
                conversation = new AgentConversation();
                resetTask = codex.ResetConversationAsync();
                Messages.Clear();
                Messages.Add(new CadChatEntry("System", "AI connection changed; a new conversation has started."));
            }
            settingsDraftBaseline = null;
            settingsDraftStatus = null;
            settingsDraftModels = null;
            ConnectionStatus = Provider == AiAssistantProvider.Codex ? "Codex configured" : "LM Studio configured";
            return true;
        }
        catch (Exception e) { ConnectionStatus = "Error"; Messages.Add(new CadChatEntry("Error", e.Message)); return false; }
    }
    private void ApplySettings(AiAssistantSettings settings)
    {
        Provider = settings.Provider; Endpoint = settings.Endpoint; Model = settings.Model;
        CodexExecutablePath = settings.CodexExecutablePath; CodexModel = settings.CodexModel;
        ReasoningEffort = settings.CodexReasoningEffort; EnableCadTools = settings.EnableCadTools;
    }

    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        if (IsBusy) return;
        var cancellation = new CancellationTokenSource();
        modelsCancellation = cancellation;
        var requestedProvider = Provider;
        IsBusy = true; ConnectionStatus = "Loading models...";
        try
        {
            var settings = CurrentSettings();
            var available = Provider == AiAssistantProvider.Codex
                ? await codex.GetModelsAsync(Options(settings), cancellation.Token)
                : await lmClient.GetModelsAsync(settings.Endpoint, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (requestedProvider != Provider) return;
            Models.Clear(); foreach (var name in available) Models.Add(name);
            ConnectionStatus = $"{available.Count} models";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (!cancellation.IsCancellationRequested)
            {
                ConnectionStatus = e.Message;
                Messages.Add(new CadChatEntry("Error", e.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(modelsCancellation, cancellation)) modelsCancellation = null;
            cancellation.Dispose();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Stop() => running?.Cancel();
    public bool AddAttachment(CadChatAttachment attachment)
    {
        if (IsBusy || Attachments.Count >= 5 || attachment.Name.Length == 0) return false;
        Attachments.Add(attachment);
        return true;
    }
    public void ReportAttachmentError(string message) => Messages.Add(new CadChatEntry("Error", message));
    [RelayCommand]
    private void RemoveAttachment(CadChatAttachment? attachment)
    {
        if (!IsBusy && attachment is not null) Attachments.Remove(attachment);
    }
    [RelayCommand]
    private void ClearConversation()
    {
        if (IsBusy) return;
        conversationVersion++; conversation = new AgentConversation(); Messages.Clear(); Attachments.Clear();
        resetTask = codex.ResetConversationAsync();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(UserInput) && Attachments.Count == 0) return;
        var settings = CurrentSettings();
        if (settings.Provider == AiAssistantProvider.LmStudio && string.IsNullOrWhiteSpace(settings.Model))
        {
            ConnectionStatus = "Select an LM Studio model first.";
            return;
        }
        try { store.Save(settings); }
        catch (Exception e)
        {
            ConnectionStatus = "Error";
            Messages.Add(new CadChatEntry("Error", e.Message));
            return;
        }
        var prompt = string.IsNullOrWhiteSpace(UserInput) ? "Please analyze the attached files." : UserInput.Trim();
        var files = Attachments.ToArray();
        var contentParts = new List<AiChatContentPart> { AiChatContentPart.TextPart(prompt) };
        contentParts.AddRange(files.Select(x => x.ContentPart));
        UserInput = ""; Attachments.Clear();
        var selectedDocument = document;
        var requestVersion = conversationVersion;
        Messages.Add(new CadChatEntry("User", files.Length == 0 ? prompt :
            $"{prompt}\n{string.Join(", ", files.Select(x => x.Name))}"));
        running = new CancellationTokenSource(); IsBusy = true; ConnectionStatus = "Thinking...";
        try
        {
            await resetTask;
            var toolset = settings.EnableCadTools ? new CadAgentToolset(commands, selectedDocument) : null;
            var context = selectedDocument is null || selectedDocument.IsDetached ? "No active document." :
                $"Active document: {selectedDocument.Session.Snapshot.Name}; bodies: {selectedDocument.Session.Snapshot.Bodies.Count}; selected: {selectedDocument.Selection.Items.Length}.";
            if (settings.Provider == AiAssistantProvider.Codex)
            {
                var result = await codex.RunAsync(new CodexAgentRunRequest(prompt, context, Options(settings), toolset, contentParts),
                    entry => ReportAsync(entry, requestVersion), running.Token);
                if (result.ResponseWasEmpty && requestVersion == conversationVersion)
                    Messages.Add(new CadChatEntry("System", "The model returned no text."));
            }
            else
            {
                conversation.AddUser(prompt, contentParts);
                var result = await runner.RunAsync(new AgentRunRequest(settings.Endpoint, settings.Model,
                    "You are Cadoryx's 3D CAD assistant. Use CAD tools to inspect the current document before stating its contents. Only claim an edit succeeded after a tool confirms it. " + context,
                    prompt, conversation, settings.ContextWindowTokens, settings.Temperature, toolset),
                    entry => ReportAsync(entry, requestVersion), running.Token);
                if (result.ResponseWasEmpty && requestVersion == conversationVersion)
                    Messages.Add(new CadChatEntry("System", "The model returned no text."));
            }
            if (requestVersion == conversationVersion) ConnectionStatus = "Ready";
        }
        catch (OperationCanceledException)
        {
            if (requestVersion == conversationVersion) { ConnectionStatus = "Stopped"; conversation = new AgentConversation(); }
        }
        catch (Exception e)
        {
            if (requestVersion == conversationVersion)
            {
                ConnectionStatus = "Error"; Messages.Add(new CadChatEntry("Error", e.Message)); conversation = new AgentConversation();
            }
        }
        finally { running?.Dispose(); running = null; IsBusy = false; }
    }

    private ValueTask ReportAsync(AgentRunEvent entry, int version)
    {
        if (version != conversationVersion) return ValueTask.CompletedTask;
        string? content = entry.Kind switch
        {
            AgentRunEventKind.AssistantMessage => entry.Content,
            AgentRunEventKind.ToolResult => $"{entry.ToolName}: {entry.Content}",
            AgentRunEventKind.ContextReduced => "Context was shortened to fit the model window.",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(content))
        {
            void Add() { if (version != conversationVersion) return; Messages.Add(new CadChatEntry(entry.Kind == AgentRunEventKind.ToolResult ? "Tool" : "Assistant", content)); while (Messages.Count > 200) Messages.RemoveAt(0); }
            if (uiContext is null || ReferenceEquals(SynchronizationContext.Current, uiContext)) Add();
            else uiContext.Post(_ => Add(), null);
        }
        return ValueTask.CompletedTask;
    }

    private AiAssistantSettings CurrentSettings()
    {
        var settings = store.Load();
        settings.Provider = Provider; settings.Endpoint = Endpoint; settings.Model = Model;
        settings.CodexExecutablePath = CodexExecutablePath; settings.CodexModel = CodexModel;
        settings.CodexReasoningEffort = ReasoningEffort; settings.EnableCadTools = EnableCadTools;
        settings.Normalize(); return settings;
    }
    private CodexAgentOptions Options(AiAssistantSettings settings) => new(
        settings.CodexExecutablePath, settings.CodexModel, settings.CodexReasoningEffort,
        settings.CodexServiceTier, document?.Session.FilePath is { } path ? Path.GetDirectoryName(path)! : Environment.CurrentDirectory,
        settings.ContextWindowTokens);
    public void Dispose() { running?.Cancel(); running?.Dispose(); modelsCancellation?.Cancel(); }
}
