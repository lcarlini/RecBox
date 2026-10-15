using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RecBox.Services;

internal static class NativeMethods
{
    public const int EnumCurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    public const uint WdaNone = 0x0;
    public const uint WdaExcludeFromCapture = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [Flags]
    public enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000
    }

    [DllImport("kernel32.dll")]
    public static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll")]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    public const int JobObjectExtendedLimitInformationClass = 9;
    public const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    /// <summary>Free bytes available to the current user on the volume holding <paramref name="folder"/>; -1 if unknown.</summary>
    public static long GetFreeBytes(string folder)
    {
        try
        {
            return GetDiskFreeSpaceEx(folder, out var free, out _, out _) ? (long)free : -1;
        }
        catch
        {
            return -1;
        }
    }
}

/// <summary>
/// Windows Job Object that kills child FFmpeg processes if RecBox itself dies,
/// so no orphaned encoder keeps running in the background.
/// </summary>
internal static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Handle = new(Create);

    private static IntPtr Create()
    {
        try
        {
            var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return IntPtr.Zero;

            var info = new NativeMethods.JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = { LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose }
            };
            NativeMethods.SetInformationJobObject(
                job,
                NativeMethods.JobObjectExtendedLimitInformationClass,
                ref info,
                Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>());
            return job;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static void Assign(Process process)
    {
        try
        {
            var job = Handle.Value;
            if (job != IntPtr.Zero)
                NativeMethods.AssignProcessToJobObject(job, process.Handle);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not attach FFmpeg to job object: {ex.Message}");
        }
    }
}
