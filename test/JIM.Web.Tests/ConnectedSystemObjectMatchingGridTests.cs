// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Preview;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Object Matching tab's rule grid.
/// </summary>
/// <remarks>
/// Order is the configuration here, not a presentation choice: rules are evaluated in ascending Order until one
/// matches, and the move up / move down buttons act on that sequence. So the grid must window the rules in Order
/// whatever else is asked of it, and must carry no sort headers offering the reader another sequence to read them in.
/// </remarks>
[TestFixture]
public class ConnectedSystemObjectMatchingGridTests : JimComponentTestContext
{
    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new UnusedJimApplicationFactory());
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
        Services.AddSingleton<IConfigurationChangePreviewStarter>(new UnusedPreviewStarter());
        // The Add Matching Rule panel judges the rule being composed as the save would (#2052).
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
        // The test cases panel beneath the rules reads its cases when it renders (#2055 Phase 4); it is not the grid
        // this fixture covers, and stubbing it keeps the guard below about the tab itself.
        ComponentFactories.AddStub<JIM.Web.Pages.Admin.Components.ConfigurationTestCasesPanel>();
    }

    /// <summary>
    /// Likewise for the preview: the tab starts one only when an administrator asks for it (#1457), so merely
    /// rendering must not.
    /// </summary>
    private sealed class UnusedPreviewStarter : IConfigurationChangePreviewStarter
    {
        public Task<Guid?> StartAsync(ConfigurationChangePreviewRequest request) =>
            throw new InvalidOperationException("The Object Matching tab started a preview while merely rendering, which it should not do.");
    }

    /// <summary>
    /// The tab only reaches the application layer when an administrator adds, reorders or deletes a rule, and this
    /// fixture only renders it, so this exists to satisfy the injection and throws if that ever changes.
    /// </summary>
    private sealed class UnusedJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication Create() =>
            throw new InvalidOperationException("The Object Matching tab reached the application layer while merely rendering, which it should not do.");
    }

    [Test]
    public void ObjectMatchingTab_Rules_AreWindowedInOrderSequenceWhateverOrderTheyWereAddedIn()
    {
        // Reading the rules in any other sequence would misrepresent which one JIM tries first.
        var connectedSystem = ConnectedSystemWithMatchingRules();

        var component = RenderMatchingTab(connectedSystem);

        var markup = component.Markup;
        Assert.That(markup.IndexOf("employeeId", StringComparison.Ordinal),
            Is.LessThan(markup.IndexOf("mail", StringComparison.Ordinal)),
            "the Order 0 rule is evaluated first, so it is the first row");
    }

    [Test]
    public void ObjectMatchingTab_Rules_OfferNoColumnSortToReorderThemBy()
    {
        // A sortable heading here would let the reader put the rules in an order JIM never evaluates them in,
        // beside move up / move down buttons that act on the real one.
        var connectedSystem = ConnectedSystemWithMatchingRules();

        var component = RenderMatchingTab(connectedSystem);

        Assert.That(component.FindComponents<VirtualisedSortHeader>(), Is.Empty);
    }

    [Test]
    public void ObjectMatchingTab_ExpressionRule_ShowsItsExpressionAsTheMatchValue()
    {
        // #2052: an expression rule has no source attribute, and a Match Value column that showed only attributes
        // would render it as an empty row.
        var connectedSystem = ConnectedSystemWithMatchingRules();
        var objectType = connectedSystem.ObjectTypes!.Single();
        objectType.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            Id = 3,
            Order = 2,
            Sources = [new ObjectMatchingRuleSource { Order = 0, Expression = "\"E\" + Trim(cs[\"employeeId\"])" }],
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 3, Name = "Employee Number" }
        });

        var component = RenderMatchingTab(connectedSystem);

        Assert.That(component.FindComponents<AttributeChip>().Select(c => c.Instance)
                .Where(c => c.Kind == AttributeChipKind.Expression).Select(c => c.Expression),
            Does.Contain("\"E\" + Trim(cs[\"employeeId\"])"));
    }

    private IRenderedComponent<ConnectedSystemObjectMatchingTab> RenderMatchingTab(ConnectedSystem connectedSystem)
    {
        return Render<ConnectedSystemObjectMatchingTab>(parameters => parameters
            .Add(p => p.ConnectedSystem, connectedSystem)
            .Add(p => p.MetaverseAttributes, new List<MetaverseAttribute>())
            .Add(p => p.MetaverseObjectTypes, new List<MetaverseObjectType>()));
    }

    /// <summary>
    /// A Connected System with one managed object type carrying two Object Matching Rules, held in the collection
    /// in the reverse of the order they are evaluated in.
    /// </summary>
    private static ConnectedSystem ConnectedSystemWithMatchingRules()
    {
        var mailAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "mail", Type = AttributeDataType.Text };
        var employeeIdAttribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "employeeId", Type = AttributeDataType.Text };

        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            ConnectedSystemId = 1,
            Selected = true,
            Attributes = [mailAttribute, employeeIdAttribute],
            ObjectMatchingRules =
            [
                Rule(1, order: 1, mailAttribute, "Email"),
                Rule(2, order: 0, employeeIdAttribute, "Employee Id")
            ]
        };

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Yellowstone",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "JIM LDAP Connector" },
            ObjectTypes = [objectType]
        };
    }

    private static ObjectMatchingRule Rule(int id, int order, ConnectedSystemObjectTypeAttribute source, string targetName) => new()
    {
        Id = id,
        Order = order,
        Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = source }],
        TargetMetaverseAttribute = new MetaverseAttribute { Id = id, Name = targetName }
    };
}
