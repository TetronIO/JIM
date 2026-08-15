// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Verifies the offset/count log-entry read (<see cref="LogReaderService.GetLogEntriesRangeAsync"/>) that backs the
/// virtualised (infinite-scroll) Logs page. The log store is a directory of per-service, per-day files rather than a
/// database table, so unlike the Metaverse Object header range tests these need no PostgreSQL instance: each test
/// writes real log files into a temporary directory and exercises the windowing, total-count and clamp semantics
/// against them directly.
/// </summary>
[TestFixture]
public class LogReaderServiceRangeTests
{
    private string _testLogPath = null!;
    private LogReaderService _service = null!;
    private DateTime _today;

    [SetUp]
    public void SetUp()
    {
        _testLogPath = Path.Combine(Path.GetTempPath(), $"jim-test-logs-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testLogPath);
        _service = new LogReaderService(_testLogPath);
        _today = DateTime.UtcNow.Date;
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testLogPath))
            Directory.Delete(_testLogPath, true);
    }

    /// <summary>
    /// Writes <paramref name="count"/> entries named "Message 001", "Message 002", ... into today's log file for
    /// <paramref name="service"/>, with strictly ascending timestamps so "Message {count}" is the newest entry and
    /// therefore the first row of an unfiltered window at offset zero.
    /// </summary>
    private async Task WriteLogFileAsync(int count, string service = "web", string level = "Information")
    {
        var baseTime = new DateTime(_today.Year, _today.Month, _today.Day, 1, 0, 0, DateTimeKind.Utc);
        var lines = Enumerable.Range(1, count)
            .Select(i => $"{{\"@t\":\"{baseTime.AddSeconds(i):yyyy-MM-ddTHH:mm:ssZ}\",\"@l\":\"{level}\",\"@m\":\"Message {i:D3}\"}}");
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.{service}.{_today:yyyyMMdd}.log"),
            string.Join("\n", lines));
    }

    [Test]
    public async Task Range_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        await WriteLogFileAsync(10);

        var result = await _service.GetLogEntriesRangeAsync(date: _today, startIndex: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            // Newest first, so the leading window holds the highest-numbered messages.
            Assert.That(result.Results.Select(r => r.Message), Is.EqualTo(new[] { "Message 010", "Message 009", "Message 008" }));
        }
    }

    [Test]
    public async Task Range_MidWindow_ReturnsSliceAtAbsoluteOffsetAsync()
    {
        await WriteLogFileAsync(10);

        var result = await _service.GetLogEntriesRangeAsync(date: _today, startIndex: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(r => r.Message), Is.EqualTo(new[] { "Message 007", "Message 006", "Message 005" }));
        }
    }

    [Test]
    public async Task Range_WindowStraddlingEnd_ReturnsRemainderAsync()
    {
        await WriteLogFileAsync(10);

        var result = await _service.GetLogEntriesRangeAsync(date: _today, startIndex: 9, count: 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(r => r.Message), Is.EqualTo(new[] { "Message 001" }));
        }
    }

    [Test]
    public async Task Range_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        await WriteLogFileAsync(10);

        var result = await _service.GetLogEntriesRangeAsync(date: _today, startIndex: 100, count: 10);

        using (Assert.EnterMultipleScope())
        {
            // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task Range_CountAboveCap_ClampsToFiveHundredAsync()
    {
        await WriteLogFileAsync(505);

        var result = await _service.GetLogEntriesRangeAsync(date: _today, startIndex: 0, count: 1000);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound the cost of a single read, while the total still reflects every
            // matching entry. See the cap's own comment in LogReaderService for how 500 was derived.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(LogReaderService.MaximumLogEntryWindowSize));
        }
    }

    [Test]
    public async Task Range_CountBelowOne_ThrowsAsync()
    {
        await WriteLogFileAsync(3);

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.GetLogEntriesRangeAsync(date: _today, startIndex: 0, count: 0));
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsWindowWithNoTotalAsync()
    {
        await WriteLogFileAsync(10);

        var result = await _service.GetLogEntriesRangeAsync(date: _today, startIndex: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the caller did not ask for the count, and zero would read as "nothing matches",
            // which is the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(r => r.Message), Is.EqualTo(new[] { "Message 007", "Message 006", "Message 005" }));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsSameWindowAsCountedReadAsync()
    {
        await WriteLogFileAsync(10);

        // Skipping the count must change what the caller is told about the total and nothing else; the window itself
        // comes from the same filtered, sorted match set either way.
        var counted = await _service.GetLogEntriesRangeAsync(date: _today, startIndex: 5, count: 4);
        var uncounted = await _service.GetLogEntriesRangeAsync(date: _today, startIndex: 5, count: 4, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(r => r.Message), Is.EqualTo(counted.Results.Select(r => r.Message)));
        }
    }

    [Test]
    public async Task Range_ServiceFilter_RestrictsWindowAndTotalAsync()
    {
        await WriteLogFileAsync(5, service: "web");
        await WriteLogFileAsync(3, service: "worker");

        var result = await _service.GetLogEntriesRangeAsync(service: "worker", date: _today, startIndex: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(3));
            Assert.That(result.Results.Select(r => r.Service), Is.All.EqualTo("worker"));
        }
    }

    [Test]
    public async Task Range_LevelFilter_RestrictsWindowAndTotalAsync()
    {
        var baseTime = new DateTime(_today.Year, _today.Month, _today.Day, 1, 0, 0, DateTimeKind.Utc);
        var lines = string.Join("\n",
            $"{{\"@t\":\"{baseTime.AddSeconds(1):yyyy-MM-ddTHH:mm:ssZ}\",\"@l\":\"Debug\",\"@m\":\"Debug message\"}}",
            $"{{\"@t\":\"{baseTime.AddSeconds(2):yyyy-MM-ddTHH:mm:ssZ}\",\"@l\":\"Warning\",\"@m\":\"Warning message\"}}",
            $"{{\"@t\":\"{baseTime.AddSeconds(3):yyyy-MM-ddTHH:mm:ssZ}\",\"@l\":\"Error\",\"@m\":\"Error message\"}}");
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, $"jim.web.{_today:yyyyMMdd}.log"), lines);

        var result = await _service.GetLogEntriesRangeAsync(levels: ["Warning", "Error"], date: _today, startIndex: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(r => r.Level), Is.EqualTo(new[] { "Error", "Warning" }));
        }
    }

    [Test]
    public async Task Range_SearchFilter_RestrictsWindowAndTotalAsync()
    {
        await WriteLogFileAsync(20);

        // "Message 01" matches Message 010 through Message 019 (ten entries), newest first.
        var result = await _service.GetLogEntriesRangeAsync(search: "Message 01", date: _today, startIndex: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(r => r.Message), Is.EqualTo(new[] { "Message 019", "Message 018", "Message 017" }));
        }
    }

    [Test]
    public async Task Range_DateFilter_ReadsOnlyThatDateAsync()
    {
        await WriteLogFileAsync(5);
        var yesterday = _today.AddDays(-1);
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.web.{yesterday:yyyyMMdd}.log"),
            $"{{\"@t\":\"{yesterday.AddHours(1):yyyy-MM-ddTHH:mm:ssZ}\",\"@l\":\"Information\",\"@m\":\"Yesterday message\"}}");

        var result = await _service.GetLogEntriesRangeAsync(date: yesterday, startIndex: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results[0].Message, Is.EqualTo("Yesterday message"));
        }
    }

    [Test]
    public async Task Range_FileGrownBetweenReads_ReturnsNewEntriesAsync()
    {
        await WriteLogFileAsync(3);
        var first = await _service.GetLogEntriesRangeAsync(date: _today, startIndex: 0, count: 10);

        // Log files are live: entries appended after a window read must appear in the next one, so any parse
        // reuse inside the service has to notice the file changing underneath it.
        await WriteLogFileAsync(5);
        var second = await _service.GetLogEntriesRangeAsync(date: _today, startIndex: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.TotalResults, Is.EqualTo(3));
            Assert.That(second.TotalResults, Is.EqualTo(5));
            Assert.That(second.Results.First().Message, Is.EqualTo("Message 005"));
        }
    }
}
