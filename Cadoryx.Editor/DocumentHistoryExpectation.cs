using Cadoryx.Db;

namespace Cadoryx.Editor;

/// <summary>Author identity plus the session preview boundary. Cursor movement does not change the author StateId.</summary>
public sealed record DocumentHistoryExpectation(DocumentCommandExpectation Author, FeatureCursor? Cursor)
{
    public void Validate(DocumentSnapshot snapshot, FeatureCursor? cursor)
    {
        Author.Validate(snapshot);
        if (Cursor != cursor) throw new StaleDocumentException();
    }
}
