// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

[TestFixture]
public class ConnectedSystemExportResultTests
{
    [Test]
    public void ValueAlreadyInUse_NamedAttribute_IsAClassifiedFailureCarryingTheAttributeAndMessage()
    {
        var result = ConnectedSystemExportResult.ValueAlreadyInUse("00002071: samldb: sAMAccountName 'jimprobe1' already in use!", "sAMAccountName");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse));
            Assert.That(result.RejectedAttributeName, Is.EqualTo("sAMAccountName"));
            Assert.That(result.ErrorMessage, Is.EqualTo("00002071: samldb: sAMAccountName 'jimprobe1' already in use!"));
        }
    }

    [Test]
    public void ValueAlreadyInUse_NoAttributeNamed_IsStillClassified()
    {
        var result = ConnectedSystemExportResult.ValueAlreadyInUse("One or more of the attribute values are already in use or are reserved.", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse));
            Assert.That(result.RejectedAttributeName, Is.Null);
        }
    }

    [Test]
    public void Failed_GeneralFailure_NamesNoRejectedAttribute()
    {
        Assert.That(ConnectedSystemExportResult.Failed("LDAP error").RejectedAttributeName, Is.Null);
    }

    [Test]
    public void Succeeded_WithAttributesAwaitingInitialPassword_CarriesThemBesideTheExternalIds()
    {
        var result = ConnectedSystemExportResult.Succeeded("6f1b8d6e-1c1a-4b55-9c43-6a1d0f0f2a10", "CN=Joe Bloggs,OU=Users,DC=corp,DC=local", ["userAccountControl"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.ExternalId, Is.EqualTo("6f1b8d6e-1c1a-4b55-9c43-6a1d0f0f2a10"));
            Assert.That(result.SecondaryExternalId, Is.EqualTo("CN=Joe Bloggs,OU=Users,DC=corp,DC=local"));
            Assert.That(result.AttributesAwaitingInitialPassword, Is.EqualTo(new[] { "userAccountControl" }));
        }
    }

    /// <summary>
    /// Only a Connector that wrote an interim value pending an Initial Password says so (#2039); every other result
    /// leaves the list null rather than empty, so "nothing was rewritten" needs no special case.
    /// </summary>
    [Test]
    public void EveryOtherFactory_LeavesAttributesAwaitingInitialPasswordNull()
    {
        var results = new[]
        {
            ConnectedSystemExportResult.Succeeded(),
            ConnectedSystemExportResult.Succeeded("id", "CN=Joe Bloggs,OU=Users,DC=corp,DC=local"),
            ConnectedSystemExportResult.Failed("LDAP error"),
            ConnectedSystemExportResult.Failed("LDAP error", ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount),
            ConnectedSystemExportResult.ValueAlreadyInUse("already in use", "mail")
        };

        Assert.That(results.Select(r => r.AttributesAwaitingInitialPassword), Is.All.Null);
    }
}
