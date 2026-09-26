namespace Cadoryx.Editor;

/// <summary>Unique asset payload retained by history in addition to the current document.
/// Excludes managed snapshot overhead, captures, native geometry and graphics memory.</summary>
public sealed record DocumentHistoryUsage(int UndoEntries,int RedoEntries,int AdditionalAssets,
    long AdditionalAssetBytes,int EntryLimit,long AssetBudgetBytes);
