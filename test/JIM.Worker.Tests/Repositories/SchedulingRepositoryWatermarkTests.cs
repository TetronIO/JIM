// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests for GetLastCompletedScheduleExecutionAsync in SchedulingRepository (issue #892). This query supplies the
/// Temporal Scope Reconciler's failure-safe watermark: only a successfully completed execution advances the
/// watermark, so a failed sweep never causes a window to be skipped for objects with static source data.
/// </summary>
[TestFixture]
public class SchedulingRepositoryWatermarkTests
{
    private Guid _scheduleId;
    private Guid _otherScheduleId;
    private List<ScheduleExecution> _executionsData = null!;
    private Mock<JimDbContext> _mockDbContext = null!;
    private PostgresDataRepository _repository = null!;

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _scheduleId = Guid.NewGuid();
        _otherScheduleId = Guid.NewGuid();
        _executionsData = new List<ScheduleExecution>();
    }

    private void BuildRepository()
    {
        var mockDbSet = _executionsData.BuildMockDbSet();
        _mockDbContext = new Mock<JimDbContext>();
        _mockDbContext.Setup(m => m.ScheduleExecutions).Returns(mockDbSet.Object);
        _repository = new PostgresDataRepository(_mockDbContext.Object);
    }

    private ScheduleExecution Execution(Guid scheduleId, ScheduleExecutionStatus status, DateTime startedAt)
    {
        return new ScheduleExecution
        {
            Id = Guid.NewGuid(),
            ScheduleId = scheduleId,
            Status = status,
            StartedAt = startedAt
        };
    }

    [Test]
    public async Task GetLastCompletedScheduleExecutionAsync_MultipleCompleted_ReturnsMostRecentBeforeBoundAsync()
    {
        var older = Execution(_scheduleId, ScheduleExecutionStatus.Completed, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Execution(_scheduleId, ScheduleExecutionStatus.Completed, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        _executionsData.AddRange(new[] { older, newer });
        BuildRepository();

        var result = await _repository.Scheduling.GetLastCompletedScheduleExecutionAsync(
            _scheduleId, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(newer.Id));
    }

    [Test]
    public async Task GetLastCompletedScheduleExecutionAsync_FailedIsMostRecent_IgnoresFailedAndReturnsCompletedAsync()
    {
        // The failure-safe guarantee: a Failed sweep more recent than the last Completed one must NOT be chosen as
        // the watermark, otherwise its (unprocessed) window would be silently skipped.
        var completed = Execution(_scheduleId, ScheduleExecutionStatus.Completed, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var failed = Execution(_scheduleId, ScheduleExecutionStatus.Failed, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        _executionsData.AddRange(new[] { completed, failed });
        BuildRepository();

        var result = await _repository.Scheduling.GetLastCompletedScheduleExecutionAsync(
            _scheduleId, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(completed.Id));
    }

    [Test]
    public async Task GetLastCompletedScheduleExecutionAsync_OnlyInProgressAndFailed_ReturnsNullAsync()
    {
        _executionsData.Add(Execution(_scheduleId, ScheduleExecutionStatus.InProgress, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        _executionsData.Add(Execution(_scheduleId, ScheduleExecutionStatus.Failed, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        BuildRepository();

        var result = await _repository.Scheduling.GetLastCompletedScheduleExecutionAsync(
            _scheduleId, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetLastCompletedScheduleExecutionAsync_CompletedAtOrAfterBound_IsExcludedAsync()
    {
        // The current (in-progress) execution's own start time is the bound; completions at or after it must be
        // excluded so the sweep never uses its own run as the lower watermark.
        var bound = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        _executionsData.Add(Execution(_scheduleId, ScheduleExecutionStatus.Completed, bound)); // equal to bound: excluded
        _executionsData.Add(Execution(_scheduleId, ScheduleExecutionStatus.Completed, bound.AddHours(1))); // after bound: excluded
        BuildRepository();

        var result = await _repository.Scheduling.GetLastCompletedScheduleExecutionAsync(_scheduleId, bound);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetLastCompletedScheduleExecutionAsync_DifferentSchedule_IsExcludedAsync()
    {
        _executionsData.Add(Execution(_otherScheduleId, ScheduleExecutionStatus.Completed, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        BuildRepository();

        var result = await _repository.Scheduling.GetLastCompletedScheduleExecutionAsync(
            _scheduleId, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));

        Assert.That(result, Is.Null);
    }
}
