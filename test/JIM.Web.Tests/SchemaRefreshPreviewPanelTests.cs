// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Staging.DTOs;
using JIM.Web.Pages.Admin.Components;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the schema refresh preview panel: the decision point a refresh now pauses at (#421). What matters is
/// that the panel tells the truth about what an apply does (removals are retained, not deleted), that additions
/// alone raise no alarm, and that the discard path only asks for confirmation when discarding actually leaves
/// JIM's schema adrift of the Connected System's.
/// </summary>
[TestFixture]
public class SchemaRefreshPreviewPanelTests : JimComponentTestContext
{
    [Test]
    public void PreviewPanel_WithRemovalsAndDefinitionChanges_WarnsAndShowsEveryChange()
    {
        var result = new SchemaRefreshResult
        {
            Success = true,
            TotalObjectTypes = 1,
            TotalAttributes = 2,
            RemovedObjectTypes = ["computer"],
            RemovedAttributes = new Dictionary<string, List<string>> { ["user"] = ["department"] }
        };
        result.AddChangedAttribute("user", new SchemaAttributeDefinitionChange
        {
            AttributeName = "displayName",
            Aspect = SchemaAttributeChangeAspect.DataType,
            OldValue = "Reference",
            NewValue = "Text"
        });

        var cut = Render<SchemaRefreshPreviewPanel>(p => p.Add(c => c.Result, result));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("computer"));
            Assert.That(cut.Markup, Does.Contain("department"));
            Assert.That(cut.Markup, Does.Contain("displayName: data type Reference"), "A definition change must be shown, not applied silently.");
            Assert.That(cut.Markup, Does.Contain("retained"), "The panel must say removals are kept, not deleted; the old warning claimed the opposite.");
            Assert.That(cut.FindAll("[data-testid=jim-apply-schema-refresh]"), Is.Empty,
                "On a destructive diff the administrator decides between the three options; a plain apply is no longer one of them (#1485).");
            Assert.That(cut.FindAll("[data-testid=jim-apply-remove-schema-refresh]"), Has.Count.EqualTo(1),
                "Apply and Remove is the full-commitment choice on a destructive diff.");
            Assert.That(cut.FindAll("[data-testid=jim-discard-schema-refresh]"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void PreviewPanel_WithAdditionsOnly_DoesNotWarnAboutRetentionOrDrift()
    {
        var result = new SchemaRefreshResult
        {
            Success = true,
            TotalObjectTypes = 1,
            TotalAttributes = 3,
            AddedAttributes = new Dictionary<string, List<string>> { ["user"] = ["mobile"] }
        };

        var cut = Render<SchemaRefreshPreviewPanel>(p => p.Add(c => c.Result, result));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("mobile"));
            Assert.That(cut.Markup, Does.Not.Contain("retained"), "Additions cannot break anything; no drift warning applies.");
            Assert.That(cut.FindAll("[data-testid=jim-apply-schema-refresh]"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll("[data-testid=jim-apply-remove-schema-refresh]"), Is.Empty,
                "Additions have nothing to remove; the plain apply stands (#421 behaviour).");
        }
    }

    [Test]
    public void PreviewPanel_WithNoChanges_SaysUpToDateAndOffersNothingToApply()
    {
        var result = SchemaRefreshResult.NoChanges(3, 42);

        var cut = Render<SchemaRefreshPreviewPanel>(p => p.Add(c => c.Result, result));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("up to date"));
            Assert.That(cut.FindAll("[data-testid=jim-apply-schema-refresh]"), Is.Empty, "There is nothing to apply.");
            Assert.That(cut.FindAll("[data-testid=jim-close-schema-refresh]"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Discard_WithAdditionsOnly_InvokesOnDiscardWithoutAskingForConfirmation()
    {
        // Discarding additions costs nothing (the next refresh finds them again), so a confirmation dialog here
        // would be friction with no protective value.
        var result = new SchemaRefreshResult
        {
            Success = true,
            AddedAttributes = new Dictionary<string, List<string>> { ["user"] = ["mobile"] }
        };
        var discarded = false;

        var cut = Render<SchemaRefreshPreviewPanel>(p => p
            .Add(c => c.Result, result)
            .Add(c => c.OnDiscard, () => discarded = true));

        cut.Find("[data-testid=jim-discard-schema-refresh]").Click();

        Assert.That(discarded, Is.True);
    }

    [Test]
    public void PreviewPanel_WithDependents_OffersApplyAndDisableAndNamesThem()
    {
        var result = new SchemaRefreshResult
        {
            Success = true,
            RemovedObjectTypes = ["computer"]
        };
        var dependents = new SchemaRefreshDependents();
        dependents.InvalidatedSyncRules.Add(new SchemaRefreshDependentRule
        {
            SyncRuleId = 10,
            SyncRuleName = "Directory Computers Inbound",
            ObjectTypeName = "computer",
            MappingCount = 4,
            Reason = "Object Type 'computer' is no longer reported by the Connected System (schema refresh of 21 Aug 2026)."
        });

        var cut = Render<SchemaRefreshPreviewPanel>(p => p
            .Add(c => c.Result, result)
            .Add(c => c.Dependents, dependents));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid=jim-apply-disable-schema-refresh]"), Has.Count.EqualTo(1),
                "A destructive diff with dependents must offer the protective middle option.");
            Assert.That(cut.Markup, Does.Contain("Directory Computers Inbound"),
                "The dependents are named on the review so the cost is visible before any dialog opens.");
        }
    }

    [Test]
    public void PreviewPanel_WithDestructiveChangesButNoDependents_OffersNoDisableOption()
    {
        // Nothing references the removed entries, so there is nothing to disable and the option would be a
        // dead affordance.
        var result = new SchemaRefreshResult
        {
            Success = true,
            RemovedObjectTypes = ["computer"]
        };

        var cut = Render<SchemaRefreshPreviewPanel>(p => p
            .Add(c => c.Result, result)
            .Add(c => c.Dependents, new SchemaRefreshDependents()));

        Assert.That(cut.FindAll("[data-testid=jim-apply-disable-schema-refresh]"), Is.Empty);
    }

    [Test]
    public void ApplyAndDisable_ShowsThePlanThenConfirmsThroughIt()
    {
        var result = new SchemaRefreshResult { Success = true, RemovedObjectTypes = ["computer"] };
        var dependents = new SchemaRefreshDependents();
        dependents.InvalidatedSyncRules.Add(new SchemaRefreshDependentRule
        {
            SyncRuleId = 10,
            SyncRuleName = "Directory Computers Inbound",
            ObjectTypeName = "computer",
            MappingCount = 4,
            Reason = "Object Type 'computer' is no longer reported by the Connected System (schema refresh of 21 Aug 2026)."
        });
        var applied = false;

        // The plan is a MudDialog, which renders through the dialog provider rather than inside the panel's
        // own tree, so the provider is rendered alongside and the dialog's content asserted through it.
        var provider = Render<MudDialogProvider>();
        var cut = Render<SchemaRefreshPreviewPanel>(p => p
            .Add(c => c.Result, result)
            .Add(c => c.Dependents, dependents)
            .Add(c => c.OnApplyWithDisables, () => applied = true));

        cut.Find("[data-testid=jim-apply-disable-schema-refresh]").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("no longer reported"),
                "The plan dialog states each reason before anything is committed.");
            Assert.That(applied, Is.False, "Opening the plan must not apply it.");
        }

        provider.Find("[data-testid=jim-confirm-apply-disable]").Click();
        Assert.That(applied, Is.True);
    }

    [Test]
    public void ApplyAndRemove_ShowsTheImpactAndTheExactWarningThenConfirmsThroughIt()
    {
        var result = new SchemaRefreshResult
        {
            Success = true,
            RemovedObjectTypes = ["computer"],
            RemovedAttributes = new Dictionary<string, List<string>> { ["user"] = ["faxNumber"] }
        };
        var dependents = new SchemaRefreshDependents();
        dependents.InvalidatedSyncRules.Add(new SchemaRefreshDependentRule
        {
            SyncRuleId = 10,
            SyncRuleName = "Directory Computers Inbound",
            ObjectTypeName = "computer",
            MappingCount = 4,
            Reason = "Object Type 'computer' is no longer reported by the Connected System (schema refresh of 21 Aug 2026)."
        });
        var impact = new SchemaRefreshRemovalImpact();
        impact.RemovedObjectTypes.Add(new SchemaRefreshRemovalTypeImpact { ObjectTypeId = 2, ObjectTypeName = "computer", ConnectedSystemObjectCount = 1204 });
        impact.RemovedAttributes.Add(new SchemaRefreshRemovalAttributeImpact { AttributeId = 13, AttributeName = "faxNumber", ObjectTypeName = "user", StoredValueCount = 87 });
        var applied = false;

        var provider = Render<MudDialogProvider>();
        var cut = Render<SchemaRefreshPreviewPanel>(p => p
            .Add(c => c.Result, result)
            .Add(c => c.Dependents, dependents)
            .Add(c => c.RemovalImpact, impact)
            .Add(c => c.OnApplyWithRemovals, () => applied = true));

        cut.Find("[data-testid=jim-apply-remove-schema-refresh]").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("This deletes configuration and identity data."),
                "The dialog leads with the exact consequence, in the words the design fixed.");
            Assert.That(provider.Markup, Does.Contain("1,204"), "The Connected System Object count is part of the decision.");
            Assert.That(provider.Markup, Does.Contain("87"), "The stored value count is part of the decision.");
            Assert.That(provider.Markup, Does.Contain("Directory Computers Inbound"));
            Assert.That(applied, Is.False, "Opening the plan must not apply it.");
        }

        provider.Find("[data-testid=jim-confirm-apply-remove]").Click();
        Assert.That(applied, Is.True);
    }

    [Test]
    public void PreviewPanel_SeparatesAdditionsFromDestructiveChanges()
    {
        var result = new SchemaRefreshResult
        {
            Success = true,
            AddedAttributes = new Dictionary<string, List<string>> { ["user"] = ["mobile"] },
            RemovedAttributes = new Dictionary<string, List<string>> { ["user"] = ["faxNumber"] }
        };

        var cut = Render<SchemaRefreshPreviewPanel>(p => p.Add(c => c.Result, result));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Additions"), "Safe changes get their own group.");
            Assert.That(cut.Markup, Does.Contain("Destructive"), "Destructive changes get their own, visually distinct group.");
        }
    }

    [Test]
    public void Apply_InvokesOnApply()
    {
        var result = new SchemaRefreshResult
        {
            Success = true,
            AddedAttributes = new Dictionary<string, List<string>> { ["user"] = ["mobile"] }
        };
        var applied = false;

        var cut = Render<SchemaRefreshPreviewPanel>(p => p
            .Add(c => c.Result, result)
            .Add(c => c.OnApply, () => applied = true));

        cut.Find("[data-testid=jim-apply-schema-refresh]").Click();

        Assert.That(applied, Is.True);
    }
}
