using JIM.Application.Services;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class LogsControllerTests
{
    private string _testLogPath = null!;
    private LogReaderService _logReaderService = null!;
    private Mock<ILogger<LogsController>> _mockLogger = null!;
    private LogsController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _testLogPath = Path.Combine(Path.GetTempPath(), $"jim-test-logs-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testLogPath);
        _logReaderService = new LogReaderService(_testLogPath);
        _mockLogger = new Mock<ILogger<LogsController>>();
        _controller = new LogsController(_mockLogger.Object, _logReaderService);
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
    public async Task GetLogFilesAsync_ReturnsOkResult()
    {
        var result = await _controller.GetLogFilesAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetLogFilesAsync_WhenFilesExist_ReturnsLogFileDtos()
    {
        var today = DateTime.UtcNow.Date;
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.web.{today:yyyyMMdd}.log"),
            "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Information\",\"@m\":\"Test\"}");

        var result = await _controller.GetLogFilesAsync() as OkObjectResult;
        var files = result?.Value as IEnumerable<LogFileDto>;

        Assert.That(files, Is.Not.Null);
        Assert.That(files!.Count(), Is.EqualTo(1));
        Assert.That(files!.First().Service, Is.EqualTo("web"));
    }

    [Test]
    public async Task GetLogFilesAsync_IncludesFormattedSize()
    {
        var today = DateTime.UtcNow.Date;
        var content = new string('a', 1024); // 1KB
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.web.{today:yyyyMMdd}.log"),
            content);

        var result = await _controller.GetLogFilesAsync() as OkObjectResult;
        var files = result?.Value as IEnumerable<LogFileDto>;

        Assert.That(files!.First().SizeFormatted, Does.Contain("KB").Or.Contain("B"));
    }

    #endregion

    #region GetLogEntriesAsync tests

    [Test]
    public async Task GetLogEntriesAsync_ReturnsOkResult()
    {
        var request = new LogQueryRequest();

        var result = await _controller.GetLogEntriesAsync(request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetLogEntriesAsync_ReturnsLogEntryDtos()
    {
        var today = DateTime.UtcNow.Date;
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.web.{today:yyyyMMdd}.log"),
            "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Warning\",\"@m\":\"Test warning\"}");
        var request = new LogQueryRequest { Date = today };

        var result = await _controller.GetLogEntriesAsync(request) as OkObjectResult;
        var entries = result?.Value as IEnumerable<LogEntryDto>;

        Assert.That(entries, Is.Not.Null);
        Assert.That(entries!.Count(), Is.EqualTo(1));
        Assert.That(entries!.First().Level, Is.EqualTo("Warning"));
        Assert.That(entries!.First().LevelShort, Is.EqualTo("WRN"));
        Assert.That(entries!.First().Message, Is.EqualTo("Test warning"));
    }

    [Test]
    public async Task GetLogEntriesAsync_ClampsLimitToMaximum()
    {
        var today = DateTime.UtcNow.Date;
        var lines = Enumerable.Range(1, 100)
            .Select(i => $"{{\"@t\":\"2026-01-04T10:00:{i % 60:D2}Z\",\"@l\":\"Information\",\"@m\":\"Message {i}\"}}")
            .ToArray();
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.web.{today:yyyyMMdd}.log"),
            string.Join("\n", lines));
        var request = new LogQueryRequest { Date = today, Limit = 10000 }; // Over max

        var result = await _controller.GetLogEntriesAsync(request) as OkObjectResult;
        var entries = result?.Value as IEnumerable<LogEntryDto>;

        // Should return up to 5000 (the max), but we only have 100
        Assert.That(entries!.Count(), Is.EqualTo(100));
    }

    [Test]
    public async Task GetLogEntriesAsync_ClampsLimitToMinimum()
    {
        var today = DateTime.UtcNow.Date;
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.web.{today:yyyyMMdd}.log"),
            "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Information\",\"@m\":\"Test\"}");
        var request = new LogQueryRequest { Date = today, Limit = -5 }; // Negative

        var result = await _controller.GetLogEntriesAsync(request) as OkObjectResult;
        var entries = result?.Value as IEnumerable<LogEntryDto>;

        // Should clamp to 1
        Assert.That(entries!.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task GetLogEntriesAsync_ClampsOffsetToMinimum()
    {
        var today = DateTime.UtcNow.Date;
        var lines = Enumerable.Range(1, 5)
            .Select(i => $"{{\"@t\":\"2026-01-04T10:00:{i:D2}Z\",\"@l\":\"Information\",\"@m\":\"Message {i}\"}}")
            .ToArray();
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.web.{today:yyyyMMdd}.log"),
            string.Join("\n", lines));
        var request = new LogQueryRequest { Date = today, Offset = -10 }; // Negative

        var result = await _controller.GetLogEntriesAsync(request) as OkObjectResult;
        var entries = result?.Value as IEnumerable<LogEntryDto>;

        // Should clamp to 0 and return all entries
        Assert.That(entries!.Count(), Is.EqualTo(5));
    }

    [Test]
    public async Task GetLogEntriesAsync_AppliesServiceFilter()
    {
        var today = DateTime.UtcNow.Date;
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.web.{today:yyyyMMdd}.log"),
            "{\"@t\":\"2026-01-04T10:00:00Z\",\"@l\":\"Information\",\"@m\":\"Web\"}");
        await File.WriteAllTextAsync(
            Path.Combine(_testLogPath, $"jim.worker.{today:yyyyMMdd}.log"),
            "{\"@t\":\"2026-01-04T10:00:01Z\",\"@l\":\"Information\",\"@m\":\"Worker\"}");
        var request = new LogQueryRequest { Date = today, Service = "worker" };

        var result = await _controller.GetLogEntriesAsync(request) as OkObjectResult;
        var entries = result?.Value as IEnumerable<LogEntryDto>;

        Assert.That(entries!.Count(), Is.EqualTo(1));
        Assert.That(entries!.First().Service, Is.EqualTo("worker"));
    }

    #endregion

    #region GetLogLevels tests

    [Test]
    public void GetLogLevels_ReturnsOkResult()
    {
        var result = _controller.GetLogLevels();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public void GetLogLevels_ReturnsAllLevels()
    {
        var result = _controller.GetLogLevels() as OkObjectResult;
        var levels = result?.Value as List<string>;

        Assert.That(levels, Is.Not.Null);
        Assert.That(levels, Has.Count.EqualTo(6));
        Assert.That(levels, Contains.Item("Information"));
        Assert.That(levels, Contains.Item("Error"));
    }

    #endregion

    #region GetLogServices tests

    [Test]
    public void GetLogServices_ReturnsOkResult()
    {
        var result = _controller.GetLogServices();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public void GetLogServices_ReturnsAllServices()
    {
        var result = _controller.GetLogServices() as OkObjectResult;
        var services = result?.Value as List<string>;

        Assert.That(services, Is.Not.Null);
        Assert.That(services, Has.Count.EqualTo(4));
        Assert.That(services, Contains.Item("web"));
        Assert.That(services, Contains.Item("worker"));
        Assert.That(services, Contains.Item("scheduler"));
        Assert.That(services, Contains.Item("database"));
    }

    #endregion
}
