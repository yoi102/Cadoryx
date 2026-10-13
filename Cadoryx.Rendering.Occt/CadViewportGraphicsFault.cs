using OcctSharp;

namespace Cadoryx.Rendering.Occt;

/// <summary>Copied rendering evidence; initial context absence does not trigger recovery.</summary>
public readonly record struct CadViewportGraphicsFault(ViewerRenderingHealth NativeHealth,bool OwnerDisposed,bool ContextPreviouslyValid)
{
    public bool RequiresNewViewer=>OwnerDisposed||
        (NativeHealth&(ViewerRenderingHealth.PresentationDataLost|ViewerRenderingHealth.ContextInvalid))!=0||
        ContextPreviouslyValid&&(NativeHealth&ViewerRenderingHealth.ContextUnavailable)!=0;
}

/// <summary>Known unavailable graphics. Ordinary geometry failures retain their original type.</summary>
public sealed class CadViewportGraphicsFaultException(CadViewportGraphicsFault observation)
    :Exception("Viewport graphics data is unavailable.")
{
    public CadViewportGraphicsFault Observation{get;}=observation;
}
