using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ThePlayer.Application;
using ThePlayer.Domain.Monitoring;

namespace ThePlayer.Infrastructure.Monitoring;

/// <summary>
/// System-wide CPU and memory, read the way each operating system offers them.
/// </summary>
/// <remarks>
/// <para>
/// <b>CPU is a rate, and a rate needs two samples.</b> Every implementation here therefore returns
/// null on its first call rather than a number, because there is nothing to subtract from yet.
/// Inventing a figure for the first reading would put a fabricated value at the top of a panel
/// whose whole purpose is to be believed.
/// </para>
/// <para>
/// Windows reads <c>GetSystemTimes</c> and Linux reads <c>/proc/stat</c>. Both give the same three
/// counters - idle, kernel, user - as monotonic totals, so the arithmetic below is shared and only
/// the reading differs. The roadmap said "performance counters" for Windows; this is the same
/// information without the <c>System.Diagnostics.PerformanceCounter</c> package, which is
/// Windows-only, needs a warm-up call before its first figure means anything, and would be a
/// dependency in a project that has otherwise stayed on the base class library.
/// </para>
/// </remarks>
public sealed class SystemMetricsReader(ILogger<SystemMetricsReader> logger) : ISystemMetricsReader
{
    private readonly object _gate = new();
    private CpuTimes? _previous;

    public Task<SystemUtilisation> ReadAsync(CancellationToken cancellationToken = default)
    {
        var times = ReadCpuTimes();
        var memory = ReadMemory();

        double? cpuPercent = null;

        if (times is { } current)
        {
            lock (_gate)
            {
                cpuPercent = _previous is { } previous ? Compare(previous, current) : null;
                _previous = current;
            }
        }

        return Task.FromResult(new SystemUtilisation(cpuPercent, memory.UsedBytes, memory.TotalBytes));
    }

    /// <summary>
    /// The share of the interval that was not spent idle.
    /// </summary>
    /// <remarks>
    /// Counters are monotonic and can wrap or be reset; an interval that appears to have gone
    /// backwards produces no reading rather than a nonsense one.
    /// </remarks>
    private static double? Compare(CpuTimes previous, CpuTimes current)
    {
        var idle = current.Idle - previous.Idle;
        var total = current.Total - previous.Total;

        if (total <= 0 || idle < 0)
        {
            return null;
        }

        var busy = 100d * (total - idle) / total;
        return Math.Clamp(Math.Round(busy, 1), 0, 100);
    }

    private CpuTimes? ReadCpuTimes()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ReadWindowsCpuTimes();
            }

            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ReadLinuxCpuTimes() : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read system CPU times.");
            return null;
        }
    }

    /// <summary>
    /// <c>GetSystemTimes</c> reports kernel time <em>including</em> idle, which is the detail worth
    /// stating: adding all three together double-counts the idle portion and produces a CPU figure
    /// that is quietly too low whenever the machine is quiet.
    /// </summary>
    private static CpuTimes? ReadWindowsCpuTimes()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return null;
        }

        var idle = ToTicks(idleTime);
        var kernel = ToTicks(kernelTime);
        var user = ToTicks(userTime);

        return new CpuTimes(idle, kernel + user);
    }

    /// <summary>
    /// The first line of <c>/proc/stat</c>: <c>cpu user nice system idle iowait irq softirq ...</c>,
    /// in jiffies. <c>iowait</c> counts as idle, because a core waiting on a disk is not doing work.
    /// </summary>
    private static CpuTimes? ReadLinuxCpuTimes()
    {
        var line = File.ReadLines("/proc/stat").FirstOrDefault();

        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return null;
        }

        var fields = line
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(field => long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0L)
            .ToList();

        if (fields.Count < 5)
        {
            return null;
        }

        var idle = fields[3] + fields[4];
        return new CpuTimes(idle, fields.Sum());
    }

    private MemoryReading ReadMemory()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ReadWindowsMemory();
            }

            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? ReadLinuxMemory()
                : default;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read system memory.");
            return default;
        }
    }

    private static MemoryReading ReadWindowsMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };

        if (!GlobalMemoryStatusEx(ref status))
        {
            return default;
        }

        var total = (long)status.TotalPhys;
        return new MemoryReading(total - (long)status.AvailPhys, total);
    }

    /// <summary>
    /// <c>MemAvailable</c> rather than <c>MemFree</c>: free memory on Linux excludes the page cache,
    /// so a healthy machine reports almost none of it and "used" would read as near 100% forever.
    /// </summary>
    private static MemoryReading ReadLinuxMemory()
    {
        long total = 0;
        long available = 0;

        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = ParseKilobytes(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = ParseKilobytes(line);
            }

            if (total > 0 && available > 0)
            {
                break;
            }
        }

        return total > 0 ? new MemoryReading(total - available, total) : default;
    }

    private static long ParseKilobytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 &&
               long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value * 1024
            : 0;
    }

    /// <param name="Idle">Monotonic idle time, in whatever unit the platform counts.</param>
    /// <param name="Total">Monotonic total time, in the same unit.</param>
    private readonly record struct CpuTimes(long Idle, long Total);

    private readonly record struct MemoryReading(long? UsedBytes, long? TotalBytes);

    private static long ToTicks(FileTime time) => ((long)time.High << 32) | (uint)time.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public int Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
