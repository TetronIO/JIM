// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers ConnectorDefinedChip (#2756), the marker beside a Connector-defined Attribute wherever an administrator reads
/// or picks a Connected System attribute. It appears only for an attribute the Connector made up, and its explanation
/// names the Connector and, where one is declared, the attribute it is written through.
/// </summary>
[TestFixture]
public class ConnectorDefinedChipTests : JimComponentTestContext
{
    [Test]
    public void ConnectorDefinedChip_ConnectorDefinedAttribute_RendersTheChip()
    {
        var cut = Render<ConnectorDefinedChip>(p => p
            .Add(c => c.Attribute, ExchangeAlias())
            .Add(c => c.ConnectorName, "JIM LDAP Connector"));

        Assert.That(cut.HasComponent<MudChip<string>>(), Is.True);
    }

    [Test]
    public void ConnectorDefinedChip_TheSystemsOwnAttribute_RendersNothing()
    {
        var cut = Render<ConnectorDefinedChip>(p => p
            .Add(c => c.Attribute, new ConnectedSystemObjectTypeAttribute { Name = "mailNickname", Type = AttributeDataType.Text })
            .Add(c => c.ConnectorName, "JIM LDAP Connector"));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void ConnectorDefinedChip_NoAttribute_RendersNothing()
    {
        var cut = Render<ConnectorDefinedChip>(p => p.Add(c => c.ConnectorName, "JIM LDAP Connector"));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void Explanation_DeclaredSource_NamesTheConnectorTheDescriptionAndTheSource()
    {
        var explanation = ConnectorDefinedChip.Explanation(ExchangeAlias(), "JIM LDAP Connector");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(explanation, Does.Contain("JIM LDAP Connector"));
            Assert.That(explanation, Does.Contain("The recipient's Exchange alias."));
            Assert.That(explanation, Does.Contain("mailNickname"));
        }
    }

    [Test]
    public void Explanation_NoDescriptionNoSourceNoConnectorName_StillSaysWhatTheAttributeIs()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Name = "Widget Mode", Type = AttributeDataType.Text, IsConnectorDefined = true };

        var explanation = ConnectorDefinedChip.Explanation(attribute, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(explanation, Does.Contain("Connector"));
            Assert.That(explanation, Does.Not.Contain("through"));
        }
    }

    private static ConnectedSystemObjectTypeAttribute ExchangeAlias() => new()
    {
        Name = "Exchange Alias",
        Type = AttributeDataType.Text,
        IsConnectorDefined = true,
        SourceAttributeName = "mailNickname",
        Description = "The recipient's Exchange alias. When empty, the object's mailNickname is used."
    };
}
