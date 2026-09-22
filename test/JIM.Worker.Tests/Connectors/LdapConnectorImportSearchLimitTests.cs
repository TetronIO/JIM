// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using NUnit.Framework;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A directory that stops a search at its own limit answers sizeLimitExceeded (or its time and administrative
/// cousins), and OpenLDAP does so across a paged search as a whole for every client but the rootDN. An import
/// that hits it must refuse rather than continue with a truncated container, and its message must tell the
/// administrator what to do: exempt the account JIM connects as, not raise the limit for everyone.
/// </summary>
[TestFixture]
public class LdapConnectorImportSearchLimitTests
{
    [TestCase(ResultCode.SizeLimitExceeded, true)]
    [TestCase(ResultCode.TimeLimitExceeded, true)]
    [TestCase(ResultCode.AdminLimitExceeded, true)]
    [TestCase(ResultCode.NoSuchObject, false)]
    [TestCase(ResultCode.InsufficientAccessRights, false)]
    [TestCase(null, false)]
    public void IsSearchLimitExceeded_RecognisesTheThreeLimitResultsOnly(ResultCode? resultCode, bool expected) =>
        Assert.That(LdapConnectorImport.IsSearchLimitExceeded(resultCode), Is.EqualTo(expected));

    [Test]
    public void DescribeSearchLimitStoppedImport_NamesTheContainerTheTypeTheAccountAndTheExemption()
    {
        var message = LdapConnectorImport.DescribeSearchLimitStoppedImport(
            containerName: "People",
            objectTypeName: "jimPerson",
            bindIdentity: "cn=svc-jim,ou=Services,dc=example,dc=com",
            directoryMessage: "The size limit was exceeded");

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.StartWith("The directory stopped the import of jimPerson objects from People at its search limit"));
            Assert.That(message, Does.Contain("(The size limit was exceeded)"));
            Assert.That(message, Does.Contain("nothing from People was imported"));
            Assert.That(message, Does.Contain("cn=svc-jim,ou=Services,dc=example,dc=com"));
            Assert.That(message, Does.Contain("olcLimits"));
            Assert.That(message, Does.Contain("Service Account Permissions"));
        });
    }

    [Test]
    public void DescribeSearchLimitStoppedImport_WithoutABindIdentity_StillSaysWhichAccountIsMeant()
    {
        var message = LdapConnectorImport.DescribeSearchLimitStoppedImport("Groups", "jimGroup", bindIdentity: null, directoryMessage: "The size limit was exceeded");

        Assert.That(message, Does.Contain("The account JIM connects as is subject to"));
    }
}
