using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cadoryx.wpf.Services.IO;

/// <summary>Windows also terminates the owned reader if the parent exits unexpectedly.</summary>
internal sealed class ImportWorkerJob : IDisposable
{
    private readonly SafeFileHandle handle;
    public ImportWorkerJob()
    {
        handle=CreateJobObject(0,null);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastPInvokeError());
        var info=new ExtendedLimitInformation{BasicLimitInformation=new(){LimitFlags=0x2000}};
        if(!SetInformationJobObject(handle,9,in info,(uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {int error=Marshal.GetLastPInvokeError();handle.Dispose();throw new Win32Exception(error);}
    }
    public void Assign(Process process)
    {
        if(!AssignProcessToJobObject(handle,process.Handle))throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    public void Dispose()=>handle.Dispose();
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit,PerJobUserTimeLimit;public uint LimitFlags;
        public nuint MinimumWorkingSetSize,MaximumWorkingSetSize;public uint ActiveProcessLimit;
        public nuint Affinity;public uint PriorityClass,SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters
    {public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;public IoCounters IoInfo;
        public nuint ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObject(nint attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int infoClass,in ExtendedLimitInformation info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,nint process);
}
