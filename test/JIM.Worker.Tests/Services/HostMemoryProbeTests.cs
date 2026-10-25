// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Where a JIM process reads how much memory it can draw on: its container's cgroup limit when one is set, since the
/// kernel stops the container there, otherwise the host's physical memory; and how much of it is still available,
/// counting file cache the kernel can reclaim as available, as the kernel itself does.
/// </summary>
[TestFixture]
public class HostMemoryProbeTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private const string HostMeminfo = """
        MemTotal:       62292208 kB
        MemFree:         1000000 kB
        MemAvailable:    4194304 kB
        Buffers:          100000 kB
        """;

    [Test]
    public void Read_NoContainerLimit_ReadsTheHostFromMeminfo()
    {
        var probe = Probe(new()
        {
            ["/sys/fs/cgroup/memory.max"] = "max\n",
            ["/proc/meminfo"] = HostMeminfo
        });

        var reading = probe.Read();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.TotalBytes, Is.EqualTo(62292208L * 1024));
            Assert.That(reading.AvailableBytes, Is.EqualTo(4L * Gb));
            Assert.That(reading.ProcessBytes, Is.EqualTo(3 * Gb));
        }
    }

    [Test]
    public void Read_ContainerLimitSet_ReadsTheLimitAndWhatTheContainerUsesExcludingReclaimableCache()
    {
        var probe = Probe(new()
        {
            ["/sys/fs/cgroup/memory.max"] = $"{8 * Gb}\n",
            ["/sys/fs/cgroup/memory.current"] = $"{7 * Gb}\n",
            ["/sys/fs/cgroup/memory.stat"] = $"anon {5 * Gb}\nfile {2 * Gb}\ninactive_file {Gb}\nactive_file {Gb}\n",
            ["/proc/meminfo"] = HostMeminfo
        });

        var reading = probe.Read();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.TotalBytes, Is.EqualTo(8 * Gb), "the kernel stops the container at its limit, not at the host's memory");
            Assert.That(reading.AvailableBytes, Is.EqualTo(2 * Gb), "7 GB charged, 1 GB of it inactive file cache the kernel reclaims first");
        }
    }

    [Test]
    public void Read_ContainerLimitAboveTheHostsMemory_ReadsTheHost()
    {
        var probe = Probe(new()
        {
            ["/sys/fs/cgroup/memory.max"] = $"{512 * Gb}\n",
            ["/sys/fs/cgroup/memory.current"] = $"{Gb}\n",
            ["/sys/fs/cgroup/memory.stat"] = "inactive_file 0\n",
            ["/proc/meminfo"] = HostMeminfo
        });

        Assert.That(probe.Read().TotalBytes, Is.EqualTo(62292208L * 1024), "a limit the host cannot honour is never reached; the host runs out first");
    }

    [Test]
    public void Read_ContainerUsingMoreThanItsLimit_ReportsNothingAvailable()
    {
        var probe = Probe(new()
        {
            ["/sys/fs/cgroup/memory.max"] = $"{4 * Gb}\n",
            ["/sys/fs/cgroup/memory.current"] = $"{5 * Gb}\n",
            ["/sys/fs/cgroup/memory.stat"] = "inactive_file 0\n"
        });

        Assert.That(probe.Read().AvailableBytes, Is.Zero);
    }

    [Test]
    public void Read_CgroupV1LimitSet_ReadsTheLimitAndUsage()
    {
        var probe = Probe(new()
        {
            ["/sys/fs/cgroup/memory/memory.limit_in_bytes"] = $"{8 * Gb}\n",
            ["/sys/fs/cgroup/memory/memory.usage_in_bytes"] = $"{6 * Gb}\n",
            ["/sys/fs/cgroup/memory/memory.stat"] = $"total_inactive_file {Gb}\n",
            ["/proc/meminfo"] = HostMeminfo
        });

        var reading = probe.Read();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.TotalBytes, Is.EqualTo(8 * Gb));
            Assert.That(reading.AvailableBytes, Is.EqualTo(3 * Gb));
        }
    }

    [Test]
    public void Read_CgroupV1Unlimited_ReadsTheHost()
    {
        // cgroup v1 states "no limit" as a page-rounded long.MaxValue rather than a word.
        var probe = Probe(new()
        {
            ["/sys/fs/cgroup/memory/memory.limit_in_bytes"] = "9223372036854771712\n",
            ["/sys/fs/cgroup/memory/memory.usage_in_bytes"] = $"{6 * Gb}\n",
            ["/proc/meminfo"] = HostMeminfo
        });

        Assert.That(probe.Read().AvailableBytes, Is.EqualTo(4L * Gb));
    }

    [Test]
    public void Read_NothingReadable_FallsBackToTheRuntimesOwnFigures()
    {
        var probe = new HostMemoryProbe(_ => null, () => 3 * Gb, () => (16 * Gb, 12 * Gb));

        var reading = probe.Read();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.TotalBytes, Is.EqualTo(16 * Gb));
            Assert.That(reading.AvailableBytes, Is.EqualTo(4 * Gb));
        }
    }

    [Test]
    public void Read_RuntimeFiguresUnknown_LeavesTotalAndAvailableUnknown()
    {
        var probe = new HostMemoryProbe(_ => null, () => 3 * Gb, () => (0, 0));

        var reading = probe.Read();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.TotalBytes, Is.Null);
            Assert.That(reading.AvailableBytes, Is.Null);
            Assert.That(reading.UsedFraction, Is.Null);
            Assert.That(reading.ProcessBytes, Is.EqualTo(3 * Gb));
        }
    }

    [Test]
    public void Read_MeminfoMissingMemAvailable_FallsBackToTheRuntimesOwnFigures()
    {
        var probe = new HostMemoryProbe(path => path == "/proc/meminfo" ? "MemTotal: 1000 kB\n" : null, () => Gb, () => (16 * Gb, 4 * Gb));

        Assert.That(probe.Read().TotalBytes, Is.EqualTo(16 * Gb));
    }

    [Test]
    public void UsedFraction_IsTheShareOfTheTotalNotAvailable()
    {
        var probe = Probe(new() { ["/proc/meminfo"] = "MemTotal: 1000 kB\nMemAvailable: 250 kB\n" });

        Assert.That(probe.Read().UsedFraction, Is.EqualTo(0.75).Within(1e-9));
    }

    private static HostMemoryProbe Probe(Dictionary<string, string> files) =>
        new(path => files.GetValueOrDefault(path), () => 3 * Gb, () => (0, 0));
}
