// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Component tests for <see cref="DataFlowTab"/> (#1199): the system-wide Data Flow view.
/// <para>
/// What is tested here is what only exists once the component renders: which of the two mutually exclusive
/// direction-specific columns a row shows, and that a filter control actually reaches the query rather than merely
/// changing colour. The direction-dependent labels and links are plain functions and are covered in
/// <see cref="DataFlowDisplayTests"/>.
/// </para>
/// </summary>
[TestFixture]
public class DataFlowTabTests : JimComponentTestContext
{
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private DataFlowQuery? _lastQuery;

    protected override void ConfigureAdditionalServices()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(mockRepository.Object));
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
    }

    [SetUp]
    public void SetUp()
    {
        _lastQuery = null;

        // Two contributors to the Metaverse Attribute the Import flow targets, so the flow reads as contested.
        _mockConnectedSystemRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseObjectTypeAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRuleMapping>
            {
                new() { Id = 1, TargetMetaverseAttributeId = 500 },
                new() { Id = 2, TargetMetaverseAttributeId = 500 }
            });
    }

    private void SetupFlows(params DataFlowHeader[] flows)
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetDataFlowHeadersAsync(It.IsAny<DataFlowQuery>()))
            .Callback<DataFlowQuery>(q => _lastQuery = q)
            .ReturnsAsync(flows.ToList());
    }

    [Test]
    public void DataFlowTab_RendersEveryFlowItIsGiven()
    {
        SetupFlows(BuildImportFlow(), BuildExportFlow());

        var cut = Render<DataFlowTab>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("HR Import"));
            Assert.That(cut.Markup, Does.Contain("AD Export"));
        }
    }

    [Test]
    public void DataFlowTab_ImportFlow_ShowsNullIsAValueAndNeverEnforceState()
    {
        // The two options are mutually exclusive by direction. Showing an Export concern against an Inbound flow
        // would tell an administrator that Drift Correction applies to a contribution it has no bearing on.
        var flow = BuildImportFlow();
        flow.NullIsValue = true;
        SetupFlows(flow);

        var cut = Render<DataFlowTab>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Null is a value"));
            Assert.That(cut.Markup, Does.Not.Contain("Enforce State"));
        }
    }

    [Test]
    public void DataFlowTab_ExportFlow_ShowsEnforceStateAndNoPriority()
    {
        var flow = BuildExportFlow();
        SetupFlows(flow);

        var cut = Render<DataFlowTab>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Enforce State"));
            Assert.That(cut.Markup, Does.Not.Contain("Null is a value"));
            Assert.That(cut.Markup, Does.Not.Contain("Unranked"), "priority does not apply outbound");
        }
    }

    [Test]
    public void DataFlowTab_DisabledSynchronisationRule_IsMarkedAsDisabled()
    {
        // A disabled rule's flows are still shown, because they are configuration an administrator is reasoning
        // about; without the marker the page would claim data moves along a path that is switched off.
        var flow = BuildImportFlow();
        flow.SyncRuleEnabled = false;
        SetupFlows(flow);

        var cut = Render<DataFlowTab>();

        Assert.That(cut.Markup, Does.Contain("Disabled"));
    }

    [Test]
    public void DataFlowTab_ExpressionSource_ShowsTheExpressionUnderTheExMarker()
    {
        // An expression has no attribute name to show, so the cell shows the expression itself behind the shared
        // "Ex" marker, rather than the word "Expression", which would hide the only thing that tells two
        // computed sources apart.
        var flow = BuildImportFlow();
        flow.Sources = [new DataFlowSource { Order = 0, Expression = "ToUpper(cs[\"dept\"])" }];
        SetupFlows(flow);

        var cut = Render<DataFlowTab>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Ex"));
            Assert.That(cut.Markup, Does.Contain("ToUpper"));
        }
    }

    [Test]
    public void DataFlowTab_MarksWhichSideOfTheMetaverseEachAttributeSitsOn()
    {
        // Both sides are just names, and the sides swap between directions, so the CS / MV markers are the only
        // thing telling a reader that an Inbound row reads a Connected System attribute and writes a Metaverse one.
        SetupFlows(BuildImportFlow());

        var cut = Render<DataFlowTab>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain(">CS<"));
            Assert.That(cut.Markup, Does.Contain(">MV<"));
        }
    }

    [Test]
    public void DataFlowTab_ContestedOnlySwitch_NarrowsTheQuery()
    {
        // The switch has to reach the query: filtering by eye over a page of flows is exactly the work the page
        // exists to remove, and a control that only changes colour is worse than no control.
        SetupFlows(BuildImportFlow());
        var cut = Render<DataFlowTab>();
        Assert.That(_lastQuery!.MultipleContributorsOnly, Is.False, "the initial load is unfiltered");

        cut.Find("input[type=checkbox]").Change(true);

        Assert.That(_lastQuery!.MultipleContributorsOnly, Is.True);
    }

    [Test]
    public void DataFlowTab_ImportFlow_ShowsItsPositionInThePriorityOrder()
    {
        var flow = BuildImportFlow();
        flow.Priority = 2;
        SetupFlows(flow);

        var cut = Render<DataFlowTab>();

        Assert.That(cut.Markup, Does.Contain("Department"));
    }

    private static DataFlowHeader BuildImportFlow() => new()
    {
        SyncRuleMappingId = 1,
        SyncRuleId = 11,
        SyncRuleName = "HR Import",
        SyncRuleEnabled = true,
        Direction = SyncRuleDirection.Import,
        ConnectedSystemId = 2,
        ConnectedSystemName = "HR System",
        ConnectedSystemObjectTypeId = 20,
        ConnectedSystemObjectTypeName = "Employee",
        MetaverseObjectTypeId = 100,
        MetaverseObjectTypeName = "Person",
        TargetMetaverseAttributeId = 500,
        TargetMetaverseAttributeName = "Department",
        Priority = 1,
        NullIsValue = false,
        Sources = [new DataFlowSource { Order = 0, ConnectedSystemAttributeId = 9, ConnectedSystemAttributeName = "dept" }]
    };

    private static DataFlowHeader BuildExportFlow() => new()
    {
        SyncRuleMappingId = 2,
        SyncRuleId = 12,
        SyncRuleName = "AD Export",
        SyncRuleEnabled = true,
        Direction = SyncRuleDirection.Export,
        ConnectedSystemId = 3,
        ConnectedSystemName = "Contoso AD",
        ConnectedSystemObjectTypeId = 30,
        ConnectedSystemObjectTypeName = "user",
        MetaverseObjectTypeId = 100,
        MetaverseObjectTypeName = "Person",
        TargetConnectedSystemAttributeId = 600,
        TargetConnectedSystemAttributeName = "department",
        EnforceState = true,
        Sources = [new DataFlowSource { Order = 0, MetaverseAttributeId = 500, MetaverseAttributeName = "Department" }]
    };

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
