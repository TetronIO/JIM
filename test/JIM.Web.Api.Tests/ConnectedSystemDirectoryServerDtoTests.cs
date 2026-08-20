// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Covers the response shape a successful directory server discovery (issue #1167) maps to. The controller's 200
/// path itself needs a live Active Directory / Samba AD directory and so is not covered by
/// <see cref="SynchronisationControllerDirectoryServersTests"/>; this proves the mapping that path would use.
/// </summary>
[TestFixture]
public class ConnectedSystemDirectoryServerDtoTests
{
    [Test]
    public void FromModel_MapsHostNameAndSite()
    {
        var model = new ConnectorDirectoryServer { HostName = "dc01.corp.local", Site = "Default-First-Site-Name" };

        var dto = ConnectedSystemDirectoryServerDto.FromModel(model);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.HostName, Is.EqualTo("dc01.corp.local"));
            Assert.That(dto.Site, Is.EqualTo("Default-First-Site-Name"));
        }
    }

    [Test]
    public void FromModel_NullSite_MapsToNull()
    {
        var model = new ConnectorDirectoryServer { HostName = "dc01.corp.local", Site = null };

        var dto = ConnectedSystemDirectoryServerDto.FromModel(model);

        Assert.That(dto.Site, Is.Null);
    }
}
