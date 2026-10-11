using Cadoryx.Db;

namespace Cadoryx.Editor;

/// <summary>Exact author state that an invocation inspected before it was queued.</summary>
public sealed record DocumentCommandExpectation(DocumentId DocumentId, DocumentStateId StateId)
{
    public static DocumentCommandExpectation Capture(DocumentSnapshot snapshot) => new(snapshot.Id, snapshot.StateId);
    public void Validate(DocumentSnapshot snapshot)
    {
        if (snapshot.Id != DocumentId || snapshot.StateId != StateId) throw new StaleDocumentException();
    }
}
