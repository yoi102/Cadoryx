using Cadoryx.Db;
using Cadoryx.Rendering;
using OcctSharp;

namespace Cadoryx.Rendering.Occt;

public enum OccurrenceGizmoMode { Move,Rotate,Scale }

public sealed partial class OcctViewport
{
    private OccurrencePath? handleOccurrence,dragOccurrence;
    private ViewerManipulator? occurrenceManipulator;
    private ViewerPresentation? occurrenceGizmoPresentation;
    private ViewerManipulatorMode dragManipulatorMode;
    private readonly Dictionary<ViewerPresentation,GpTrsf> occurrenceStartTransforms=[];
    private double[]? occurrenceWorldMatrix;
    private BodyId? occurrenceFocusBody;
    private bool occurrenceAllowScale;
    public event EventHandler<(OccurrencePath Path,BodyId? FocusBody,ViewerManipulatorMode Mode,double[] WorldMatrix,bool Commit)>? OccurrenceGizmoFinished;
    public ViewerManipulatorState? OccurrenceGizmoState=>occurrenceManipulator?.State;
    public bool CanScaleOccurrence=>handleOccurrence is not null&&occurrenceAllowScale;
    public OccurrenceGizmoMode GizmoMode {get;private set;}=OccurrenceGizmoMode.Move;
    public void SetOccurrenceGizmoMode(OccurrenceGizmoMode mode)
    {
        if(!Enum.IsDefined(mode))throw new ArgumentOutOfRangeException(nameof(mode));
        if(mode==OccurrenceGizmoMode.Scale&&handleOccurrence is not null&&!occurrenceAllowScale)
            throw new InvalidOperationException("This occurrence does not support scaling.");
        GizmoMode=mode;
        if(handleOccurrence is {} path)SetOccurrenceHandles(path,occurrenceFocusBody,occurrenceAllowScale);
    }
    public ViewerManipulatorMode HitOccurrenceGizmo(int x,int y)
    {
        if(occurrenceManipulator is not {} manipulator)return ViewerManipulatorMode.None;
        viewer.MoveTo(x,y);return manipulator.State.ActiveMode;
    }

    public void SetOccurrenceHandles(OccurrencePath? path,BodyId? focusBody=null,bool allowScale=false)
    {
        if(dragOccurrence is not null)FinishOccurrenceHandle(false);
        ClearOccurrenceHandles();
        occurrenceFocusBody=focusBody;occurrenceAllowScale=allowScale;
        if(!allowScale&&GizmoMode==OccurrenceGizmoMode.Scale)GizmoMode=OccurrenceGizmoMode.Move;
        if(path is null||constructing||assemblyDatumSelection is not null||selectionKind is not null)return;
        var selected=entries.Values.Where(e=>OccurrenceDrag.Contains(path,e.Item.Path)).Select(e=>e.Item).ToArray();
        if(selected.Length==0)return;
        var focus=selected.FirstOrDefault(i=>i.BodyId==focusBody)??
            selected.OrderByDescending(i=>(i.Geometry.Bounds.Max-i.Geometry.Bounds.Min).Length).First();
        var bounds=SceneEnvelope.Measure([focus])!.Value;
        var center=(bounds.Min+bounds.Max)/2;
        try
        {
            occurrenceGizmoPresentation=entries[(focus.Path,focus.BodyId)].Presentation;
            occurrenceManipulator=occurrenceGizmoPresentation.CreateManipulator(new()
            {
                AdjustPosition=false,AdjustSize=false,ActivationOnDetection=true,
                ZoomPersistence=true,Skin=ViewerManipulatorSkin.Shaded,
                EnabledModes=GizmoMode switch
                {
                    OccurrenceGizmoMode.Rotate=>ViewerManipulatorModes.Rotation,
                    OccurrenceGizmoMode.Scale when allowScale=>ViewerManipulatorModes.Scaling,
                    _=>ViewerManipulatorModes.Translation|ViewerManipulatorModes.TranslationPlane
                },
                Size=70,Gap=8,
                Position=GpAx2Value.Create(new(center.X,center.Y,center.Z),new(0,0,1),new(1,0,0))
            });
            // EnabledModes controls selection only. OCCT still draws all parts until
            // each hidden mode is explicitly switched off on every axis.
            foreach(var axis in Enum.GetValues<ViewerManipulatorAxis>())
                foreach(var part in new[]{ViewerManipulatorMode.Translation,ViewerManipulatorMode.Rotation,
                    ViewerManipulatorMode.Scaling,ViewerManipulatorMode.TranslationPlane})
                    occurrenceManipulator.SetPart(axis,part,GizmoMode switch
                    {
                        OccurrenceGizmoMode.Move=>part is ViewerManipulatorMode.Translation or ViewerManipulatorMode.TranslationPlane,
                        OccurrenceGizmoMode.Rotate=>part==ViewerManipulatorMode.Rotation,
                        OccurrenceGizmoMode.Scale=>part==ViewerManipulatorMode.Scaling,
                        _=>false
                    });
            handleOccurrence=path;viewer.Redraw();
        }
        catch{ClearOccurrenceHandles();throw;}
    }

    private void ClearOccurrenceHandles()
    {
        bool hadHandles=occurrenceManipulator is not null;
        foreach(var original in occurrenceStartTransforms.Values)original.Dispose();
        occurrenceStartTransforms.Clear();occurrenceWorldMatrix=null;dragManipulatorMode=ViewerManipulatorMode.None;
        occurrenceManipulator?.Dispose();occurrenceManipulator=null;occurrenceGizmoPresentation=null;
        handleOccurrence=null;occurrenceFocusBody=null;
        if(hadHandles&&!disposed)viewer.Redraw();
    }

    private bool TryStartOccurrenceHandle(int x,int y)
    {
        if(handleOccurrence is not {} path)return false;
        if(occurrenceManipulator is {} gizmo)
        {
            var mode=HitOccurrenceGizmo(x,y);
            if(mode!=ViewerManipulatorMode.None)
            {
                dragOccurrence=path;dragManipulatorMode=mode;
                try
                {
                    foreach(var entry in entries.Values.Where(e=>OccurrenceDrag.Contains(path,e.Item.Path)&&
                        (GizmoMode!=OccurrenceGizmoMode.Scale||ReferenceEquals(e.Presentation,occurrenceGizmoPresentation))))
                        occurrenceStartTransforms.Add(entry.Presentation,entry.Presentation.GetTransform());
                    gizmo.Start(x,y);
                    occurrenceWorldMatrix=null;
                    return true;
                }
                catch
                {
                    foreach(var original in occurrenceStartTransforms.Values)original.Dispose();
                    occurrenceStartTransforms.Clear();dragOccurrence=null;dragManipulatorMode=ViewerManipulatorMode.None;
                    throw;
                }
            }
        }
        return false;
    }

    private void MoveOccurrenceHandle(int x,int y)
    {
        if(dragOccurrence is null)return;
        if(dragManipulatorMode!=ViewerManipulatorMode.None&&occurrenceManipulator is {} gizmo)
        {
            using var local=gizmo.Transform(x,y);
            var focus=occurrenceStartTransforms[occurrenceGizmoPresentation!];
            using var inverse=focus.Inverted();
            using var composed=focus.Multiplied(local);
            using var world=composed.Multiplied(inverse);
            gizmo.Preview(local);
            foreach(var (presentation,original) in occurrenceStartTransforms)
            {
                if(ReferenceEquals(presentation,occurrenceGizmoPresentation))continue;
                using var moved=world.Multiplied(original);
                presentation.SetTransform(moved);
            }
            occurrenceWorldMatrix=[world.Value(1,1),world.Value(1,2),world.Value(1,3),world.Value(1,4),
                world.Value(2,1),world.Value(2,2),world.Value(2,3),world.Value(2,4),
                world.Value(3,1),world.Value(3,2),world.Value(3,3),world.Value(3,4)];
            viewer.Redraw();return;
        }
    }

    private void FinishOccurrenceHandle(bool commit)
    {
        if(dragOccurrence is not {} path)return;
        if(dragManipulatorMode!=ViewerManipulatorMode.None&&occurrenceManipulator is {} gizmo)
        {
            var mode=dragManipulatorMode;
            var matrix=occurrenceWorldMatrix;
            gizmo.Stop(commit&&matrix is not null);
            if(!commit||matrix is null)
            {
                foreach(var (presentation,original) in occurrenceStartTransforms)
                    presentation.SetTransform(original);
                viewer.Redraw();
            }
            foreach(var original in occurrenceStartTransforms.Values)original.Dispose();
            occurrenceStartTransforms.Clear();dragOccurrence=null;dragManipulatorMode=ViewerManipulatorMode.None;
            occurrenceWorldMatrix=null;
            if(matrix is not null)OccurrenceGizmoFinished?.Invoke(this,(path,occurrenceFocusBody,mode,matrix,commit));
            return;
        }
    }
}
