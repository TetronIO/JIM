// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers how a reference attribute value points at another Connected System Object: the referenced
/// object's type, a link to it, and its Secondary External ID (the DN, for LDAP systems).
/// <para>
/// The Secondary External ID is an identifier like every other one on these pages (external IDs,
/// DNs, GUIDs, raw string values), so it renders in the code font, low-lighted, and separated from
/// the object link by a single hyphen rather than wrapped in brackets. Brackets read as an aside;
/// this is the identifier an administrator copies out and searches the Connected System with.
/// </para>
/// </summary>
[TestFixture]
public class CsoReferenceLinkTests : JimComponentTestContext
{
    private const string Dn = "uid=alice.smith400,ou=People,dc=glitterband,dc=local";
    private static readonly Guid ReferencedObjectId = Guid.Parse("b96a6ed2-2c19-1041-8f96-8b24144ed420");

    [Test]
    public void CsoReferenceLink_WithSecondaryExternalId_SeparatesItFromTheLinkWithAHyphen()
    {
        var cut = Render<CsoReferenceLink>(p => p.Add(c => c.ReferenceValue, BuildReferencedObject(Dn)));

        Assert.That(cut.Find("span.jim-attr-secondary-id").TextContent.Trim(), Is.EqualTo($"- {Dn}"));
    }

    [Test]
    public void CsoReferenceLink_WithSecondaryExternalId_RendersItInTheCodeFont()
    {
        var cut = Render<CsoReferenceLink>(p => p.Add(c => c.ReferenceValue, BuildReferencedObject(Dn)));

        // jim-text-code is JIM's own class (site.css), not MudBlazor markup, so it is safe to assert on.
        Assert.That(cut.Find("span.jim-attr-secondary-id").ClassList, Does.Contain("jim-text-code"));
    }

    [Test]
    public void CsoReferenceLink_WithSecondaryExternalId_DoesNotWrapItInBrackets()
    {
        var cut = Render<CsoReferenceLink>(p => p.Add(c => c.ReferenceValue, BuildReferencedObject(Dn)));

        var suffix = cut.Find("span.jim-attr-secondary-id").TextContent;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(suffix, Does.Not.Contain("("));
            Assert.That(suffix, Does.Not.Contain(")"));
        }
    }

    [Test]
    public void CsoReferenceLink_WithoutSecondaryExternalId_RendersNoSuffix()
    {
        var cut = Render<CsoReferenceLink>(p => p.Add(c => c.ReferenceValue, BuildReferencedObject(null)));

        Assert.That(cut.FindAll("span.jim-attr-secondary-id"), Is.Empty);
    }

    [Test]
    public void CsoReferenceLink_AlwaysLinksToTheReferencedObject()
    {
        var cut = Render<CsoReferenceLink>(p => p.Add(c => c.ReferenceValue, BuildReferencedObject(Dn)));

        var link = cut.Find("a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.GetAttribute("href"), Is.EqualTo($"/admin/connected-systems/2/connector-space/{ReferencedObjectId}"));
            Assert.That(link.TextContent.Trim(), Is.EqualTo("Alice Smith (S8-400)"));
        }
    }

    [Test]
    public void CsoReferenceLink_AlwaysRendersTheReferencedObjectType()
    {
        var cut = Render<CsoReferenceLink>(p => p.Add(c => c.ReferenceValue, BuildReferencedObject(Dn)));

        Assert.That(cut.Markup, Does.Contain("jimPerson"));
    }

    /// <summary>
    /// An LDAP-shaped Connected System Object: an external ID (entryUUID), a display name, and
    /// optionally a Secondary External ID (the DN), which not every Connected System provides.
    /// </summary>
    private static ConnectedSystemObject BuildReferencedObject(string? secondaryExternalId)
    {
        const int externalIdAttributeId = 10;
        const int secondaryExternalIdAttributeId = 11;
        const int displayNameAttributeId = 12;

        var connectedSystemObject = new ConnectedSystemObject
        {
            Id = ReferencedObjectId,
            ConnectedSystemId = 2,
            Type = new ConnectedSystemObjectType { Id = 1, Name = "jimPerson" },
            ExternalIdAttributeId = externalIdAttributeId,
            SecondaryExternalIdAttributeId = secondaryExternalId == null ? null : secondaryExternalIdAttributeId
        };

        connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = externalIdAttributeId,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = externalIdAttributeId, Name = "entryUUID", Type = AttributeDataType.Text, IsExternalId = true },
            StringValue = ReferencedObjectId.ToString()
        });

        connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = displayNameAttributeId,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = displayNameAttributeId, Name = "displayName", Type = AttributeDataType.Text },
            StringValue = "Alice Smith (S8-400)"
        });

        if (secondaryExternalId != null)
        {
            connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = secondaryExternalIdAttributeId,
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = secondaryExternalIdAttributeId, Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true },
                StringValue = secondaryExternalId
            });
        }

        return connectedSystemObject;
    }
}
