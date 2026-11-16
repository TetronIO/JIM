// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The import-incomplete marker across the API layer (#2521): read on the Connected System, so a script can find a
/// Connected System whose synchronisation is refused because an import stopped part-way, and never written through it.
/// Only a completed Full Import, or clearing the Connector Space, takes the marker down.
/// </summary>
[TestFixture]
public class ConnectedSystemImportIncompleteApiTests
{
    [Test]
    public void FromEntity_Marked_ReportsWhenTheStoppedImportBeganWriting()
    {
        var since = new DateTime(2026, 11, 16, 10, 3, 12, DateTimeKind.Utc);
        var entity = new ConnectedSystem { Name = "HR System", ImportIncompleteSince = since };

        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        Assert.That(dto.ImportIncompleteSince, Is.EqualTo(since));
    }

    [Test]
    public void FromEntity_NotMarked_ReportsNull()
    {
        var dto = ConnectedSystemDetailDto.FromEntity(new ConnectedSystem { Name = "HR System" });

        Assert.That(dto.ImportIncompleteSince, Is.Null);
    }

    [TestCase(typeof(UpdateConnectedSystemRequest))]
    [TestCase(typeof(CreateConnectedSystemRequest))]
    public void WriteRequests_CarryNoImportIncompleteMarker(Type requestType)
    {
        Assert.That(requestType.GetProperties().Select(p => p.Name), Has.No.Member(nameof(ConnectedSystem.ImportIncompleteSince)),
            "nothing may clear the marker by hand: a synchronisation would then read the half-saved import");
    }
}
