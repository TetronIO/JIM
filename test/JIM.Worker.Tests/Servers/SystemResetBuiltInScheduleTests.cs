// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Scheduling;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that a factory reset restores the built-in Temporal Scope Reconciliation Schedule (issue #892). The wipe
/// truncates the Schedules table, and built-in data is meant to survive a reset; without an immediate re-seed the
/// Schedule only reappears on the next worker restart, leaving date-based scope reconciliation silently inoperative
/// until then.
/// <para>
/// The reset used to call <c>SeedBuiltInSchedulesAsync</c> directly, as one of a hand-maintained list of repairs for
/// the specific built-ins earlier resets were observed to lose. It now runs the whole built-in configuration
/// pipeline instead (issue #916), so this asserts the same outcome through the shared path.
/// </para>
/// </summary>
[TestFixture]
public class SystemResetBuiltInScheduleTests
{
    private SeedingTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new SeedingTestHarness();
        _harness.PersistBuiltInConfiguration();
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    /// <summary>
    /// Puts the harness in the state the wipe leaves behind: built-in rows preserved, but the Schedules and
    /// Activities tables truncated.
    /// </summary>
    private void SimulateWipe()
    {
        _harness.Schedules.Clear();
        _harness.CreatedActivities.Clear();
        _harness.UpdatedActivities.Clear();
    }

    [Test]
    public async Task ResetSystemAsync_SchedulesWiped_ReseedsBuiltInScheduleAsync()
    {
        SimulateWipe();

        await _harness.Jim.System.ResetSystemAsync(
            ActivityInitiatorType.ApiKey, Guid.NewGuid(), "Infrastructure Key", includeAdministrators: false);

        Assert.That(_harness.CreatedSchedules.Any(s =>
                s.BuiltIn &&
                s.IsEnabled &&
                s.Steps.Any(st => st.StepType == ScheduleStepType.TemporalScopeReconciliation)), Is.True,
            "a factory reset must restore the built-in Temporal Scope Reconciliation Schedule immediately, not on the next worker restart");
    }

    [Test]
    public async Task ResetSystemAsync_ReseedCreatesSeedingParentActivity_ParentIsCompletedAsync()
    {
        // The reseed lazily creates the "System Initialisation" parent Activity that groups seeded objects. Prove
        // it does not get left permanently InProgress; an in-flight activity would also block any subsequent reset
        // via the in-progress guard.
        SimulateWipe();

        await _harness.Jim.System.ResetSystemAsync(
            ActivityInitiatorType.ApiKey, Guid.NewGuid(), "Infrastructure Key", includeAdministrators: false);

        var seedingParent = _harness.UpdatedActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(seedingParent, Is.Not.Null,
            "the reseed's System Initialisation parent Activity must be completed by the reset path, not left permanently InProgress");
        Assert.That(seedingParent!.Status, Is.EqualTo(ActivityStatus.Complete));
    }
}
