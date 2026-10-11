using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cadoryx.Kernel.Occt;

/// <summary>Only this operation's native worker belongs to the kill-on-close job.</summary>
internal sealed class MetricThreadWorkerJob:IDisposable
{
    private readonly SafeFileHandle handle;
    internal MetricThreadWorkerJob()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("The bundled native thread worker requires Windows.");
        handle=CreateJobObject(0,null);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastPInvokeError());
        var info=new ExtendedLimitInformation{Basic=new(){LimitFlags=0x2000}};
        if(!SetInformationJobObject(handle,9,in info,(uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {int error=Marshal.GetLastPInvokeError();handle.Dispose();throw new Win32Exception(error);}
    }
    internal void Assign(Process process)
    {if(!AssignProcessToJobObject(handle,process.Handle))throw new Win32Exception(Marshal.GetLastPInvokeError());}
    public void Dispose()=>handle.Dispose();
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimitInformation
    {
        public long ProcessTime,JobTime;public uint LimitFlags;public nuint MinimumWorkingSet,MaximumWorkingSet;
        public uint ActiveProcessLimit;public nuint Affinity;public uint PriorityClass,SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters
    {public ulong ReadOperations,WriteOperations,OtherOperations,ReadTransfer,WriteTransfer,OtherTransfer;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimitInformation
    {public BasicLimitInformation Basic;public IoCounters Io;public nuint ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObject(nint attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,in ExtendedLimitInformation info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,nint process);
}
