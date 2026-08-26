using System.Diagnostics;
using ThePlayer.Infrastructure.Monitoring;

namespace ThePlayer.Infrastructure.Tests.Monitoring;

/// <summary>
/// Finding everything a pipeline spawned.
/// </summary>
/// <remarks>
/// This is here because of a measurement that was confidently wrong. On Windows with a Chocolatey
/// FFmpeg, the process the server starts is a shim that launches the real one as a child and then
/// idles - so a per-broadcast CPU figure taken from the root alone read <c>0.0%</c> while a GPU
/// transcode ran. These tests spawn a real child, because a fake parent map would have agreed with
/// the broken implementation.
/// </remarks>
public class ProcessTreeTests
{
    [Fact]
    public void The_root_is_always_included()
    {
        ProcessTree.Descendants(Environment.ProcessId).Should().Contain(Environment.ProcessId);
    }

    [Fact]
    public void A_process_id_that_does_not_exist_still_answers_with_itself()
    {
        // The caller then finds nothing readable and reports no figure. Answering with an empty
        // list here would be a second way of saying the same thing, and one more branch to get
        // wrong at the call site.
        ProcessTree.Descendants(-1).Should().ContainSingle().Which.Should().Be(-1);
    }

    [Fact]
    public async Task A_child_process_is_found()
    {
        // The case the whole file exists for.
        using var child = StartWaitingChild();

        try
        {
            // The parent map is a snapshot of the OS, and a process is not in it until it is.
            await WaitForVisibilityAsync(child.Id);

            var descendants = ProcessTree.Descendants(Environment.ProcessId);

            descendants.Should().Contain(child.Id, "a child of this process is part of its cost");
            descendants.Should().Contain(Environment.ProcessId);
        }
        finally
        {
            Stop(child);
        }
    }

    [Fact]
    public async Task A_grandchild_is_found_too()
    {
        // FFmpeg's shim is a child; the shim's own helpers would be grandchildren. Walking one
        // level would have fixed the observed bug and left the next one in place.
        using var child = StartWaitingChild();

        try
        {
            await WaitForVisibilityAsync(child.Id);

            // Everything under this process, reached from this process, must include the child -
            // which is only true if the walk recurses rather than reading one level.
            var fromRoot = ProcessTree.Descendants(Environment.ProcessId);
            var fromChild = ProcessTree.Descendants(child.Id);

            fromChild.Should().Contain(child.Id);
            fromRoot.Should().Contain(fromChild);
        }
        finally
        {
            Stop(child);
        }
    }

    /// <summary>A child that stays alive until it is killed, on either platform.</summary>
    private static Process StartWaitingChild()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c pause")
            : new ProcessStartInfo("sh", "-c \"read line\"");

        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        var process = Process.Start(startInfo);
        process.Should().NotBeNull();

        return process!;
    }

    private static async Task WaitForVisibilityAsync(int processId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (ProcessTree.Descendants(Environment.ProcessId).Contains(processId))
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
    }
}
