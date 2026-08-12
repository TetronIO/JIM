// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Unit tests for the Data Flow view's application logic (#1199): stamping each Import flow with how many
/// contributors its target Metaverse Attribute has, and filtering to multiply-contributed attributes on request. The repository
/// is mocked, so these exercise the counting and filtering rules rather than the query behind them; the query itself
/// is covered by <see cref="DataFlowQueryDatabaseTests"/> against real PostgreSQL.
/// </summary>
[TestFixture]
public class DataFlowTests
{
    private const int PersonObjectTypeId = 7;
    private const int DepartmentAttributeId = 42;
    private const int EmployeeIdAttributeId = 43;

    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _jim = new JimApplication(mockRepository.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task GetDataFlowsAsync_ImportFlow_CarriesItsAttributesContributorCountAsync()
    {
        SetUpFlows(
            ImportFlow(mappingId: 1, DepartmentAttributeId, priority: 1),
            ImportFlow(mappingId: 2, DepartmentAttributeId, priority: 2));
        SetUpContributors((1, DepartmentAttributeId), (2, DepartmentAttributeId));

        var flows = await _jim.ConnectedSystems.GetDataFlowsAsync(new DataFlowQuery());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flows.Select(f => f.ContributorCount), Is.All.EqualTo(2));
            Assert.That(flows.Select(f => f.HasMultipleContributors), Is.All.True);
        }
    }

    [Test]
    public async Task GetDataFlowsAsync_FilteredToOneSystem_StillCountsEveryContributorAsync()
    {
        // The count answers "does this attribute have a priority order worth managing?", which is a property of the
        // whole configuration. Counting only the flows the filter returned would report a shared attribute as having
        // a single contributor the moment someone filtered to one Connected System, inverting what the count is for.
        SetUpFlows(ImportFlow(mappingId: 1, DepartmentAttributeId, priority: 1));
        SetUpContributors((1, DepartmentAttributeId), (2, DepartmentAttributeId));

        var flows = await _jim.ConnectedSystems.GetDataFlowsAsync(new DataFlowQuery { ConnectedSystemId = 100 });

        Assert.That(flows.Single().ContributorCount, Is.EqualTo(2),
            "the filtered-out contributor still contributes; the count must not shrink with the filter");
    }

    [Test]
    public async Task GetDataFlowsAsync_MultipleContributorsOnly_KeepsOnlySharedAttributesAsync()
    {
        SetUpFlows(
            ImportFlow(mappingId: 1, DepartmentAttributeId, priority: 1),
            ImportFlow(mappingId: 2, DepartmentAttributeId, priority: 2),
            ImportFlow(mappingId: 3, EmployeeIdAttributeId, priority: int.MaxValue));
        SetUpContributors((1, DepartmentAttributeId), (2, DepartmentAttributeId), (3, EmployeeIdAttributeId));

        var flows = await _jim.ConnectedSystems.GetDataFlowsAsync(new DataFlowQuery { MultipleContributorsOnly = true });

        Assert.That(flows.Select(f => f.SyncRuleMappingId), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task GetDataFlowsAsync_ExportFlow_IsNeverGivenAContributorCountAsync()
    {
        // Priority is an import concern. An export flow has no contributors to count, and giving it a count would
        // make MultipleContributorsOnly silently drop or keep export rows on a number that means nothing for them.
        SetUpFlows(ExportFlow(mappingId: 1, enforceState: true));
        SetUpContributors();

        var flows = await _jim.ConnectedSystems.GetDataFlowsAsync(new DataFlowQuery());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flows.Single().ContributorCount, Is.Null);
            Assert.That(flows.Single().HasMultipleContributors, Is.False);
            Assert.That(flows.Single().EnforceState, Is.True);
        }
    }

    [Test]
    public void GetDataFlowsAsync_NullQuery_ThrowsAsync()
    {
        Assert.That(async () => await _jim.ConnectedSystems.GetDataFlowsAsync(null!),
            Throws.TypeOf<ArgumentNullException>());
    }

    private void SetUpFlows(params DataFlowHeader[] flows)
    {
        _mockCsRepo
            .Setup(r => r.GetDataFlowHeadersAsync(It.IsAny<DataFlowQuery>()))
            .ReturnsAsync(flows.ToList());
    }

    /// <summary>
    /// Stands in for the whole configuration's import mappings, which is what the contributor count is taken over.
    /// </summary>
    private void SetUpContributors(params (int MappingId, int AttributeId)[] contributors)
    {
        _mockCsRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseObjectTypeAsync(PersonObjectTypeId))
            .ReturnsAsync(contributors.Select(c => new SyncRuleMapping
            {
                Id = c.MappingId,
                TargetMetaverseAttributeId = c.AttributeId
            }).ToList());
    }

    private static DataFlowHeader ImportFlow(int mappingId, int targetMetaverseAttributeId, int priority) => new()
    {
        SyncRuleMappingId = mappingId,
        SyncRuleId = mappingId,
        SyncRuleName = $"Import Rule {mappingId}",
        SyncRuleEnabled = true,
        Direction = SyncRuleDirection.Import,
        ConnectedSystemId = 100 + mappingId,
        ConnectedSystemName = $"System {mappingId}",
        ConnectedSystemObjectTypeId = 200,
        ConnectedSystemObjectTypeName = "user",
        MetaverseObjectTypeId = PersonObjectTypeId,
        MetaverseObjectTypeName = "Person",
        TargetMetaverseAttributeId = targetMetaverseAttributeId,
        TargetMetaverseAttributeName = "Department",
        Priority = priority,
        NullIsValue = false
    };

    private static DataFlowHeader ExportFlow(int mappingId, bool enforceState) => new()
    {
        SyncRuleMappingId = mappingId,
        SyncRuleId = mappingId,
        SyncRuleName = $"Export Rule {mappingId}",
        SyncRuleEnabled = true,
        Direction = SyncRuleDirection.Export,
        ConnectedSystemId = 100 + mappingId,
        ConnectedSystemName = $"System {mappingId}",
        ConnectedSystemObjectTypeId = 200,
        ConnectedSystemObjectTypeName = "user",
        MetaverseObjectTypeId = PersonObjectTypeId,
        MetaverseObjectTypeName = "Person",
        TargetConnectedSystemAttributeId = 300,
        TargetConnectedSystemAttributeName = "department",
        EnforceState = enforceState
    };
}
