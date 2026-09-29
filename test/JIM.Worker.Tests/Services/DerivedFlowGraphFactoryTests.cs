// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Application.Services;
using JIM.Data;
using JIM.TestSupport;
using Moq;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// <see cref="DerivedFlowGraphFactory"/> (#1750, plan decisions 11 and 12): the one place the run-time feature flag is
/// read. Flag off, no graph, so the engine behaves exactly as before the feature; flag on, the graph of enabled
/// mappings, refusing to start a run over a dependency cycle.
/// </summary>
[TestFixture]
public class DerivedFlowGraphFactoryTests
{
    private DerivedFlowTestModel _model = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new DerivedFlowTestModel();
    }

    private static JimApplication Application(bool flagsOn)
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.ServiceSettings).Returns(flagsOn
            ? InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled()
            : new InMemoryServiceSettingsRepository());
        return new JimApplication(repo.Object);
    }

    [Test]
    public async Task CreateAsync_FlagOff_ReturnsNullAsync()
    {
        var hr = ImportRule(1, "HR Import", 1);
        Expression(hr, 101, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"");
        using var jim = Application(flagsOn: false);

        var graph = await DerivedFlowGraphFactory.CreateAsync(jim.FeatureFlags, [hr], _model.Types);

        Assert.That(graph, Is.Null);
    }

    [Test]
    public async Task CreateAsync_FlagOff_DoesNotThrowOverACycleAsync()
    {
        // Flag off, the engine is exactly as before the feature; a cycle is meaningless there.
        var hr = ImportRule(1, "HR Import", 1);
        Expression(hr, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        Expression(hr, 102, _model.MailNickname, "mv[\"Display Name\"]");
        using var jim = Application(flagsOn: false);

        Assert.That(await DerivedFlowGraphFactory.CreateAsync(jim.FeatureFlags, [hr], _model.Types), Is.Null);
    }

    [Test]
    public async Task CreateAsync_FlagOn_ReturnsTheGraphOfEnabledMappingsOnlyAsync()
    {
        var hr = ImportRule(1, "HR Import", 1);
        var email = Expression(hr, 101, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"");
        var disabled = Expression(hr, 102, _model.UserPrincipalName, "mv[\"Email\"]", enabled: false);
        using var jim = Application(flagsOn: true);

        var graph = await DerivedFlowGraphFactory.CreateAsync(jim.FeatureFlags, [hr], _model.Types);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph, Is.Not.Null);
            Assert.That(graph!.IsDerived(email), Is.True);
            Assert.That(graph!.IsDerived(disabled), Is.False, "synchronisation evaluates enabled mappings only (plan decision 2)");
            Assert.That(graph!.MaxLevel(PersonTypeId), Is.EqualTo(1));
        }
    }

    [Test]
    public void CreateAsync_FlagOnWithACycle_ThrowsNamingEveryAttributeAndRuleOnIt()
    {
        var first = ImportRule(1, "HR Import", 1);
        Expression(first, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        var second = ImportRule(2, "AD Import", 2);
        Expression(second, 201, _model.MailNickname, "mv[\"Display Name\"]");
        using var jim = Application(flagsOn: true);

        var thrown = Assert.ThrowsAsync<DerivedFlowCycleException>(() => DerivedFlowGraphFactory.CreateAsync(jim.FeatureFlags, [first, second], _model.Types));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown!.Cycles, Has.Count.EqualTo(1));
            Assert.That(thrown!.Message, Does.Contain("Display Name"));
            Assert.That(thrown!.Message, Does.Contain("Mail Nickname"));
            Assert.That(thrown!.Message, Does.Contain("'HR Import'"));
            Assert.That(thrown!.Message, Does.Contain("'AD Import'"));
            Assert.That(thrown!.Message, Does.Contain("Person"));
        }
    }

    [Test]
    public async Task CreateAsync_FlagOnWithACycleThroughADisabledMapping_DoesNotThrowAsync()
    {
        // Disabling a mapping on a cycle is how an administrator breaks it (Phase 1), so the run-time graph, which
        // holds enabled mappings only, has no cycle to refuse.
        var hr = ImportRule(1, "HR Import", 1);
        Expression(hr, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        Expression(hr, 102, _model.MailNickname, "mv[\"Display Name\"]", enabled: false);
        using var jim = Application(flagsOn: true);

        var graph = await DerivedFlowGraphFactory.CreateAsync(jim.FeatureFlags, [hr], _model.Types);

        Assert.That(graph!.HasCycle, Is.False);
    }
}
