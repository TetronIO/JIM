// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Issue #985 (d): unit tests for the Max Export Parallelism resolution order, extracted from
/// SyncExportTaskProcessor into ExportParallelismResolver so it can be tested without a full
/// processor harness. Resolution order: an explicit Connected System value always wins; otherwise
/// a connector recommendation (via IConnectorRecommendedExportParallelism) is used, clamped to
/// [1, 16]; otherwise the fallback of 1 (sequential, matching the pre-#985d default).
/// </summary>
[TestFixture]
public class ExportParallelismResolverTests
{
    private static readonly List<ConnectedSystemSettingValue> EmptySettingValues = [];

    [Test]
    public void Resolve_ExplicitValueSet_ReturnsExplicitValue()
    {
        var connector = new Mock<IConnector>();

        var result = ExportParallelismResolver.Resolve(
            explicitMaxExportParallelism: 5,
            connector: connector.Object,
            settingValues: EmptySettingValues,
            connectedSystemName: "Test System");

        Assert.That(result, Is.EqualTo(5));
    }

    [Test]
    public void Resolve_ExplicitValueSet_IgnoresConnectorRecommendation()
    {
        // An admin's explicit choice must always win, even when the connector recommends
        // something different (same philosophy as the Export Concurrency auto-tune guard).
        var connector = new Mock<IConnector>();
        var recommender = connector.As<IConnectorRecommendedExportParallelism>();
        recommender.Setup(r => r.GetRecommendedExportParallelism(It.IsAny<List<ConnectedSystemSettingValue>>()))
            .Returns(16);

        var result = ExportParallelismResolver.Resolve(
            explicitMaxExportParallelism: 5,
            connector: connector.Object,
            settingValues: EmptySettingValues,
            connectedSystemName: "Test System");

        Assert.That(result, Is.EqualTo(5));
    }

    [Test]
    public void Resolve_NoExplicitValue_UsesConnectorRecommendation()
    {
        var connector = new Mock<IConnector>();
        var recommender = connector.As<IConnectorRecommendedExportParallelism>();
        recommender.Setup(r => r.GetRecommendedExportParallelism(It.IsAny<List<ConnectedSystemSettingValue>>()))
            .Returns(8);

        var result = ExportParallelismResolver.Resolve(
            explicitMaxExportParallelism: null,
            connector: connector.Object,
            settingValues: EmptySettingValues,
            connectedSystemName: "Test System");

        Assert.That(result, Is.EqualTo(8));
    }

    [Test]
    public void Resolve_NoExplicitValue_RecommendationAboveMax_ClampsTo16()
    {
        var connector = new Mock<IConnector>();
        var recommender = connector.As<IConnectorRecommendedExportParallelism>();
        recommender.Setup(r => r.GetRecommendedExportParallelism(It.IsAny<List<ConnectedSystemSettingValue>>()))
            .Returns(32);

        var result = ExportParallelismResolver.Resolve(
            explicitMaxExportParallelism: null,
            connector: connector.Object,
            settingValues: EmptySettingValues,
            connectedSystemName: "Test System");

        Assert.That(result, Is.EqualTo(16));
    }

    [Test]
    public void Resolve_NoExplicitValue_RecommendationBelowMin_ClampsTo1()
    {
        var connector = new Mock<IConnector>();
        var recommender = connector.As<IConnectorRecommendedExportParallelism>();
        recommender.Setup(r => r.GetRecommendedExportParallelism(It.IsAny<List<ConnectedSystemSettingValue>>()))
            .Returns(0);

        var result = ExportParallelismResolver.Resolve(
            explicitMaxExportParallelism: null,
            connector: connector.Object,
            settingValues: EmptySettingValues,
            connectedSystemName: "Test System");

        Assert.That(result, Is.EqualTo(1));
    }

    [Test]
    public void Resolve_NoExplicitValue_ConnectorDoesNotImplementRecommendationInterface_ReturnsFallbackOf1()
    {
        var connector = new Mock<IConnector>();

        var result = ExportParallelismResolver.Resolve(
            explicitMaxExportParallelism: null,
            connector: connector.Object,
            settingValues: EmptySettingValues,
            connectedSystemName: "Test System");

        Assert.That(result, Is.EqualTo(1));
    }

    [Test]
    public void Resolve_NoExplicitValue_ConnectorRecommendsNull_ReturnsFallbackOf1()
    {
        var connector = new Mock<IConnector>();
        var recommender = connector.As<IConnectorRecommendedExportParallelism>();
        recommender.Setup(r => r.GetRecommendedExportParallelism(It.IsAny<List<ConnectedSystemSettingValue>>()))
            .Returns((int?)null);

        var result = ExportParallelismResolver.Resolve(
            explicitMaxExportParallelism: null,
            connector: connector.Object,
            settingValues: EmptySettingValues,
            connectedSystemName: "Test System");

        Assert.That(result, Is.EqualTo(1));
    }
}
