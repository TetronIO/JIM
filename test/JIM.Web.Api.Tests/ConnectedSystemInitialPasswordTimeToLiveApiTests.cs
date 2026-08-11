// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The Connected System's initial-password time to live across the API layer: it is reported so automation can
/// audit it, and accepted so automation can raise it ahead of a planned outage rather than only from the portal.
/// Mirrors the <c>MaxExportParallelism</c> coverage in <see cref="ConnectedSystemParallelExportTests"/>.
/// </summary>
[TestFixture]
public class ConnectedSystemInitialPasswordTimeToLiveApiTests
{
    [Test]
    public void FromEntity_TimeToLiveSet_MapsCorrectly()
    {
        var entity = new ConnectedSystem
        {
            Name = "Corporate AD",
            InitialPasswordTimeToLive = TimeSpan.FromDays(21)
        };

        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        Assert.That(dto.InitialPasswordTimeToLive, Is.EqualTo(TimeSpan.FromDays(21)));
    }

    /// <summary>
    /// Null is reported as null rather than as the seven-day default, so a caller can tell a system that has been
    /// configured to seven days from one that has never been configured at all.
    /// </summary>
    [Test]
    public void FromEntity_TimeToLiveNotSet_MapsAsNull()
    {
        var entity = new ConnectedSystem { Name = "Corporate AD" };

        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        Assert.That(dto.InitialPasswordTimeToLive, Is.Null);
    }

    [Test]
    public void UpdateConnectedSystemRequest_TimeToLive_AcceptsAValue()
    {
        var request = new UpdateConnectedSystemRequest { InitialPasswordTimeToLive = TimeSpan.FromDays(30) };

        Assert.That(request.InitialPasswordTimeToLive, Is.EqualTo(TimeSpan.FromDays(30)));
    }

    [Test]
    public void UpdateConnectedSystemRequest_TimeToLiveOmitted_IsNull()
    {
        var request = new UpdateConnectedSystemRequest();

        Assert.That(request.InitialPasswordTimeToLive, Is.Null);
    }
}
