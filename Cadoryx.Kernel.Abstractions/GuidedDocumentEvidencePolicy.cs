namespace Cadoryx.Kernel.Abstractions;

/// <summary>Explicit producer policy. It selects copied evidence for actual new document results,
/// never promotes imported bytes to original-source proof and never limits modeling size.</summary>
public enum GuidedDocumentEvidencePolicy
{
    LegacyMembers=0,
    PreferCurrentOccurrences=1
}
