using System.IO;

namespace Cadoryx.wpf.Diagnostics;

/// <summary>Keeps every diagnostic run's recovery and layout files inside its evidence directory.</summary>
internal static class DiagnosticPaths
{
    internal static string? OutputDirectory(IReadOnlyList<string> args)
    {
        if(args.Count>=4&&args[1] is "--m6-benchmark" or "--m14-open-benchmark")return Path.GetFullPath(args[3]);
        if(args.Count>=3&&(args[1]=="--smoke"||args[1].EndsWith("-smoke",StringComparison.Ordinal)||
            args[1] is "--recovery-seed" or "--recovery-verify"))return Path.GetFullPath(args[2]);
        return null;
    }
}
