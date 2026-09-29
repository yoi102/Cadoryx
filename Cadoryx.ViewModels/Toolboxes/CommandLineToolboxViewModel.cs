using System.Collections.ObjectModel;
using AvalonDock.Core;
using Cadoryx.CommandLine;
using Cadoryx.Editor;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels.Toolboxes;

public sealed record CadTerminalEntry(DateTime Timestamp, string Kind, string Text);

public partial class CommandLineToolboxViewModel : CadToolboxViewModelBase
{
    private readonly ICadCommandLineService service;
    private readonly ICadMessageLog log;
    private readonly SynchronizationContext? uiContext = SynchronizationContext.Current;
    private CadDocumentViewModel? document;
    private bool executingInput;
    private readonly List<string> history = [];
    private int historyIndex;
    [ObservableProperty] private string commandText = "";
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string? selectedSuggestion;
    public ObservableCollection<CadTerminalEntry> Entries { get; } = [];
    public ObservableCollection<string> Suggestions { get; } = [];

    public CommandLineToolboxViewModel(ICadCommandLineService service, IToolboxIconProvider icons, ICadMessageLog log)
        : base("toolbox.command-line", Strings.ResourceManager.GetString("CommandTerminal") ?? "Commands",
            DockZone.BottomRight, "", true)
    {
        this.service = service;
        this.log = log;
        log.MessageAdded += OnMessageAdded;
        Icon = icons.CommandLine;
        Shortcut = "Ctrl+Oem3";
        Add("Info", "Type HELP to see available commands.");
    }

    public void Bind(CadDocumentViewModel? value)
    {
        if (ReferenceEquals(document, value)) return;
        if (document is not null) document.Session.CommandCommitted -= OnCommandCommitted;
        document = value;
        if (document is not null) document.Session.CommandCommitted += OnCommandCommitted;
        Add("Info", value is null ? "No active document." : $"Active document: {value.Session.Snapshot.Name}");
    }

    public void RecordAction(string text) => Add("Action", text);

    private void OnCommandCommitted(object? sender, CadDocumentCommandActivity activity)
    {
        if (!executingInput) Add("Action", $"{activity.Verb}: {activity.Name} · {activity.DocumentName}");
    }

    private void OnMessageAdded(object? sender, CadMessageEntry entry)
    {
        if (entry.Level == CadMessageLevel.Information) return;
        void Append() => Add(entry.Level == CadMessageLevel.Error ? "Error" : "Warning", entry.Text);
        if (uiContext is not null && SynchronizationContext.Current != uiContext) uiContext.Post(_ => Append(), null);
        else Append();
    }

    partial void OnCommandTextChanged(string value)
    {
        Suggestions.Clear();
        SelectedSuggestion = null;
        if (value.Contains(' ') || string.IsNullOrWhiteSpace(value)) return;
        foreach (var suggestion in service.Complete(value)) Suggestions.Add(suggestion);
    }

    public bool AcceptSelectedSuggestion()
    {
        if (SelectedSuggestion is null || !Suggestions.Contains(SelectedSuggestion)) return false;
        CommandText = SelectedSuggestion + " ";
        return true;
    }

    public void SelectSuggestion(int delta)
    {
        if (Suggestions.Count == 0) return;
        int index = SelectedSuggestion is null ? (delta > 0 ? -1 : 0) : Suggestions.IndexOf(SelectedSuggestion);
        SelectedSuggestion = Suggestions[(index + delta + Suggestions.Count) % Suggestions.Count];
    }

    public bool CompleteFirstSuggestion()
    {
        if (Suggestions.Count == 0) return false;
        SelectedSuggestion ??= Suggestions[0];
        return AcceptSelectedSuggestion();
    }

    [RelayCommand]
    private void Previous()
    {
        if (history.Count == 0) return;
        historyIndex = Math.Max(0, historyIndex - 1); CommandText = history[historyIndex];
    }
    [RelayCommand]
    private void Next()
    {
        if (history.Count == 0) return;
        historyIndex = Math.Min(history.Count, historyIndex + 1);
        CommandText = historyIndex == history.Count ? "" : history[historyIndex];
    }
    [RelayCommand]
    private void Clear() => Entries.Clear();

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (IsRunning || string.IsNullOrWhiteSpace(CommandText)) return;
        var input = CommandText.Trim(); CommandText = "";
        if (history.Count == 0 || !string.Equals(history[^1], input, StringComparison.Ordinal)) history.Add(input);
        if (history.Count > 100) history.RemoveAt(0);
        historyIndex = history.Count;
        Add("Input", $"> {input}"); IsRunning = true; executingInput = true;
        try
        {
            var selectedDocument = document;
            var result = await service.ExecuteAsync(input,
                selectedDocument is null || selectedDocument.IsDetached || selectedDocument.IsClosingRequested
                    ? null : new CadCommandContext(selectedDocument));
            if (result.ClearOutput) Entries.Clear();
            else if (!string.IsNullOrWhiteSpace(result.Message)) Add(result.Success ? "Output" : "Error", result.Message);
        }
        catch (Exception error) { Add("Error", error.Message); }
        finally { executingInput = false; IsRunning = false; }
    }

    private void Add(string kind, string text)
    {
        Entries.Add(new CadTerminalEntry(DateTime.Now, kind, text));
        while (Entries.Count > 500) Entries.RemoveAt(0);
    }
}
