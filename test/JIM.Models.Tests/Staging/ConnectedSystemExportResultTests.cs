// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
}
