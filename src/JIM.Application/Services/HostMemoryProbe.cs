// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Operations;

namespace JIM.Application.Services;

/// <summary>
/// Reads how much memory this process holds and how much it can still draw on. The kernel stops a container at its
/// cgroup memory limit, so a limit is read first (cgroup v2, then v1); without one, or with one larger than the host
/// can honour, the host's own figures from <c>/proc/meminfo</c> are read. Where neither is readable (a Windows
/// developer machine), the runtime's own view of physical memory is used.
/// </summary>
/// <remarks>
/// Available memory counts file cache the kernel can reclaim, as <c>MemAvailable</c> does: a container's usage
/// (<c>memory.current</c>) includes its page cache, and judging that as pressure would see a directory import's
/// read-ahead as a looming out-of-memory.
/// </remarks>
public sealed class HostMemoryProbe
{
    private const string CgroupV2Max = "/sys/fs/cgroup/memory.max";
    private const string CgroupV2Current = "/sys/fs/cgroup/memory.current";
    private const string CgroupV2Stat = "/sys/fs/cgroup/memory.stat";
    private const string CgroupV1Limit = "/sys/fs/cgroup/memory/memory.limit_in_bytes";
    private const string CgroupV1Usage = "/sys/fs/cgroup/memory/memory.usage_in_bytes";
    private const string CgroupV1Stat = "/sys/fs/cgroup/memory/memory.stat";
    private const string Meminfo = "/proc/meminfo";

    /// <summary>Any cgroup v1 limit at or above this (2^62 bytes) is the kernel's way of saying there is none.</summary>
    private const long CgroupV1NoLimit = 1L << 62;

    private readonly Func<string, string?> _readFile;
    private readonly Func<long> _processBytes;
    private readonly Func<(long TotalBytes, long LoadBytes)> _runtimeFigures;

    public HostMemoryProbe() : this(ReadFileOrNull, () => Environment.WorkingSet, ReadRuntimeFigures)
    {
    }

    /// <param name="readFile">Reads a file's text, or null when it cannot be read.</param>
    /// <param name="processBytes">This process's working set.</param>
    /// <param name="runtimeFigures">The runtime's physical memory total and load; zeros when it has none.</param>
    internal HostMemoryProbe(Func<string, string?> readFile, Func<long> processBytes, Func<(long TotalBytes, long LoadBytes)> runtimeFigures)
    {
        _readFile = readFile;
        _processBytes = processBytes;
        _runtimeFigures = runtimeFigures;
    }

    public MemoryReading Read()
    {
        var processBytes = _processBytes();
        var host = ReadHost();

        // A limit the host cannot honour is never reached: the host runs out first.
        var figures = ReadContainer() is { } container && (host == null || container.Total < host.Value.Total)
            ? container
            : host ?? ReadRuntime();

        return new MemoryReading { ProcessBytes = processBytes, TotalBytes = figures?.Total, AvailableBytes = figures?.Available };
    }

    /// <summary>The container's limit and what it has left, or null when no limit is set or it cannot be read.</summary>
    private (long Total, long Available)? ReadContainer()
    {
        var v2Max = _readFile(CgroupV2Max)?.Trim();
        if (v2Max != null)
        {
            // "max" is cgroup v2's word for no limit.
            return long.TryParse(v2Max, out var limit) && ParseLong(_readFile(CgroupV2Current)) is { } current
                ? Remaining(limit, current, StatValue(_readFile(CgroupV2Stat), "inactive_file"))
                : null;
        }

        // cgroup v1 states no limit as a page-rounded long.MaxValue rather than a word.
        return ParseLong(_readFile(CgroupV1Limit)) is { } v1Limit and < CgroupV1NoLimit && ParseLong(_readFile(CgroupV1Usage)) is { } usage
            ? Remaining(v1Limit, usage, StatValue(_readFile(CgroupV1Stat), "total_inactive_file"))
            : null;
    }

    private static (long Total, long Available) Remaining(long limit, long charged, long reclaimable) =>
        (limit, Math.Max(0, limit - Math.Max(0, charged - reclaimable)));

    /// <summary>The host's physical memory and what is available, from <c>/proc/meminfo</c>; null when it cannot be read.</summary>
    private (long Total, long Available)? ReadHost()
    {
        var meminfo = _readFile(Meminfo);
        return MeminfoKb(meminfo, "MemTotal") is { } totalKb && MeminfoKb(meminfo, "MemAvailable") is { } availableKb
            ? (totalKb * 1024, availableKb * 1024)
            : null;
    }

    private (long? Total, long? Available)? ReadRuntime()
    {
        var (total, load) = _runtimeFigures();
        return total > 0 ? (total, Math.Max(0, total - load)) : null;
    }

    private static long? MeminfoKb(string? meminfo, string key) =>
        meminfo?.Split('\n')
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim() == key)
            .Select(parts => ParseLong(parts[1].Replace("kB", string.Empty)))
            .FirstOrDefault();

    private static long StatValue(string? stat, string key) =>
        stat?.Split('\n')
            .Select(line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0] == key)
            .Select(parts => ParseLong(parts[1]))
            .FirstOrDefault() ?? 0;

    private static long? ParseLong(string? text) => long.TryParse(text?.Trim(), out var value) ? value : null;

    private static (long TotalBytes, long LoadBytes) ReadRuntimeFigures()
    {
        var info = GC.GetGCMemoryInfo();
        return (info.TotalAvailableMemoryBytes, info.MemoryLoadBytes);
    }

    private static string? ReadFileOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
