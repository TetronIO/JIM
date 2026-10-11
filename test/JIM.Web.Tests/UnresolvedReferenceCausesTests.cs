// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Which attribute values the Connected System Object page diagnoses for its inline cause chips (#959): the same values
/// the Unresolved References section lists, so a chip never appears beside a value the section leaves out.
/// </summary>
[TestFixture]
public class UnresolvedReferenceCausesTests
{
    [Test]
    public void IsUnresolved_ImportedReferenceNotLinked_IsTrue()
    {
        Assert.That(UnresolvedReferenceCauses.IsUnresolved(new ConnectedSystemObjectAttributeValue
        {
            UnresolvedReferenceValue = "CN=Ana Silva,OU=Contractors,DC=corp,DC=example,DC=com"
        }), Is.True);
    }

    [Test]
    public void IsUnresolved_ResolvedSince_IsFalse()
    {
        // The imported text is kept beside the reference once it resolves.
        Assert.That(UnresolvedReferenceCauses.IsUnresolved(new ConnectedSystemObjectAttributeValue
        {
            UnresolvedReferenceValue = "CN=Ana Silva,OU=Contractors,DC=corp,DC=example,DC=com",
            ReferenceValueId = Guid.NewGuid()
        }), Is.False);
    }

    [Test]
    public void IsUnresolved_NotAReference_IsFalse()
    {
        Assert.That(UnresolvedReferenceCauses.IsUnresolved(new ConnectedSystemObjectAttributeValue { StringValue = "Ana Silva" }), Is.False);
    }
}
