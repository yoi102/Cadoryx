using Cadoryx.IO;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SectionMigrationTests
{
    private static readonly SectionFormat Old=new("old",1,"json"),New=new("old",2,"messagepack"),Extra=new("extra",1,"messagepack");
    [Fact] public void MultiSectionMigrationRequiresAllInputsAndPublishesAllOutputs()
    {
        var registry=new CadSectionMigrationRegistry(false);
        var peer=new SectionFormat("peer",1,"json");
        registry.RegisterStep("combine",[Old,peer],[New,Extra],input=>[new(New,input["old"].Bytes),new(Extra,input["peer"].Bytes)]);
        var targets=new Dictionary<string,SectionFormat>{{New.Kind,New},{Extra.Kind,Extra}};
        Assert.Throws<NotSupportedException>(()=>registry.Migrate([new(Old,new byte[]{1})],targets));
        var result=registry.Migrate([new(Old,new byte[]{1}),new(peer,new byte[]{2})],targets);
        Assert.Equal(2,result.Count);Assert.Equal(new byte[]{1},result["old"].Bytes.ToArray());Assert.Equal(new byte[]{2},result["extra"].Bytes.ToArray());
    }
    [Theory][InlineData("missing")][InlineData("wrong-version")][InlineData("too-big")][InlineData("collision")][InlineData("throw")][InlineData("cancel")]
    public void FailedMigrationLeavesOriginalPayloadsUntouched(string failure)
    {
        var registry=new CadSectionMigrationRegistry(false);using var cancel=new CancellationTokenSource();
        var original=new SectionPayload(Old,new byte[]{1,2,3});
        registry.RegisterStep("step",[Old],[New,Extra],input=>
        {
            if(failure=="throw")throw new IOException("injected migration failure");
            if(failure=="cancel")cancel.Cancel();
            if(failure=="missing")return [new(New,new byte[]{4})];
            return [new(failure=="wrong-version"?New with{Version=3}:New,failure=="too-big"?new byte[10]:new byte[]{4}),new(Extra,new byte[]{5})];
        });
        var inputs=failure=="collision"?new[]{original,new SectionPayload(Extra,new byte[]{9})}:new[]{original};
        Assert.ThrowsAny<Exception>(()=>registry.Migrate(inputs,new Dictionary<string,SectionFormat>{{"old",New},{"extra",Extra}},maxSectionBytes:5,cancellationToken:cancel.Token));
        Assert.Equal(Old,original.Format);Assert.Equal(new byte[]{1,2,3},original.Bytes.ToArray());
    }
    [Fact] public void AmbiguousBackwardAndDuplicateStepsAreRejected()
    {
        var registry=new CadSectionMigrationRegistry(false);
        registry.RegisterStep("step",[Old],[New],_=>[new(New,new byte[]{1})]);
        Assert.Throws<ArgumentException>(()=>registry.RegisterStep("ambiguous",[Old],[New with{Version=3}],_=>[]));
        Assert.Throws<ArgumentException>(()=>registry.RegisterStep("backward",[New],[Old],_=>[]));
    }
    [Fact] public void MigrationEnforcesCombinedOutputBudget()
    {
        var registry=new CadSectionMigrationRegistry(false);
        registry.RegisterStep("split",[Old],[New,Extra],_=>[new(New,new byte[6]),new(Extra,new byte[6])]);
        Assert.Throws<InvalidDataException>(()=>registry.Migrate([new(Old,new byte[]{1})],new Dictionary<string,SectionFormat>{{"old",New}},maxSectionBytes:8,maxTotalBytes:10));
    }
}
