using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vintagestory.API.Config;

/// <summary>
/// Render-thread frame pacing with a Windows high-resolution waitable timer.
/// The caller retains its existing sleep path when this timer is unavailable.
/// </summary>
public static class OptimumFrameWait
{
    private const uint HighResolutionTimer = 0x2;
    private const uint SynchronizeAndModify = 0x00100002;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeoutMs = 250;

    private static readonly SafeWaitHandle Timer = CreateTimer();
    private static volatile bool failed;

    public static bool IsSupported => !failed && !Timer.IsInvalid;

    public static bool TryWait(Stopwatch frameStopwatch, long targetTicks)
    {
        if (!IsSupported) return false;

        try
        {
            long remainingTicks;
            while ((remainingTicks = targetTicks - frameStopwatch.ElapsedTicks) > 0)
            {
                // Negative due times are relative, in 100 ns units. Round up so
                // the timer cannot be armed for zero or an earlier deadline.
                long dueTime = -Math.Max(1L, (long)Math.Ceiling(
                    remainingTicks * (10_000_000.0 / Stopwatch.Frequency)));
                if (!SetWaitableTimerEx(Timer, ref dueTime, 0, IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, 0) ||
                    WaitForSingleObject(Timer, WaitTimeoutMs) != WaitObject0)
                {
                    failed = true;
                    return false;
                }
            }
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            failed = true;
            return false;
        }
    }

    private static SafeWaitHandle CreateTimer()
    {
        if (!OperatingSystem.IsWindows()) return new SafeWaitHandle(IntPtr.Zero, true);
        try
        {
            return CreateWaitableTimerExW(IntPtr.Zero, null, HighResolutionTimer,
                SynchronizeAndModify);
        }
        catch (EntryPointNotFoundException)
        {
            return new SafeWaitHandle(IntPtr.Zero, true);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes,
        string name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", EntryPoint = "SetWaitableTimerEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer,
        ref long dueTime, int period, IntPtr completionRoutine,
        IntPtr completionContext, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject")]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint timeoutMs);
}
