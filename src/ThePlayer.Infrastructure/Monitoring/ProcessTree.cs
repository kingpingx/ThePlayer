using System.Globalization;
using System.Runtime.InteropServices;

namespace ThePlayer.Infrastructure.Monitoring;

/// <summary>
/// Who is whose child, so that what a broadcast costs can include everything it spawned.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a real and thoroughly misleading measurement. On Windows with a
/// Chocolatey FFmpeg, the process this server starts is a <b>shim</b>: it launches the real
/// <c>ffmpeg.exe</c> as its own child and then does nothing but wait. Measuring the process we
/// started therefore reported <c>0.0%</c> for a transcode that was saturating a GPU - the most
/// interesting number in the system, confidently wrong, on the developer's own machine.
/// </para>
/// <para>
/// The shim is only the case that exposed it. FFmpeg spawns helpers of its own, and
/// <c>Kill(entireProcessTree: true)</c> is already used everywhere for the same reason, so the
/// subtree - not the process - was always the right unit. Cost is measured the way it is killed.
/// </para>
/// <para>
/// The parent map is built once per reading and thrown away. It is a snapshot of something that
/// changes underneath it, and caching it would attribute a dead process's children to whatever
/// later inherited its id.
/// </para>
/// </remarks>
public static class ProcessTree
{
    /// <summary>
    /// A process and everything descended from it, the root first.
    /// </summary>
    /// <remarks>
    /// Always contains at least <paramref name="rootProcessId"/>, even when the parent map cannot
    /// be read: measuring only the process we started is a poor answer, but it is a better one than
    /// measuring nothing.
    /// </remarks>
    public static IReadOnlyList<int> Descendants(int rootProcessId)
    {
        var parents = ReadParentMap();

        if (parents.Count == 0)
        {
            return [rootProcessId];
        }

        var childrenOf = new Dictionary<int, List<int>>();

        foreach (var (child, parent) in parents)
        {
            if (child == parent)
            {
                // The idle process is its own parent on Windows, and following that is a loop.
                continue;
            }

            if (!childrenOf.TryGetValue(parent, out var siblings))
            {
                siblings = [];
                childrenOf[parent] = siblings;
            }

            siblings.Add(child);
        }

        var found = new List<int> { rootProcessId };
        var seen = new HashSet<int> { rootProcessId };
        var pending = new Queue<int>();
        pending.Enqueue(rootProcessId);

        while (pending.Count > 0)
        {
            if (!childrenOf.TryGetValue(pending.Dequeue(), out var children))
            {
                continue;
            }

            foreach (var child in children.Where(seen.Add))
            {
                found.Add(child);
                pending.Enqueue(child);
            }
        }

        return found;
    }

    /// <summary>Every process id on the machine, mapped to its parent's.</summary>
    private static Dictionary<int, int> ReadParentMap()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ReadWindowsParentMap();
            }

            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ReadLinuxParentMap() : [];
        }
        catch (Exception)
        {
            // A missing map costs accuracy, not correctness - the caller falls back to the root
            // alone. It is never worth failing a metrics sample over.
            return [];
        }
    }

    /// <summary>
    /// The toolhelp snapshot, which is the only way to get parent ids without pulling in
    /// <c>System.Management</c> - a Windows-only package in a project that has otherwise stayed on
    /// the base class library.
    /// </summary>
    private static Dictionary<int, int> ReadWindowsParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);

        if (snapshot == InvalidHandle)
        {
            return map;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };

            if (!Process32First(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return map;
    }

    /// <summary>
    /// <c>/proc/&lt;pid&gt;/stat</c>, where the parent id is the fourth field.
    /// </summary>
    /// <remarks>
    /// Parsed from the last <c>)</c> rather than by splitting the whole line, because the second
    /// field is the executable name and a process is free to have spaces and brackets in it - which
    /// is exactly the sort of thing that turns up in production and never in a test.
    /// </remarks>
    private static Dictionary<int, int> ReadLinuxParentMap()
    {
        var map = new Dictionary<int, int>();

        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(directory);

            if (!int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            try
            {
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var afterName = stat.LastIndexOf(')');

                if (afterName < 0)
                {
                    continue;
                }

                var fields = stat[(afterName + 1)..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // fields[0] is the state character; the parent id follows it.
                if (fields.Length >= 2 &&
                    int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent))
                {
                    map[pid] = parent;
                }
            }
            catch (Exception)
            {
                // The process exited while /proc was being walked, which is ordinary.
            }
        }

        return map;
    }

    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>
    /// <c>CharSet.Unicode</c> is load-bearing rather than decorative.
    /// </summary>
    /// <remarks>
    /// Without it <c>ByValTStr</c> marshals as ANSI, so <c>Marshal.SizeOf</c> reports 260 bytes for
    /// the name instead of 520, <c>dwSize</c> goes out wrong, and <c>Process32FirstW</c> refuses
    /// with <c>ERROR_BAD_LENGTH</c> - silently, because the failure path here returns an empty map
    /// rather than throwing. The symptom was every process appearing to have no children at all.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
