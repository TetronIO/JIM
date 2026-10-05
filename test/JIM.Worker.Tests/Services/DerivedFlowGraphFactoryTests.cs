// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Exceptions;
using JIM.Application.Services;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// <see cref="DerivedFlowGraphFactory"/> (#1750, plan decisions 11 and 12): the graph of enabled mappings every run,
/// preview and recall evaluates derived flows from, refusing to start a run over a dependency cycle.
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

    [Test]
    public void Create_ReturnsTheGraphOfEnabledMappingsOnly()
    {
        var hr = ImportRule(1, "HR Import", 1);
        var email = Expression(hr, 101, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"");
        var disabled = Expression(hr, 102, _model.UserPrincipalName, "mv[\"Email\"]", enabled: false);

        var graph = DerivedFlowGraphFactory.Create([hr], _model.Types);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.IsDerived(email), Is.True);
            Assert.That(graph.IsDerived(disabled), Is.False, "synchronisation evaluates enabled mappings only (plan decision 2)");
            Assert.That(graph.MaxLevel(PersonTypeId), Is.EqualTo(1));
        }
    }

    [Test]
    public void Create_WithACycle_ThrowsNamingEveryAttributeAndRuleOnIt()
    {
        var first = ImportRule(1, "HR Import", 1);
        Expression(first, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        var second = ImportRule(2, "AD Import", 2);
        Expression(second, 201, _model.MailNickname, "mv[\"Display Name\"]");

        var thrown = Assert.Throws<DerivedFlowCycleException>(() => DerivedFlowGraphFactory.Create([first, second], _model.Types));

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
    public void Create_WithACycleThroughADisabledMapping_DoesNotThrow()
    {
        // Disabling a mapping on a cycle is how an administrator breaks it (Phase 1), so the run-time graph, which
        // holds enabled mappings only, has no cycle to refuse.
        var hr = ImportRule(1, "HR Import", 1);
        Expression(hr, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        Expression(hr, 102, _model.MailNickname, "mv[\"Display Name\"]", enabled: false);

        var graph = DerivedFlowGraphFactory.Create([hr], _model.Types);

        Assert.That(graph.HasCycle, Is.False);
    }
}
