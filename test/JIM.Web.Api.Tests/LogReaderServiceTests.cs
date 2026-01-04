using JIM.Application.Services;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class LogReaderServiceTests
{
    private string _testLogPath = null!;
    private LogReaderService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _testLogPath = Path.Combine(Path.GetTempPath(), $"jim-test-logs-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testLogPath);
        _service = new LogReaderService(_testLogPath);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testLogPath))
        {
            Directory.Delete(_testLogPath, true);
        }
    }

    #region GetLogFilesAsync tests

    [Test]
    public async Task GetLogFilesAsync_WhenDirectoryEmpty_ReturnsEmptyList()
    {
        var files = await _service.GetLogFilesAsync();

        Assert.That(files, Is.Empty);
    }

    [Test]
    public async Task GetLogFilesAsync_WhenDirectoryDoesNotExist_ReturnsEmptyList()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var service = new LogReaderService(nonExistentPath);

        var files = await service.GetLogFilesAsync();

        Assert.That(files, Is.Empty);
    }

    [Test]
    public async Task GetLogFilesAsync_WithValidLogFile_ReturnsFileInfo()
    {
        var fileName = "jim.web.20260104.log";
        var filePath = Path.Combine(_testLogPath, fileName);
        await File.WriteAllTextAsync(filePath, "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Information\",\"@m\":\"Test message\"}");

        var files = await _service.GetLogFilesAsync();

        Assert.That(files, Has.Count.EqualTo(1));
        Assert.That(files[0].FileName, Is.EqualTo(fileName));
        Assert.That(files[0].Service, Is.EqualTo("web"));
        Assert.That(files[0].Date, Is.EqualTo(new DateTime(2026, 1, 4)));
    }

    [Test]
    public async Task GetLogFilesAsync_WithMultipleServices_ReturnsAllFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, "jim.web.20260104.log"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, "jim.worker.20260104.log"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, "jim.scheduler.20260104.log"), "{}");

        var files = await _service.GetLogFilesAsync();

        Assert.That(files, Has.Count.EqualTo(3));
        Assert.That(files.Select(f => f.Service), Is.EquivalentTo(new[] { "web", "worker", "scheduler" }));
    }

    [Test]
    public async Task GetLogFilesAsync_WithRolledFile_ParsesCorrectly()
    {
        var fileName = "jim.web.20260104_001.log";
        var filePath = Path.Combine(_testLogPath, fileName);
        await File.WriteAllTextAsync(filePath, "{}");

        var files = await _service.GetLogFilesAsync();

        Assert.That(files, Has.Count.EqualTo(1));
        Assert.That(files[0].Date, Is.EqualTo(new DateTime(2026, 1, 4)));
    }

    [Test]
    public async Task GetLogFilesAsync_IgnoresNonLogFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, "jim.web.20260104.log"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, "readme.txt"), "text");
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, "config.json"), "{}");

        var files = await _service.GetLogFilesAsync();

        Assert.That(files, Has.Count.EqualTo(1));
    }

    #endregion

    #region GetLogEntriesAsync tests

    [Test]
    public async Task GetLogEntriesAsync_WithJsonLogs_ParsesCorrectly()
    {
        var today = DateTime.UtcNow.Date;
        var fileName = $"jim.web.{today:yyyyMMdd}.log";
        var logContent = "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Information\",\"@m\":\"Test message\"}";
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, fileName), logContent);

        var entries = await _service.GetLogEntriesAsync(date: today);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Message, Is.EqualTo("Test message"));
        Assert.That(entries[0].Level, Is.EqualTo("Information"));
        Assert.That(entries[0].LevelShort, Is.EqualTo("INF"));
    }

    [Test]
    public async Task GetLogEntriesAsync_WithException_ParsesExceptionDetails()
    {
        var today = DateTime.UtcNow.Date;
        var fileName = $"jim.web.{today:yyyyMMdd}.log";
        var logContent = "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Error\",\"@m\":\"Error occurred\",\"@x\":\"System.Exception: Test exception\"}";
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, fileName), logContent);

        var entries = await _service.GetLogEntriesAsync(date: today);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Exception, Is.EqualTo("System.Exception: Test exception"));
    }

    [Test]
    public async Task GetLogEntriesAsync_WithServiceFilter_ReturnsOnlyMatchingService()
    {
        var today = DateTime.UtcNow.Date;
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.web.{today:yyyyMMdd}.log"),
            "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Information\",\"@m\":\"Web message\"}");
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.worker.{today:yyyyMMdd}.log"),
            "{\"@t\":\"2026-01-04T10:00:01Z\",\"@l\":\"Information\",\"@m\":\"Worker message\"}");

        var entries = await _service.GetLogEntriesAsync(service: "web", date: today);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Message, Is.EqualTo("Web message"));
        Assert.That(entries[0].Service, Is.EqualTo("web"));
    }

    [Test]
    public async Task GetLogEntriesAsync_WithLevelFilter_FiltersCorrectly()
    {
        var today = DateTime.UtcNow.Date;
        var fileName = $"jim.web.{today:yyyyMMdd}.log";
        var logContent = string.Join("\n",
            "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Debug\",\"@m\":\"Debug message\"}",
            "{\"@t\":\"2026-01-04T10:00:01Z\",\"@l\":\"Information\",\"@m\":\"Info message\"}",
            "{\"@t\":\"2026-01-04T10:00:02Z\",\"@l\":\"Warning\",\"@m\":\"Warning message\"}",
            "{\"@t\":\"2026-01-04T10:00:03Z\",\"@l\":\"Error\",\"@m\":\"Error message\"}");
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, fileName), logContent);

        var entries = await _service.GetLogEntriesAsync(minLevel: "Warning", date: today);

        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries.Select(e => e.Level), Is.EquivalentTo(new[] { "Warning", "Error" }));
    }

    [Test]
    public async Task GetLogEntriesAsync_WithSearchFilter_FiltersCorrectly()
    {
        var today = DateTime.UtcNow.Date;
        var fileName = $"jim.web.{today:yyyyMMdd}.log";
        var logContent = string.Join("\n",
            "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Information\",\"@m\":\"Processing user request\"}",
            "{\"@t\":\"2026-01-04T10:00:01Z\",\"@l\":\"Information\",\"@m\":\"Database query executed\"}",
            "{\"@t\":\"2026-01-04T10:00:02Z\",\"@l\":\"Information\",\"@m\":\"User authentication complete\"}");
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, fileName), logContent);

        var entries = await _service.GetLogEntriesAsync(search: "user", date: today);

        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries.All(e => e.Message.Contains("user", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task GetLogEntriesAsync_WithLimit_RespectsLimit()
    {
        var today = DateTime.UtcNow.Date;
        var fileName = $"jim.web.{today:yyyyMMdd}.log";
        var lines = Enumerable.Range(1, 10)
            .Select(i => $"{{\"@t\":\"2026-01-04T10:00:{i:D2}Z\",\"@l\":\"Information\",\"@m\":\"Message {i}\"}}")
            .ToArray();
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, fileName), string.Join("\n", lines));

        var entries = await _service.GetLogEntriesAsync(limit: 5, date: today);

        Assert.That(entries, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task GetLogEntriesAsync_WithOffset_SkipsEntries()
    {
        var today = DateTime.UtcNow.Date;
        var fileName = $"jim.web.{today:yyyyMMdd}.log";
        var lines = Enumerable.Range(1, 10)
            .Select(i => $"{{\"@t\":\"2026-01-04T10:00:{i:D2}Z\",\"@l\":\"Information\",\"@m\":\"Message {i}\"}}")
            .ToArray();
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, fileName), string.Join("\n", lines));

        var entries = await _service.GetLogEntriesAsync(offset: 3, limit: 5, date: today);

        Assert.That(entries, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task GetLogEntriesAsync_SortsByTimestampDescending()
    {
        var today = DateTime.UtcNow.Date;
        var fileName = $"jim.web.{today:yyyyMMdd}.log";
        var logContent = string.Join("\n",
            "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Information\",\"@m\":\"First\"}",
            "{\"@t\":\"2026-01-04T10:00:02Z\",\"@l\":\"Information\",\"@m\":\"Third\"}",
            "{\"@t\":\"2026-01-04T10:00:01Z\",\"@l\":\"Information\",\"@m\":\"Second\"}");
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, fileName), logContent);

        var entries = await _service.GetLogEntriesAsync(date: today);

        Assert.That(entries[0].Message, Is.EqualTo("Third"));
        Assert.That(entries[1].Message, Is.EqualTo("Second"));
        Assert.That(entries[2].Message, Is.EqualTo("First"));
    }

    [Test]
    public async Task GetLogEntriesAsync_WithProperties_ParsesProperties()
    {
        var today = DateTime.UtcNow.Date;
        var fileName = $"jim.web.{today:yyyyMMdd}.log";
        var logContent = "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Information\",\"@m\":\"User login\",\"UserId\":\"12345\",\"Action\":\"Login\"}";
        await File.WriteAllTextAsync(Path.Combine(_testLogPath, fileName), logContent);

        var entries = await _service.GetLogEntriesAsync(date: today);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Properties, Is.Not.Null);
        Assert.That(entries[0].Properties!["UserId"], Is.EqualTo("12345"));
        Assert.That(entries[0].Properties!["Action"], Is.EqualTo("Login"));
    }

    #endregion

    #region GetLogLevels tests

    [Test]
    public void GetLogLevels_ReturnsAllLevelsInOrder()
    {
        var levels = LogReaderService.GetLogLevels();

        Assert.That(levels, Has.Count.EqualTo(6));
        Assert.That(levels[0], Is.EqualTo("Verbose"));
        Assert.That(levels[1], Is.EqualTo("Debug"));
        Assert.That(levels[2], Is.EqualTo("Information"));
        Assert.That(levels[3], Is.EqualTo("Warning"));
        Assert.That(levels[4], Is.EqualTo("Error"));
        Assert.That(levels[5], Is.EqualTo("Fatal"));
    }

    #endregion

    #region GetServices tests

    [Test]
    public void GetServices_ReturnsAllServices()
    {
        var services = LogReaderService.GetServices();

        Assert.That(services, Has.Count.EqualTo(3));
        Assert.That(services, Contains.Item("web"));
        Assert.That(services, Contains.Item("worker"));
        Assert.That(services, Contains.Item("scheduler"));
    }

    #endregion
}
