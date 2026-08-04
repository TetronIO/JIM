// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Models.Staging.DTOs;
using JIM.Web.Causality;
using JIM.Web.Pages;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Regression coverage for the Run Profile Execution Item detail page's causality page-context
/// wiring. <see cref="CausalityPageContext.ConnectedSystemId"/>/<see cref="CausalityPageContext.ConnectedSystemName"/>
/// must describe the Connected System the Run Profile executed against (the Activity's system), while
/// <see cref="CausalityPageContext.CsoConnectedSystemId"/>/<see cref="CausalityPageContext.CsoConnectedSystemName"/>
/// must independently describe the item's Connected System Object's own system. These normally
/// coincide, but diverge for cross-system cascades, e.g. a Full Sync on system A provisioning or
/// exporting to a Connected System Object on system B: the run and record identities must each stay
/// correct for their own consumers (see <see cref="CausalityPageContext"/> for the full list).
/// </summary>
/// <remarks>
/// This page has no existing bUnit render harness (it depends on IJimApplicationFactory,
/// NavigationManager and [Authorize]/cascading auth state, none of which any page-level test in this
/// project sets up yet), so the field-to-context mapping is exercised directly via reflection over
/// the page's private state rather than a full render. The async resolution in OnParametersSetAsync
/// (which chooses between reusing the already-loaded header and issuing a fresh lookup) is data
/// plumbing over an already-tested repository call and is covered by build + runtime verification,
/// not by this suite.
/// </remarks>
[TestFixture]
public class ActivityRunProfileExecutionItemDetailTests
{
    [Test]
    public void BuildCausalityPageContext_RunSystemDiffersFromObjectSystem_UsesRunSystem()
    {
        // Arrange: a cross-system cascade, e.g. a Full Sync on Yellowstone APAC (the run's system)
        // producing an execution item for a Connected System Object that lives on Glitterband EMEA.
        var runSystem = new ConnectedSystemHeader { Id = 1, Name = "Yellowstone APAC" };
        var objectSystem = new ConnectedSystemHeader { Id = 2, Name = "Glitterband EMEA" };

        var page = new ActivityRunProfileExecutionItemDetail();
        SetPrivateField(page, "_runConnectedSystemHeader", runSystem);
        SetPrivateField(page, "_connectedSystemHeader", objectSystem);

        // Act
        var context = InvokeBuildCausalityPageContext(page);

        // Assert: ConnectedSystemId/Name name the run's system, not the object's own system...
        Assert.That(context.ConnectedSystemId, Is.EqualTo(runSystem.Id));
        Assert.That(context.ConnectedSystemName, Is.EqualTo(runSystem.Name));
        // ...while CsoConnectedSystemId/Name independently carry the object's own system, so
        // record-scoped consumers (the record hyperlink, per-event system badges) still get it right.
        Assert.That(context.CsoConnectedSystemId, Is.EqualTo(objectSystem.Id));
        Assert.That(context.CsoConnectedSystemName, Is.EqualTo(objectSystem.Name));
    }

    [Test]
    public void BuildCausalityPageContext_RunSystemSameAsObjectSystem_UsesSharedSystem()
    {
        // Arrange: the common case, where the run executed directly against the object's own system.
        var sharedSystem = new ConnectedSystemHeader { Id = 3, Name = "Yellowstone APAC" };

        var page = new ActivityRunProfileExecutionItemDetail();
        SetPrivateField(page, "_runConnectedSystemHeader", sharedSystem);
        SetPrivateField(page, "_connectedSystemHeader", sharedSystem);

        // Act
        var context = InvokeBuildCausalityPageContext(page);

        // Assert
        Assert.That(context.ConnectedSystemId, Is.EqualTo(sharedSystem.Id));
        Assert.That(context.ConnectedSystemName, Is.EqualTo(sharedSystem.Name));
        Assert.That(context.CsoConnectedSystemId, Is.EqualTo(sharedSystem.Id));
        Assert.That(context.CsoConnectedSystemName, Is.EqualTo(sharedSystem.Name));
    }

    [Test]
    public void BuildCausalityPageContext_RunSystemUnresolved_DegradesToNullRatherThanObjectSystem()
    {
        // Arrange: the run's Connected System could not be resolved (e.g. an Activity with no
        // ConnectedSystemId). The panel must degrade to null rather than silently falling back to
        // the object's own system, per CausalityPageContext's documented "degrade gracefully" contract.
        var objectSystem = new ConnectedSystemHeader { Id = 2, Name = "Glitterband EMEA" };

        var page = new ActivityRunProfileExecutionItemDetail();
        SetPrivateField(page, "_connectedSystemHeader", objectSystem);
        // _runConnectedSystemHeader deliberately left unset (null).

        // Act
        var context = InvokeBuildCausalityPageContext(page);

        // Assert: the run identity degrades to null...
        Assert.That(context.ConnectedSystemId, Is.Null);
        Assert.That(context.ConnectedSystemName, Is.Null);
        // ...independently of the object identity, which is unaffected and still resolves.
        Assert.That(context.CsoConnectedSystemId, Is.EqualTo(objectSystem.Id));
        Assert.That(context.CsoConnectedSystemName, Is.EqualTo(objectSystem.Name));
    }

    private static void SetPrivateField(ActivityRunProfileExecutionItemDetail target, string fieldName, object? value)
    {
        var field = typeof(ActivityRunProfileExecutionItemDetail)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(field, Is.Not.Null, $"Expected private field '{fieldName}' to exist on the page.");
        field!.SetValue(target, value);
    }

    private static CausalityPageContext InvokeBuildCausalityPageContext(ActivityRunProfileExecutionItemDetail target)
    {
        var method = typeof(ActivityRunProfileExecutionItemDetail)
            .GetMethod("BuildCausalityPageContext", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null, "Expected private method 'BuildCausalityPageContext' to exist on the page.");
        return (CausalityPageContext)method!.Invoke(target, null)!;
    }
}
