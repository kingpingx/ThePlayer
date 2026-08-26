using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ThePlayer.Infrastructure.Processes;

/// <summary>
/// Makes child processes die when this one does, including when it is killed outright.
/// </summary>
/// <remarks>
/// <para>
/// A gap since Phase 0, and a visible one: <c>taskkill /F</c> on the server left MediaMTX running
/// and holding ports 8554, 8889 and 9997, so the next start found them taken. The supervisor
/// already adopts a survivor, which made it a nuisance rather than a failure - but adoption is a
/// recovery, not a fix, and it does nothing for the FFmpeg processes.
/// </para>
/// <para>
/// <b>Ordinary cleanup cannot solve this.</b> Every pipeline already kills its child on dispose.
/// None of that runs on <c>SIGKILL</c> or <c>taskkill /F</c>, which is exactly when orphans appear.
/// The fix has to be something the operating system enforces without our participation.
/// </para>
/// <para>
/// The two platforms offer that guarantee in different shapes, and the difference is <em>when</em>
/// it can be arranged rather than merely how:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Windows</b> - a Job Object with <c>KILL_ON_JOB_CLOSE</c>. A process is assigned to it
/// <see cref="Adopt">after</see> it starts, and when the last handle to the job closes - which
/// happens when this process ends, however it ends - the kernel terminates everything in it.
/// </item>
/// <item>
/// <b>Linux</b> - <c>PR_SET_PDEATHSIG</c>, which must be set <em>by the child</em>, between fork
/// and exec. .NET exposes no hook there, so it is arranged <see cref="Prepare">before</see> the
/// start instead, by launching through <c>setpriv</c>.
/// </item>
/// </list>
/// </remarks>
public interface IChildProcessGuard
{
    /// <summary>
    /// Adjusts how a child will be launched, where the guarantee has to be arranged before it runs.
    /// </summary>
    /// <returns>The same start info, modified in place, so a caller can start it as usual.</returns>
    ProcessStartInfo Prepare(ProcessStartInfo startInfo);

    /// <summary>
    /// Binds a running child to this process's lifetime, where that can be done after the fact.
    /// </summary>
    /// <remarks>
    /// Never throws. A child that could not be adopted is a child that may outlive a hard kill,
    /// which is worth a log line and never worth failing a broadcast over.
    /// </remarks>
    void Adopt(Process process);
}

/// <summary>Chooses the mechanism this operating system offers, once.</summary>
public static class ChildProcessGuards
{
    public static IChildProcessGuard For(ILoggerFactory loggerFactory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new JobObjectGuard(loggerFactory.CreateLogger<JobObjectGuard>());
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new ParentDeathSignalGuard(loggerFactory.CreateLogger<ParentDeathSignalGuard>());
        }

        return new UnguardedChildren(loggerFactory.CreateLogger<UnguardedChildren>());
    }
}

/// <summary>
/// Windows: everything launched joins a Job Object that the kernel empties when we exit.
/// </summary>
/// <remarks>
/// The handle is deliberately never closed. Its lifetime <em>is</em> the guarantee: the kernel
/// terminates the job's members when the last handle to it goes away, and the last handle going
/// away is precisely what happens when this process ends - cleanly, on a crash, or under
/// <c>taskkill /F</c> alike.
/// </remarks>
internal sealed class JobObjectGuard : IChildProcessGuard
{
    private readonly ILogger _logger;
    private readonly IntPtr _job;

    public JobObjectGuard(ILogger<JobObjectGuard> logger)
    {
        _logger = logger;
        _job = CreateJob(logger);
    }

    public ProcessStartInfo Prepare(ProcessStartInfo startInfo) => startInfo;

    public void Adopt(Process process)
    {
        if (_job == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (!AssignProcessToJobObject(_job, process.Handle))
            {
                _logger.LogDebug(
                    "Could not add process {ProcessId} to the job object (error {Error}). " +
                    "It may outlive a hard kill of this server.",
                    process.Id,
                    Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            // The child exited between the check and the assignment, which is ordinary.
            _logger.LogDebug(ex, "Could not adopt a child process.");
        }
    }

    private static IntPtr CreateJob(ILogger logger)
    {
        var job = CreateJobObject(IntPtr.Zero, null);

        if (job == IntPtr.Zero)
        {
            logger.LogWarning(
                "Could not create a job object (error {Error}). Child processes will not be " +
                "killed automatically if this server is terminated abruptly.",
                Marshal.GetLastWin32Error());

            return IntPtr.Zero;
        }

        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation
            {
                LimitFlags = LimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(job, ExtendedLimitInformationClass, buffer, (uint)size))
            {
                logger.LogWarning(
                    "Could not set kill-on-close on the job object (error {Error}).",
                    Marshal.GetLastWin32Error());

                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        logger.LogDebug("Child processes will be terminated with this server.");
        return job;
    }

    private const int ExtendedLimitInformationClass = 9;
    private const uint LimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int infoClass,
        IntPtr info,
        uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Linux: children are launched through <c>setpriv</c>, which sets their parent-death signal.
/// </summary>
/// <remarks>
/// <para>
/// <c>PR_SET_PDEATHSIG</c> has to be set by the child itself after <c>fork</c> and before
/// <c>exec</c>, and .NET's <c>Process.Start</c> offers no hook in that window. <c>setpriv</c> from
/// util-linux does exactly that and then execs the real command, which is why the wrapper is worth
/// a process rather than writing a launcher of our own.
/// </para>
/// <para>
/// The wrapper does not disturb anything else. It <c>exec</c>s in place, so the process id we hold
/// is the child's; and per-broadcast metrics have measured the whole subtree since Phase 4, so a
/// wrapper would not have distorted them even if it lingered.
/// </para>
/// <para>
/// Where <c>setpriv</c> is missing this reports once and does nothing further. In a container the
/// point is close to moot - the app is PID 1 and the whole namespace goes when it does - which is
/// exactly the deployment this project targets on Linux.
/// </para>
/// </remarks>
internal sealed class ParentDeathSignalGuard(ILogger<ParentDeathSignalGuard> logger) : IChildProcessGuard
{
    private static readonly string[] SearchPath = ["/usr/bin/setpriv", "/bin/setpriv", "/usr/local/bin/setpriv"];

    private readonly string? _setpriv = SearchPath.FirstOrDefault(File.Exists);
    private bool _warned;

    public ProcessStartInfo Prepare(ProcessStartInfo startInfo)
    {
        if (_setpriv is null)
        {
            WarnOnce();
            return startInfo;
        }

        // The original command becomes an argument of the wrapper, which execs it in place. The
        // existing arguments are carried through untouched rather than re-parsed: they are built
        // as one already-quoted string everywhere in this codebase, and taking them apart to put
        // them back together is how quoting bugs get introduced.
        var original = startInfo.FileName;

        startInfo.FileName = _setpriv;
        startInfo.Arguments = $"--pdeathsig SIGKILL -- \"{original}\" {startInfo.Arguments}".TrimEnd();

        return startInfo;
    }

    public void Adopt(Process process)
    {
        // Already arranged at launch. Nothing to do after the fact.
    }

    private void WarnOnce()
    {
        if (_warned)
        {
            return;
        }

        _warned = true;

        logger.LogWarning(
            "setpriv was not found, so child processes will not be killed automatically if this " +
            "server is terminated abruptly. Install util-linux, or run in a container where the " +
            "process namespace does this instead.");
    }
}

/// <summary>Everywhere else: says so once, and does nothing.</summary>
/// <remarks>
/// A null object rather than a silent no-op. macOS has no equivalent primitive, and a deployment
/// that believes it is protected when it is not is worse off than one that knows.
/// </remarks>
internal sealed class UnguardedChildren(ILogger<UnguardedChildren> logger) : IChildProcessGuard
{
    private bool _warned;

    public ProcessStartInfo Prepare(ProcessStartInfo startInfo)
    {
        if (!_warned)
        {
            _warned = true;

            logger.LogWarning(
                "This platform offers no way to guarantee child processes die with their parent. " +
                "A hard kill of this server may leave FFmpeg or MediaMTX running.");
        }

        return startInfo;
    }

    public void Adopt(Process process)
    {
    }
}
