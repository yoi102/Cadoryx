using MathNet.Numerics.LinearAlgebra;

namespace Cadoryx.Commands;

/// <summary>Scaled Jacobian SVD avoids squaring its condition number in normal equations.</summary>
internal static class AssemblyNumerics
{
    internal sealed record Analysis(int Rank,bool Uncertain,double[][] Nullspace);

    internal static double[] Step(double[,] jacobian,double[] residual,double damping)
    {
        int rows=jacobian.GetLength(0),cols=jacobian.GetLength(1);var scale=Scales(jacobian);
        var augmented=Matrix<double>.Build.Dense(rows+cols,cols);
        for(int row=0;row<rows;row++)for(int col=0;col<cols;col++)augmented[row,col]=jacobian[row,col]/scale[col];
        for(int col=0;col<cols;col++)augmented[rows+col,col]=Math.Sqrt(damping);
        var rhs=Vector<double>.Build.Dense(rows+cols);for(int row=0;row<rows;row++)rhs[row]=-residual[row];
        var solution=augmented.Svd(true).Solve(rhs);
        return Enumerable.Range(0,cols).Select(col=>solution[col]/scale[col]).ToArray();
    }

    internal static Analysis Analyze(double[,] jacobian,bool includeNullspace=true)
    {
        int rows=jacobian.GetLength(0),cols=jacobian.GetLength(1);
        if(cols==0)return new(0,false,[]);
        if(rows==0)return new(0,false,includeNullspace?Enumerable.Range(0,cols).Select(i=>Enumerable.Range(0,cols).Select(j=>i==j?1d:0d).ToArray()).ToArray():[]);
        var scale=Scales(jacobian);
        // Leave-one-relation-out diagnostics need singular values only. Exact
        // zero entries let independent blocks omit unrelated degrees of freedom.
        // All blocks still use the original column scales and global cutoffs.
        if(!includeNullspace&&TryAnalyzeBlocks(jacobian,scale,out var rankOnly))return rankOnly;
        var matrix=Matrix<double>.Build.Dense(rows,cols,(row,col)=>jacobian[row,col]/scale[col]);
        var svd=matrix.Svd(includeNullspace);double largest=svd.S.Count==0?0:svd.S[0];
        int Rank(double relative)=>svd.S.Count(value=>value>Math.Max(1e-10,largest*relative));
        int rank=Rank(1e-8);var modes=new List<double[]>();
        for(int row=rank;includeNullspace&&row<cols;row++)
        {
            var vector=Enumerable.Range(0,cols).Select(col=>svd.VT[row,col]/scale[col]).ToArray();
            double norm=Math.Sqrt(vector.Sum(value=>value*value));
            modes.Add(vector.Select(value=>value/norm).ToArray());
        }
        return new(rank,Rank(1e-6)!=rank,modes.ToArray());
    }

    private static bool TryAnalyzeBlocks(double[,] jacobian,double[] scale,out Analysis analysis)
    {
        int rows=jacobian.GetLength(0),cols=jacobian.GetLength(1);
        var parents=Enumerable.Range(0,cols).ToArray();
        int Root(int col)
        {
            while(parents[col]!=col){parents[col]=parents[parents[col]];col=parents[col];}
            return col;
        }
        var rowFirst=new int[rows];Array.Fill(rowFirst,-1);var active=new bool[cols];
        for(int row=0;row<rows;row++)for(int col=0;col<cols;col++)
        {
            double value=jacobian[row,col];
            if(!double.IsFinite(value)){analysis=new(0,true,[]);return false;}
            if(value==0)continue;
            active[col]=true;
            if(rowFirst[row]<0)rowFirst[row]=col;
            else parents[Root(col)]=Root(rowFirst[row]);
        }
        var columnGroups=new Dictionary<int,List<int>>();
        for(int col=0;col<cols;col++)if(active[col])
        {
            int key=Root(col);
            if(!columnGroups.TryGetValue(key,out var group))columnGroups.Add(key,group=[]);
            group.Add(col);
        }
        if(columnGroups.Count==0){analysis=new(0,false,[]);return true;}
        if(columnGroups.Count==1&&columnGroups.Values.Single().Count==cols)
        {analysis=new(0,false,[]);return false;}
        var rowGroups=columnGroups.Keys.ToDictionary(key=>key,_=>new List<int>());
        for(int row=0;row<rows;row++)if(rowFirst[row]>=0)rowGroups[Root(rowFirst[row])].Add(row);
        var singularValues=new List<double>();
        foreach(var entry in columnGroups)
        {
            var columns=entry.Value;var indices=rowGroups[entry.Key];
            var block=Matrix<double>.Build.Dense(indices.Count,columns.Count,
                (row,col)=>jacobian[indices[row],columns[col]]/scale[columns[col]]);
            singularValues.AddRange(block.Svd(false).S);
        }
        double largest=singularValues.Max();
        // Near either original cutoff, use the original whole-matrix SVD to
        // retain its floating-point boundary decision. This changes no cutoff.
        double roundoff=64*2.2204460492503131e-16*(rows+cols)*largest;
        double rankCutoff=Math.Max(1e-10,largest*1e-8),uncertainCutoff=Math.Max(1e-10,largest*1e-6);
        if(singularValues.Any(value=>Math.Abs(value-rankCutoff)<=roundoff||Math.Abs(value-uncertainCutoff)<=roundoff))
        {analysis=new(0,true,[]);return false;}
        int rank=singularValues.Count(value=>value>rankCutoff);
        analysis=new(rank,singularValues.Count(value=>value>uncertainCutoff)!=rank,[]);
        return true;
    }

    private static double[] Scales(double[,] jacobian)
    {
        int rows=jacobian.GetLength(0),cols=jacobian.GetLength(1);var result=new double[cols];
        for(int col=0;col<cols;col++)
        {
            double sum=0;for(int row=0;row<rows;row++)sum+=jacobian[row,col]*jacobian[row,col];
            result[col]=sum>1e-20?Math.Sqrt(sum):1;
        }
        return result;
    }
}
