using System.Collections.Immutable;
using Cadoryx.Db;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ParameterExpressionTests
{
    [Theory]
    [InlineData("2*(3 cm + 4 mm)",68,ParameterDimension.Length)]
    [InlineData("1 in + 1 ft",330.2,ParameterDimension.Length)]
    [InlineData("90 deg + pi * rad / 2",Math.PI,ParameterDimension.Angle)]
    [InlineData("sqrt(4 mm * 4 mm)",4,ParameterDimension.Length)]
    [InlineData("max(2 mm, abs(-5 mm))",5,ParameterDimension.Length)]
    [InlineData("sin(30 deg) * 10 mm",5,ParameterDimension.Length)]
    [InlineData("1e-2 * 2 m",20,ParameterDimension.Length)]
    [InlineData("2 + 3 * 4 / 2",8,ParameterDimension.Scalar)]
    public void ArithmeticUnitsAndFunctionsHaveExplicitDimensions(string expression,double expected,ParameterDimension dimension)
    {var value=ParameterExpressions.Evaluate(expression);Assert.Equal(expected,value.Value,9);Assert.True(value.HasDimension(dimension));}
    [Theory]
    [InlineData("1 mm + 1 deg")][InlineData("1 mm + 1")][InlineData("1 / 0")][InlineData("sqrt(1 mm)")]
    [InlineData("sin(1)")][InlineData("cos(1 mm)")][InlineData("min(1)")][InlineData("abs(1,2)")]
    [InlineData("unknown(1)")][InlineData("1e999")][InlineData("2 width")][InlineData("1;run()")]
    [InlineData("1.2.3")][InlineData("1 2")][InlineData("(1 + 2")]
    public void InvalidOrUnsafeExpressionsFail(string expression)=>Assert.Throws<CadValidationException>(()=>ParameterExpressions.Evaluate(expression));
    [Fact] public void BudgetsAndNamesAreBounded()
    {
        Assert.Throws<CadValidationException>(()=>ParameterExpressions.Evaluate(new string('(',65)+"1"+new string(')',65)));
        Assert.Throws<CadValidationException>(()=>ParameterExpressions.Evaluate(string.Join('+',Enumerable.Repeat("1",300))));
        Assert.False(ParameterExpressions.IsName("mm"));Assert.False(ParameterExpressions.IsName("SIN"));Assert.False(ParameterExpressions.IsName("2width"));Assert.True(ParameterExpressions.IsName("plate_width"));
        Assert.Equal(new[]{"Depth","width"},ParameterExpressions.Dependencies("max(width, Depth) + 2 mm").ToArray());
    }
    [Fact] public void DependenciesAreCaseInsensitiveAndCyclesShowTheActualChain()
    {
        var a=new CadParameter(Guid.NewGuid(),"Width",ParameterDimension.Length,"20 mm");var b=new CadParameter(Guid.NewGuid(),"Half",ParameterDimension.Length,"WIDTH / 2");
        var doc=DocumentSnapshot.Create("Parameters") with{Parameters=new([a,b],[])};Assert.Equal(10,ParameterGraph.Evaluate(doc)[b.Id].Value);
        doc=doc with{Parameters=new([a with{Expression="half"},b],[])};var error=Assert.Throws<CadValidationException>(()=>ParameterGraph.Evaluate(doc));Assert.Contains("Half -> Width -> Half",error.Message);
        Assert.Throws<CadValidationException>(()=>ParameterGraph.Evaluate(doc with{Parameters=new([a,b with{Name="width"}],[])}));
        Assert.Throws<CadValidationException>(()=>ParameterGraph.Evaluate(doc with{Parameters=new([a with{Expression="missing"}],[])}));
    }
    [Fact] public void DeepDependencyGraphAndNestedExpressionsDoNotMultiplyParserRecursion()
    {
        var parameters=Enumerable.Range(0,60).Select(i=>new CadParameter(Guid.NewGuid(),"p"+i,ParameterDimension.Scalar,
            new string('(',60)+(i==59?"1":"p"+(i+1))+new string(')',60))).ToImmutableArray();
        var doc=DocumentSnapshot.Create("Nested") with{Parameters=new(parameters,[])};Assert.All(ParameterGraph.Evaluate(doc).Values,v=>Assert.Equal(1,v.Value));
        var chain=Enumerable.Range(0,65).Select(i=>new CadParameter(Guid.NewGuid(),"p"+i,ParameterDimension.Scalar,i==64?"1":"p"+(i+1))).ToImmutableArray();
        Assert.Throws<CadValidationException>(()=>ParameterGraph.Evaluate(doc with{Parameters=new(chain,[])}));
    }
}
