using OcctSharp;

internal static class PresentationMetadataProbe
{
    internal static void Run(Action<string,Func<object>> probe,string output)
    {
        foreach(int channel in new[]{0,1,2})probe("independent-presentation-channel-"+channel,()=>
        {
            using var source=XdeDocument.Create();using var destination=XdeDocument.Create();using var shape=ShapeFactory.CreateBox(2,3,4);XdeLabel from,to;
            using(var edit=source.BeginTransaction("Independent channels"))
            {from=source.AddShape(shape,"Source");from.SetPresentationColor((XdeColorChannel)channel,new(.25,.5,.75,.5));from.AddLayer("Inspection");from.Material=new("Alloy","Density",2.7,"g/cm3","density");edit.Commit();}
            using(var edit=destination.BeginTransaction("Copy presentation"))
            {to=destination.AddShape(shape,"Target");destination.CopyPresentationMetadata(from,to);edit.Commit();}
            Require(from.GetPresentationColors()==to.GetPresentationColors(),"A direct native color channel changed.");
            Require(to.Name=="Target"&&to.Material==from.Material&&to.Layers.SequenceEqual(from.Layers),"Presentation/name contract changed.");
            return new{channel,independentChannels=true};
        });
        foreach(bool reflected in new[]{false,true})probe("native-compound-face-presentation-"+reflected,()=>
        {
            using var source=XdeDocument.Create();using var destination=XdeDocument.Create();using var shape=ShapeFactory.CreateBox(2,3,4);XdeLabel root;
            using(var edit=source.BeginTransaction("Source")){root=source.AddShape(shape,"Source");root.SetPresentationColor(XdeColorChannel.General,new(.2,.3,.4));edit.Commit();}
            using var sourceMap=new RepairDocumentSession(source,root);var marked=sourceMap.Source.Topology.First(t=>t.Kind==ShapeKind.Face);XdeLabel face;
            using(var edit=source.BeginTransaction("Marked face"))
            {face=sourceMap.GetOrCreateSubshapeLabel(marked.Selection);face.SetPresentationColor(XdeColorChannel.Surface,new(1,0,0,.5));face.SetPresentationColor(XdeColorChannel.Curve,new(0,0,1));face.AddLayer("Cutting");face.VisualMaterial=new("Coating",new(1,0,0),.4,.6,new(0,0,0));edit.Commit();}
            using var transform=reflected?GpTrsf.CreatePlaneMirror(0,0,0,0,0,1):GpTrsf.Create(9,-2,3,2,1,3,.4);
            using var moved=ShapeHistoryModeling.Transform(sourceMap.Source,transform);using var second=RepairSnapshot.Create(moved.RequireShape());
            using var joined=ShapeHistoryModeling.Compound([sourceMap.Source,second]);using var joinedMap=RepairSnapshot.Create(joined.RequireShape());
            var copySlot=moved.History.Single(h=>h.Source?.TopologyIndex==marked.Selection.Index&&h.Kind is LocalFeatureHistoryKind.Modified or LocalFeatureHistoryKind.Unchanged).ResultTopologyIndex;
            Require(copySlot.HasValue,"Marked transform face was not proved.");
            var targets=joined.History.Where(h=>h.Source is {} s&&((s.ArgumentIndex==0&&s.TopologyIndex==marked.Selection.Index)||(s.ArgumentIndex==1&&s.TopologyIndex==copySlot)))
                .Select(h=>h.ResultTopologyIndex??throw new InvalidOperationException("Missing compound target")).Distinct().ToArray();
            Require(targets.Length==2,"Compound lost a native copy path.");XdeLabel result;
            using(var edit=destination.BeginTransaction("Attach proved members"))
            {
                result=destination.AddShape(joined.RequireShape(),"Compound");destination.CopyPresentationMetadata(root,result);
                using var membership=new RepairDocumentSession(destination,result);
                VerifyMap(joinedMap,membership.Source,output,"membership-"+reflected);
                foreach(int slot in targets)destination.CopyPresentationMetadata(face,membership.GetOrCreateSubshapeLabel(membership.Source.Select(slot)));
                edit.Commit();
            }
            var path=Path.Combine(output,"native-compound-"+reflected+".xbf");destination.Save(path);using var reopened=XdeDocument.Open(path);
            var restored=reopened.GetLabel(result.Entry);using var restoredMap=new RepairDocumentSession(reopened,restored);using var verify=reopened.BeginTransaction("Read native members");
            VerifyMap(joinedMap,restoredMap.Source,output,"reopen-"+reflected);
            foreach(int slot in targets)
            {
                var actual=restoredMap.GetOrCreateSubshapeLabel(restoredMap.Source.Select(slot));
                Require(actual.GetPresentationColors()==face.GetPresentationColors()&&actual.Layers.SequenceEqual(face.Layers)&&actual.VisualMaterial==face.VisualMaterial,"A proved compound face lost presentation.");
            }
            return new{reflected,provedFaceCopies=targets.Length,nativeReopened=true,exactFingerprint=restoredMap.Source.Fingerprint==joinedMap.Fingerprint,
                orderedTopologyAndEverySlotCanonicalBrepVerified=true};
        });
        probe("hidden-normalized-definition-and-native-reopen",()=>
        {
            using var source=XdeDocument.Create();using var destination=XdeDocument.Create();using var shape=ShapeFactory.CreateBox(2,3,4);XdeLabel root,copied;
            using(var edit=source.BeginTransaction("Hidden root")){root=source.AddShape(shape,"Hidden");root.IsVisible=false;root.SetPresentationColor(XdeColorChannel.General,new(1,0,0));root.SetPresentationColor(XdeColorChannel.Surface,new(0,1,0));root.SetPresentationColor(XdeColorChannel.Curve,new(0,0,1));root.UpdateValidationPropertiesFromShape();root.Comment="Exclude";edit.Commit();}
            using var transform=GpTrsf.CreatePlaneMirror(0,0,0,0,0,1);
            using(var edit=destination.BeginTransaction("Transform presentation")){copied=destination.CopyTransformedDefinition(root,transform);edit.Commit();}
            Require(copied.GetPresentationColors()==root.GetPresentationColors(),"Normalization lost visibility or collapsed channels.");
            Require(!copied.ValidationProperties.HasAny&&copied.Comment is null,"Unproved validation/text copied.");
            var path=Path.Combine(output,"hidden-normalized.xbf");destination.Save(path);using var reopened=XdeDocument.Open(path);
            Require(reopened.GetLabel(copied.Entry).GetPresentationColors()==root.GetPresentationColors(),"Hidden normalized metadata lost during native persistence.");
            return new{hidden=true,independentChannels=true,nativeReopened=true,validationAndTextExcluded=true};
        });
        probe("assigned-target-and-closed-transaction-rejection",()=>
        {
            using var document=XdeDocument.Create();using var first=ShapeFactory.CreateBox(2,3,4);using var second=ShapeFactory.CreateBox(3,4,5);XdeLabel source,target;
            using(var edit=document.BeginTransaction("Prepare")){source=document.AddShape(first,"Source");target=document.AddShape(second,"Target");target.IsVisible=false;edit.Commit();}
            int rejected=0;try{document.CopyPresentationMetadata(source,target);}catch(ArgumentException){rejected++;}
            using(var edit=document.BeginTransaction("Reject hidden target")){try{document.CopyPresentationMetadata(source,target);}catch(ArgumentException){rejected++;}}
            Require(rejected==2&&!target.IsVisible&&target.Name=="Target","Invalid copy modified the target.");return new{rejected};
        });
    }
    private static void VerifyMap(RepairSnapshot before,RepairSnapshot after,string output,string name)
    {
        Require(before.Topology.Select(t=>(t.Kind,t.Orientation,t.ParentIndex)).SequenceEqual(after.Topology.Select(t=>(t.Kind,t.Orientation,t.ParentIndex))),"Reopen changed ordered topology.");
        var directory=Path.Combine(output,name);Directory.CreateDirectory(directory);
        string Canonical(Shape shape,string path)
        {ShapeExchange.WriteBrep(shape,path);using var reread=ShapeExchange.ReadBrep(path);ShapeExchange.WriteBrep(reread,path);return File.ReadAllText(path);}
        foreach(var slot in before.Topology)
        {
            using var a=before.CopySubshape(slot.Selection);using var b=after.CopySubshape(after.Select(slot.Selection.Index));
            var first=Canonical(a,Path.Combine(directory,slot.Selection.Index+"-before.brep"));
            var second=Canonical(b,Path.Combine(directory,slot.Selection.Index+"-after.brep"));
            Require(Cadoryx.Kernel.Occt.BrepDirectionRoundtrip.Matches(first,second),"Canonical BRep mismatch in same ordered native slot "+slot.Selection.Index);
        }
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
