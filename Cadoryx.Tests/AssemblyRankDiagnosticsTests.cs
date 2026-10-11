using System.Reflection;
using Cadoryx.Commands;
using MathNet.Numerics.LinearAlgebra;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AssemblyRankDiagnosticsTests
{
    [Theory]
    [InlineData(1e-8,.9999)] [InlineData(1e-8,1.0001)]
    [InlineData(1e-8,.99999999999999)] [InlineData(1e-8,1.00000000000001)]
    [InlineData(1e-6,.9999)] [InlineData(1e-6,1.0001)]
    [InlineData(1e-6,.99999999999999)] [InlineData(1e-6,1.00000000000001)]
    public void IndependentBlocksUseWholeMatrixRankAndUncertaintyCutoffs(double threshold,double factor)
    {
        // Three identical columns in a separate block set the global largest
        // singular value to sqrt(3). A block-local cutoff gives the wrong rank.
        var matrix=new double[5,7];matrix[0,0]=1;matrix[0,1]=1;
        matrix[1,1]=Math.Sqrt(6)*threshold*factor;
        for(int col=2;col<5;col++)matrix[2,col]=1;
        AssertDenseAgreement(matrix);
        // Preserve the same diagnostic under row/column reordering and units.
        var permuted=new double[5,7];int[] columns=[4,0,6,2,1,5,3];
        for(int row=0;row<5;row++)for(int col=0;col<7;col++)
            permuted[4-row,col]=matrix[row,columns[col]]*(col%2==0?1e3:1e-3);
        AssertDenseAgreement(permuted);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ZeroAndCoupledColumnsRetainDenseDiagnosticsAndFullNullspace(int kind)
    {
        double[,] matrix=kind switch
        {
            0=>new double[4,6],
            1=>new double[,]{{1,0,0,0},{0,1,0,0}},
            2=>new double[,]{{1,1,0,0},{2,2,0,0},{0,0,1,0},{0,0,2,0}},
            _=>new double[,]{{1,2,3},{4,5,6},{7,8,10}}
        };
        AssertDenseAgreement(matrix);
        var full=Analyze(matrix,true);Assert.Equal(matrix.GetLength(1)-full.Rank,full.Modes.Length);
        foreach(var mode in full.Modes)
        {
            Assert.InRange(Math.Abs(Math.Sqrt(mode.Sum(v=>v*v))-1),0,1e-10);
            for(int row=0;row<matrix.GetLength(0);row++)
                Assert.InRange(Math.Abs(Enumerable.Range(0,mode.Length).Sum(col=>matrix[row,col]*mode[col])),0,1e-7);
        }
    }

    private static void AssertDenseAgreement(double[,] jacobian)
    {
        int rows=jacobian.GetLength(0),cols=jacobian.GetLength(1);
        var scale=Enumerable.Range(0,cols).Select(col=>
        {
            double squared=Enumerable.Range(0,rows).Sum(row=>jacobian[row,col]*jacobian[row,col]);
            return squared>1e-20?Math.Sqrt(squared):1;
        }).ToArray();
        var matrix=Matrix<double>.Build.Dense(rows,cols,(row,col)=>jacobian[row,col]/scale[col]);
        var values=matrix.Svd(false).S;double largest=values.Count==0?0:values[0];
        int rank=values.Count(value=>value>Math.Max(1e-10,largest*1e-8));
        bool uncertain=values.Count(value=>value>Math.Max(1e-10,largest*1e-6))!=rank;
        var actual=Analyze(jacobian,false);Assert.Equal(rank,actual.Rank);Assert.Equal(uncertain,actual.Uncertain);Assert.Empty(actual.Modes);
    }

    private static (int Rank,bool Uncertain,double[][] Modes) Analyze(double[,] matrix,bool nullspace)
    {
        var type=typeof(AssemblySolveCommands).Assembly.GetType("Cadoryx.Commands.AssemblyNumerics",true)!;
        var result=type.GetMethod("Analyze",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[matrix,nullspace])!;
        var analysis=result.GetType();
        return ((int)analysis.GetProperty("Rank")!.GetValue(result)!,
            (bool)analysis.GetProperty("Uncertain")!.GetValue(result)!,
            (double[][])analysis.GetProperty("Nullspace")!.GetValue(result)!);
    }
}
