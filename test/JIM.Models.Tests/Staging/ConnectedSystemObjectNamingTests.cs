// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// The Connected System Object half of the shared naming policy (see <see cref="ObjectNaming"/>):
/// <see cref="ConnectedSystemObject.Name"/> resolves the ordered candidate attributes, and
/// <see cref="ConnectedSystemObject.NameOrId"/> falls through to the external id then the secondary
/// external id. Connector schemas are the customer's, not JIM's, so candidate matching is
/// case-insensitive and a missing candidate must degrade rather than throw.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectNamingTests
{
    private const int ExternalIdAttributeId = 100;
    private const int SecondaryExternalIdAttributeId = 200;

    private static ConnectedSystemObject CreateCso(params (string AttributeName, int AttributeId, string? Value)[] attributeValues)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ExternalIdAttributeId = ExternalIdAttributeId,
            SecondaryExternalIdAttributeId = SecondaryExternalIdAttributeId
        };

        foreach (var (attributeName, attributeId, value) in attributeValues)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = attributeId,
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = attributeId, Name = attributeName },
                StringValue = value
            });
        }

        return cso;
    }

    private static (string, int, string?) DisplayName(string? value) => ("displayName", 1, value);

    private static (string, int, string?) CommonName(string? value) => ("cn", 2, value);

    private static (string, int, string?) NameAttribute(string? value) => ("name", 3, value);

    private static (string, int, string?) ExternalId(string? value) => ("entryUUID", ExternalIdAttributeId, value);

    private static (string, int, string?) SecondaryExternalId(string? value) => ("distinguishedName", SecondaryExternalIdAttributeId, value);

    [Test]
    public void Name_DisplayNamePresent_PrefersDisplayName()
    {
        var cso = CreateCso(CommonName("Project-GlobalGateway"), DisplayName("Global Gateway Project"), NameAttribute("gg"));

        Assert.That(cso.Name, Is.EqualTo("Global Gateway Project"));
    }

    [Test]
    public void Name_NoDisplayNameButCommonNamePresent_ReturnsCommonName()
    {
        // The defect this policy exists to fix: LDAP group objects carry cn but no displayName, so the
        // label fell through to the external id (a guid) and rendered as an unreadable identifier.
        var cso = CreateCso(CommonName("Project-GlobalGateway"), ExternalId("1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e"));

        Assert.That(cso.Name, Is.EqualTo("Project-GlobalGateway"));
    }

    [Test]
    public void Name_OnlyNameAttributePresent_ReturnsNameAttribute()
    {
        var cso = CreateCso(NameAttribute("Contractors"), ExternalId("abc"));

        Assert.That(cso.Name, Is.EqualTo("Contractors"));
    }

    [Test]
    public void Name_CandidateAttributeCasingDiffers_MatchesCaseInsensitively()
    {
        // Connector schemas are the customer's; JIM cannot dictate their attribute name casing.
        var cso = CreateCso(("DISPLAYNAME", 1, "Erin Byrne"));

        Assert.That(cso.Name, Is.EqualTo("Erin Byrne"));
    }

    [Test]
    public void Name_DisplayNameWhitespaceOnly_FallsThroughToCommonName()
    {
        var cso = CreateCso(DisplayName("   "), CommonName("Project-GlobalGateway"));

        Assert.That(cso.Name, Is.EqualTo("Project-GlobalGateway"));
    }

    [Test]
    public void Name_NoCandidateAttributes_ReturnsNull()
    {
        var cso = CreateCso(ExternalId("abc"), ("description", 9, "a group"));

        Assert.That(cso.Name, Is.Null);
    }

    [Test]
    public void Name_NoAttributeValuesAtAll_ReturnsNull()
    {
        var cso = CreateCso();

        Assert.That(cso.Name, Is.Null);
    }

    [Test]
    public void Name_DuplicateCandidateValues_ReturnsAValueRatherThanThrowing()
    {
        // The previous implementation used SingleOrDefault, which threw when a schema produced two
        // values for the same candidate attribute.
        var cso = CreateCso(DisplayName("First"), DisplayName("Second"));

        Assert.That(cso.Name, Is.AnyOf("First", "Second"));
    }

    [Test]
    public void NameOrId_NameAbsent_FallsBackToExternalId()
    {
        var cso = CreateCso(ExternalId("1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e"), SecondaryExternalId("cn=x,ou=Groups"));

        Assert.That(cso.NameOrId, Is.EqualTo("1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e"));
    }

    [Test]
    public void NameOrId_NameAndExternalIdAbsent_FallsBackToSecondaryExternalId()
    {
        var cso = CreateCso(SecondaryExternalId("cn=Project-GlobalGateway,ou=Groups,dc=glitterband,dc=local"));

        Assert.That(cso.NameOrId, Is.EqualTo("cn=Project-GlobalGateway,ou=Groups,dc=glitterband,dc=local"));
    }

    [Test]
    public void NameOrId_CommonNamePresent_PrefersNameOverExternalId()
    {
        var cso = CreateCso(CommonName("Project-GlobalGateway"), ExternalId("1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e"));

        Assert.That(cso.NameOrId, Is.EqualTo("Project-GlobalGateway"));
    }

    [Test]
    public void NameOrId_NothingResolvable_ReturnsNull()
    {
        var cso = CreateCso(("description", 9, "a group"));

        Assert.That(cso.NameOrId, Is.Null);
    }
}
