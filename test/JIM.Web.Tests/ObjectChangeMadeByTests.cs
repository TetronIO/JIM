// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

[TestFixture]
public class ObjectChangeMadeByTests
{
    [Test]
    public void Describe_SynchronisationRuleChange_NamesTheRule()
    {
        var change = new MvoChangeHistoryDto { SyncRuleName = "HR Inbound", InitiatedByType = ActivityInitiatorType.ApiKey, InitiatedByName = "Infrastructure Key" };

        Assert.That(ObjectChangeMadeBy.Describe(change), Is.EqualTo("HR Inbound"));
    }

    [Test]
    public void Describe_NoRule_NamesTheInitiator()
    {
        var change = new MvoChangeHistoryDto { InitiatedByType = ActivityInitiatorType.ApiKey, InitiatedByName = "Infrastructure Key" };

        Assert.That(ObjectChangeMadeBy.Describe(change), Is.EqualTo("Infrastructure Key"));
    }

    [Test]
    public void Describe_InitiatorTypeOnly_NamesTheType()
    {
        var change = new MvoChangeHistoryDto { InitiatedByType = ActivityInitiatorType.ApiKey };

        Assert.That(ObjectChangeMadeBy.Describe(change), Is.EqualTo("Api Key"));
    }

    [Test]
    public void Describe_NoInitiatorRecordedButSystemMadeIt_SaysSystem()
    {
        // A change recorded before its initiator was captured still says which mechanism made it.
        var change = new MvoChangeHistoryDto { InitiatedByType = ActivityInitiatorType.NotSet, ChangeInitiatorType = MetaverseObjectChangeInitiatorType.System };

        Assert.That(ObjectChangeMadeBy.Describe(change), Is.EqualTo("System"));
    }

    [Test]
    public void Describe_NothingRecorded_ReturnsNull()
    {
        Assert.That(ObjectChangeMadeBy.Describe(new MvoChangeHistoryDto()), Is.Null);
    }
}
