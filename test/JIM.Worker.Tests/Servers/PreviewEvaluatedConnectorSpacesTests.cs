// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Application.Servers.Preview;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Logic;
using JIM.Models.Preview;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// #3014: which Connector Spaces each Configuration Change Preview evaluates as that system's synchronisation would,
/// so the framework can warn when an import of one has stopped part-way (#2521). A surface that answers wrongly
/// either reads half an import without saying so, or warns about a refusal that does not apply to it.
/// </summary>
[TestFixture]
public class PreviewEvaluatedConnectorSpacesTests
{
    private const int RuleId = 12;
    private const int ConnectedSystemId = 4;

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        _jim = new JimApplication(repository.Object);
    }

    [TearDown]
    public void TearDown() => _jim.Dispose();

    public static IEnumerable<TestCaseData> SyncRuleAdapters()
    {
        yield return new TestCaseData((Func<JimApplication, IConfigurationChangePreviewAdapter>)(jim => new SyncRuleAttributeFlowPreviewAdapter(jim, new SyncEngine()))).SetArgDisplayNames("AttributeFlow");
        yield return new TestCaseData((Func<JimApplication, IConfigurationChangePreviewAdapter>)(jim => new SyncRuleBehaviourTogglePreviewAdapter(jim, new SyncEngine()))).SetArgDisplayNames("BehaviourToggle");
        yield return new TestCaseData((Func<JimApplication, IConfigurationChangePreviewAdapter>)(jim => new SyncRuleDestructiveTogglePreviewAdapter(jim, new SyncEngine()))).SetArgDisplayNames("DestructiveToggle");
        yield return new TestCaseData((Func<JimApplication, IConfigurationChangePreviewAdapter>)(jim => new SyncRuleScopingPreviewAdapter(jim, new SyncEngine()))).SetArgDisplayNames("Scoping");
    }

    [TestCaseSource(nameof(SyncRuleAdapters))]
    public async Task GetEvaluatedConnectorSpacesAsync_ImportRule_IsTheRulesConnectedSystemAsync(Func<JimApplication, IConfigurationChangePreviewAdapter> create)
    {
        StoreRule(SyncRuleDirection.Import);

        var spaces = await create(_jim).GetEvaluatedConnectorSpacesAsync(Context(RuleId));

        Assert.That(spaces, Is.EqualTo(new[] { ConnectedSystemId }),
            "an import rule's preview evaluates its system's objects, and that system's synchronisation applies the change");
    }

    [TestCaseSource(nameof(SyncRuleAdapters))]
    public async Task GetEvaluatedConnectorSpacesAsync_ExportRule_IsNoneAsync(Func<JimApplication, IConfigurationChangePreviewAdapter> create)
    {
        StoreRule(SyncRuleDirection.Export);

        var spaces = await create(_jim).GetEvaluatedConnectorSpacesAsync(Context(RuleId));

        Assert.That(spaces, Is.Empty, "an export rule's preview evaluates Metaverse Objects, whose synchronisations are not refused");
    }

    [Test]
    public async Task GetEvaluatedConnectorSpacesAsync_ObjectMatching_IsTheConnectedSystemAsync()
    {
        var spaces = await new ObjectMatchingPreviewAdapter(_jim).GetEvaluatedConnectorSpacesAsync(Context(ConnectedSystemId));

        Assert.That(spaces, Is.EqualTo(new[] { ConnectedSystemId }));
    }

    [Test]
    public async Task GetEvaluatedConnectorSpacesAsync_FullSynchronisation_IsTheConnectedSystemAsync()
    {
        var spaces = await new ConnectedSystemFullSynchronisationPreviewAdapter(_jim).GetEvaluatedConnectorSpacesAsync(Context(ConnectedSystemId));

        Assert.That(spaces, Is.EqualTo(new[] { ConnectedSystemId }));
    }

    public static IEnumerable<TestCaseData> AdaptersEvaluatingNoConnectorSpace()
    {
        yield return new TestCaseData((Func<JimApplication, IConfigurationChangePreviewAdapter>)(jim => new ConnectedSystemDeletionPreviewAdapter(jim))).SetArgDisplayNames("Deletion");
        yield return new TestCaseData((Func<JimApplication, IConfigurationChangePreviewAdapter>)(jim => new ConnectedSystemSchemaPreviewAdapter(jim, new SyncEngine()))).SetArgDisplayNames("Schema");
        yield return new TestCaseData((Func<JimApplication, IConfigurationChangePreviewAdapter>)(jim => new ConnectedSystemScopeSelectionPreviewAdapter(jim, new SyncEngine()))).SetArgDisplayNames("ScopeSelection");
        yield return new TestCaseData((Func<JimApplication, IConfigurationChangePreviewAdapter>)(jim => new MetaverseObjectTypeDeletionSettingsPreviewAdapter(jim))).SetArgDisplayNames("MetaverseObjectTypeDeletionSettings");
    }

    [TestCaseSource(nameof(AdaptersEvaluatingNoConnectorSpace))]
    public async Task GetEvaluatedConnectorSpacesAsync_SurfaceAppliedByImportOrOverMetaverse_IsNoneAsync(Func<JimApplication, IConfigurationChangePreviewAdapter> create)
    {
        var spaces = await create(_jim).GetEvaluatedConnectorSpacesAsync(Context(ConnectedSystemId));

        Assert.That(spaces, Is.Empty);
    }

    private void StoreRule(SyncRuleDirection direction) =>
        _connectedSystems.Setup(r => r.GetSyncRuleAsync(RuleId))
            .ReturnsAsync(new SyncRule { Id = RuleId, Name = "HR", ConnectedSystemId = ConnectedSystemId, Direction = direction });

    private static PreviewContext Context(int targetId) => new()
    {
        Surface = ConfigurationChangePreviewSurface.SynchronisationRule,
        ActivityId = Guid.CreateVersion7(),
        TargetId = targetId,
        ProposedConfiguration = new object()
    };
}
