// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests the Activity target link helpers: an Activity's target should deep-link to the surface where its subject is
/// actually managed (a mapping to the rule's Attribute Flow tab, a schema import to the system's Schema tab), not
/// just the owning object's default tab.
/// </summary>
[TestFixture]
public class ActivityTargetHrefTests
{
    [Test]
    public void GetSyncRuleActivityHref_MappingActivity_DeepLinksToAttributeFlowTab()
    {
        var href = Helpers.GetSyncRuleActivityHref(4, $"{Activity.SyncRuleMappingTargetNamePrefix}company");

        Assert.That(href, Is.EqualTo("/admin/sync-rules/4?t=attribute-flow"),
            "mappings are managed on the Synchronisation Rule's Attribute Flow tab");
    }

    [Test]
    public void GetSyncRuleActivityHref_RuleActivity_LinksToRulePage()
    {
        var href = Helpers.GetSyncRuleActivityHref(4, "AD to HR Inbound");

        Assert.That(href, Is.EqualTo("/admin/sync-rules/4"));
    }

    [Test]
    public void GetSyncRuleActivityHref_NoRuleId_ReturnsNull()
    {
        var href = Helpers.GetSyncRuleActivityHref(null, $"{Activity.SyncRuleMappingTargetNamePrefix}company");

        Assert.That(href, Is.Null, "without a rule id there is nothing to link to; the caller falls back to plain text");
    }

    [Test]
    public void GetConnectedSystemActivityHref_SchemaImport_DeepLinksToSchemaTab()
    {
        var href = Helpers.GetConnectedSystemActivityHref(2, ActivityTargetOperationType.ImportSchema);

        Assert.That(href, Is.EqualTo("/admin/connected-systems/2/?t=schema"));
    }

    [Test]
    public void GetConnectedSystemActivityHref_HierarchyImport_DeepLinksToPartitionsTab()
    {
        var href = Helpers.GetConnectedSystemActivityHref(2, ActivityTargetOperationType.ImportHierarchy);

        Assert.That(href, Is.EqualTo("/admin/connected-systems/2/?t=partitions-containers"));
    }

    [Test]
    public void GetConnectedSystemActivityHref_OtherOperation_LinksToSystemPage()
    {
        var href = Helpers.GetConnectedSystemActivityHref(2, ActivityTargetOperationType.Update);

        Assert.That(href, Is.EqualTo("/admin/connected-systems/2/"));
    }

    [Test]
    public void GetConnectedSystemActivityHref_NoSystemId_ReturnsNull()
    {
        var href = Helpers.GetConnectedSystemActivityHref(null, ActivityTargetOperationType.Update);

        Assert.That(href, Is.Null);
    }
}
