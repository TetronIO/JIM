// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Activity Schedule context panel (issue #1196, item 2c). The behaviour under test is that an
/// Activity produced by a Schedule says so, linking back to the Schedule Execution that produced it, and that
/// an Activity with no Schedule behind it (or whose Schedule Execution has since been pruned) renders nothing
/// at all rather than an empty panel.
/// <para>
/// The lookup guard is the other reason this component is tested: the Operations History tab polls and
/// re-renders continuously, so an unguarded lookup would hit the database on every poll tick.
/// </para>
/// </summary>
[TestFixture]
public class ActivityScheduleContextTests : JimComponentTestContext
{
    private static readonly Guid ExecutionId = new("3f6f1d5e-6f2a-4a1e-9d0b-7c6c2f9a1b44");
    private const string ScheduleName = "Nightly Directory Synchronisation";

    /// <summary>
    /// The heading the page-width panel carries, and the only thing distinguishing it from the compact panel
    /// section in the rendered markup.
    /// </summary>
    private const string PagePanelHeading = "Part of a Schedule";

    /// <summary>
    /// The field labels the page-width panel carries, matching the Summary panel's label style. The Schedule
    /// Execution label is what tells a reader that the chip beside it is the whole run's outcome rather than
    /// this Activity's own status.
    /// </summary>
    private const string ScheduleLabel = "Schedule:";
    private const string StepLabel = "Step:";
    private const string ScheduleExecutionLabel = "Schedule Execution:";

    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private JimApplication _jim = null!;

    /// <summary>
    /// Builds the mocked repository, the real <see cref="JimApplication"/> wrapping it, and registers a fake
    /// <see cref="IJimApplicationFactory"/> handing it out. Must happen here rather than in <c>[SetUp]</c>: see
    /// <see cref="JimComponentTestContext.ConfigureAdditionalServices"/>.
    /// </summary>
    protected override void ConfigureAdditionalServices()
    {
        var mockRepository = new Mock<IRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();
        mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);

        _jim = new JimApplication(mockRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
    }

    /// <summary>
    /// NUnit reuses one fixture instance across every test in the fixture, and the bUnit service provider is
    /// built once in the base constructor (see <see cref="JimComponentTestContext.ConfigureAdditionalServices"/>),
    /// so the mock is shared. Reset it per test or arrangements and recorded invocations leak between tests.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _mockSchedulingRepository.Reset();
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    private void ArrangeExecution(int totalSteps = 6, ScheduleExecutionStatus status = ScheduleExecutionStatus.Complete)
    {
        _mockSchedulingRepository
            .Setup(r => r.GetScheduleExecutionAsync(ExecutionId))
            .ReturnsAsync(new ScheduleExecution
            {
                Id = ExecutionId,
                ScheduleName = ScheduleName,
                TotalSteps = totalSteps,
                Status = status
            });
    }

    private void ArrangeNoExecution()
    {
        _mockSchedulingRepository
            .Setup(r => r.GetScheduleExecutionAsync(It.IsAny<Guid>()))
            .ReturnsAsync((ScheduleExecution?)null);
    }

    [Test]
    public void ActivityScheduleContext_WithoutAScheduleExecutionId_RendersNothing()
    {
        var cut = Render<ActivityScheduleContext>(p => p
            .Add(c => c.ScheduleExecutionId, (Guid?)null)
            .Add(c => c.ScheduleStepIndex, (int?)null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup.Trim(), Is.Empty);
            Assert.That(cut.HasComponent<MudAlert>(), Is.False);
        }

        // An Activity with no Schedule behind it must not cost a database round trip either.
        _mockSchedulingRepository.Verify(r => r.GetScheduleExecutionAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public void ActivityScheduleContext_WhenTheScheduleExecutionNoLongerExists_RendersNothing()
    {
        ArrangeNoExecution();

        var cut = Render<ActivityScheduleContext>(p => p
            .Add(c => c.ScheduleExecutionId, ExecutionId)
            .Add(c => c.ScheduleStepIndex, 2));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void ActivityScheduleContext_WithAnExecutionAndStepIndex_RendersTheScheduleNameAndOneBasedStep()
    {
        ArrangeExecution();

        var cut = Render<ActivityScheduleContext>(p => p
            .Add(c => c.ScheduleExecutionId, ExecutionId)
            .Add(c => c.ScheduleStepIndex, 2));

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain(ScheduleName)));
        using (Assert.EnterMultipleScope())
        {
            // The page-width panel lays the facts out as labelled fields, one per line, matching the Summary
            // panel above it; the step is its own labelled field rather than part of a sentence.
            Assert.That(cut.Markup, Does.Contain(StepLabel));
            // ScheduleStepIndex is 0-based; the display is 1-based, so index 2 reads as "3 of 6".
            Assert.That(cut.Markup, Does.Contain("3 of 6"));
        }
    }

    /// <summary>
    /// Every value in the page-width panel is labelled, and the status chip especially: the Summary panel above
    /// it already carries a chip for the Activity's own status, so an unlabelled second chip describing the whole
    /// run's outcome is ambiguous about which object it describes. Both chips now read "Complete" (the Schedule
    /// Execution vocabulary was aligned with the Activity vocabulary in #1196), so the label is the only thing
    /// distinguishing them.
    /// </summary>
    [Test]
    public void ActivityScheduleContext_NotCompact_LabelsEveryFieldIncludingTheStatusChip()
    {
        ArrangeExecution();

        var cut = Render<ActivityScheduleContext>(p => p
            .Add(c => c.ScheduleExecutionId, ExecutionId)
            .Add(c => c.ScheduleStepIndex, 2)
            .Add(c => c.Compact, false));

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain(ScheduleName)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain(ScheduleLabel));
            Assert.That(cut.Markup, Does.Contain(StepLabel));
            Assert.That(cut.Markup, Does.Contain(ScheduleExecutionLabel));
            // The run's outcome, which is what the labelled chip has to be readable as.
            Assert.That(cut.Markup, Does.Contain("Complete"));
        }
    }

    /// <summary>
    /// The fields sit in the same grid the Summary panel uses, so the two panels read as siblings rather than as
    /// two unrelated designs stacked on top of each other.
    /// </summary>
    [Test]
    public void ActivityScheduleContext_NotCompact_LaysTheFieldsOutInAGrid()
    {
        ArrangeExecution();

        var cut = Render<ActivityScheduleContext>(p => p
            .Add(c => c.ScheduleExecutionId, ExecutionId)
            .Add(c => c.ScheduleStepIndex, 2)
            .Add(c => c.Compact, false));

        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<MudGrid>(), Is.True));
        // Schedule, Step and Schedule Execution, each on its own line.
        Assert.That(cut.FindComponents<MudItem>(), Has.Count.EqualTo(3));
    }

    /// <summary>
    /// The compact treatment is unchanged by the page-width panel's move to labelled fields: it has no heading
    /// of its own and sits in a narrow side panel, so it keeps the single-sentence form.
    /// </summary>
    [Test]
    public void ActivityScheduleContext_Compact_KeepsTheSingleSentenceForm()
    {
        ArrangeExecution();

        var cut = Render<ActivityScheduleContext>(p => p
            .Add(c => c.ScheduleExecutionId, ExecutionId)
            .Add(c => c.ScheduleStepIndex, 2)
            .Add(c => c.Compact, true));

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain($"Part of {ScheduleName}, step 3 of 6")));
        Assert.That(cut.HasComponent<MudGrid>(), Is.False);
    }

    [Test]
    public void ActivityScheduleContext_WithAnExecution_LinksToTheScheduleExecutionDetailPage()
    {
        ArrangeExecution();

        var cut = Render<ActivityScheduleContext>(p => p
            .Add(c => c.ScheduleExecutionId, ExecutionId)
            .Add(c => c.ScheduleStepIndex, 0));

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain(ScheduleName)));
        Assert.That(cut.Markup, Does.Contain($"/admin/operations/schedule-executions/{ExecutionId}"));
    }

    [Test]
    public void ActivityScheduleContext_WithoutAStepIndex_RendersTheScheduleNameWithoutAStep()
    {
        ArrangeExecution();

        var cut = Render<ActivityScheduleContext>(p => p
            .Add(c => c.ScheduleExecutionId, ExecutionId)
            .Add(c => c.ScheduleStepIndex, (int?)null));

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain(ScheduleName)));
        using (Assert.EnterMultipleScope())
        {
            // Omitted entirely rather than rendered as an empty field.
            Assert.That(cut.Markup, Does.Not.Contain(StepLabel));
            Assert.That(cut.FindComponents<MudItem>(), Has.Count.EqualTo(2));
        }
    }

    /// <summary>
    /// The page-width variant is a panel matching the Activity page's other panels, headed "Part of a Schedule",
    /// and explicitly not an alert: the context is another section of the page, not a notice interrupting it.
    /// </summary>
    [Test]
    public void ActivityScheduleContext_NotCompact_RendersThePagePanelTreatment()
    {
        ArrangeExecution();

        var cut = Render<ActivityScheduleContext>(p => p
            .Add(c => c.ScheduleExecutionId, ExecutionId)
            .Add(c => c.ScheduleStepIndex, 2)
            .Add(c => c.Compact, false));

        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<MudPaper>(), Is.True));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<MudAlert>(), Is.False);
            Assert.That(cut.Markup, Does.Contain(PagePanelHeading));
            Assert.That(cut.Markup, Does.Contain("View Schedule Execution"));
        }
    }

    [Test]
    public void ActivityScheduleContext_Compact_RendersThePanelSectionTreatment()
    {
        ArrangeExecution();

        var cut = Render<ActivityScheduleContext>(p => p
            .Add(c => c.ScheduleExecutionId, ExecutionId)
            .Add(c => c.ScheduleStepIndex, 2)
            .Add(c => c.Compact, true));

        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<MudPaper>(), Is.True));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<MudAlert>(), Is.False);
            // Both variants are papers now, so the heading is what tells them apart.
            Assert.That(cut.Markup, Does.Not.Contain(PagePanelHeading));
        }
    }

    [Test]
    public void ActivityScheduleContext_ReRenderedWithTheSameExecutionId_DoesNotQueryAgain()
    {
        // The Operations History tab polls, so the component is re-parameterised continuously with the same
        // values. Without the loaded-id guard that is one database round trip per poll tick.
        ArrangeExecution();

        var cut = Render<ActivityScheduleContext>(p => p
            .Add(c => c.ScheduleExecutionId, ExecutionId)
            .Add(c => c.ScheduleStepIndex, 2));

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain(ScheduleName)));

        cut.Render();
        cut.Render();

        _mockSchedulingRepository.Verify(r => r.GetScheduleExecutionAsync(ExecutionId), Times.Once);
    }

    /// <summary>
    /// Hands out the same, already-arranged <see cref="JimApplication"/> instance on every call, since the
    /// component only needs one over the fixture's lifetime and the class under test disposes what it creates.
    /// </summary>
    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
