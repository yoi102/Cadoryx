using System.Collections.ObjectModel;

namespace Cadoryx.ViewModels.Services.Platform.Notifications;

public enum CadMessageLevel
{
    Information,
    Warning,
    Error
}

public sealed record CadMessageEntry(
    DateTimeOffset Timestamp,
    CadMessageLevel Level,
    string Text,
    string? Source);

public interface ICadMessageLog
{
    ReadOnlyObservableCollection<CadMessageEntry> Entries { get; }

    event EventHandler<CadMessageEntry>? MessageAdded;

    event EventHandler? Cleared;

    void Add(string text, CadMessageLevel level = CadMessageLevel.Information, string? source = null);

    void Clear();
}

public sealed class CadMessageLog : ICadMessageLog
{
    private const int DefaultMaximumEntryCount = 1000;
    private readonly ObservableCollection<CadMessageEntry> _entries = [];
    private readonly int _maximumEntryCount;

    public CadMessageLog(int maximumEntryCount = DefaultMaximumEntryCount)
    {
        if (maximumEntryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntryCount));

        _maximumEntryCount = maximumEntryCount;
        Entries = new ReadOnlyObservableCollection<CadMessageEntry>(_entries);
    }

    public ReadOnlyObservableCollection<CadMessageEntry> Entries { get; }

    public event EventHandler<CadMessageEntry>? MessageAdded;

    public event EventHandler? Cleared;

    public void Add(
        string text,
        CadMessageLevel level = CadMessageLevel.Information,
        string? source = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var entry = new CadMessageEntry(
            DateTimeOffset.Now,
            level,
            text.Trim(),
            string.IsNullOrWhiteSpace(source) ? null : source.Trim());

        while (_entries.Count >= _maximumEntryCount)
            _entries.RemoveAt(0);

        _entries.Add(entry);
        MessageAdded?.Invoke(this, entry);
    }

    public void Clear()
    {
        if (_entries.Count == 0)
            return;

        _entries.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }
}
