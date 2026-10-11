// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Web.Authentication;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="FailedAuthenticationRecorder"/>, the one place a failed authentication is written to the
/// security audit log from the request path.
/// </summary>
[TestFixture]
public class FailedAuthenticationRecorderTests
{
    [Test]
    public async Task RecordAsync_WritesTheAttemptThroughTheSecurityAuditAsync()
    {
        var activities = new Mock<IActivityRepository>();
        activities.Setup(a => a.IncrementAggregatedFailedAuthenticationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(true);
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Activity).Returns(activities.Object);
        var factory = new Mock<IJimApplicationFactory>();
        factory.Setup(f => f.Create()).Returns(() => new JimApplication(repository.Object));

        await new FailedAuthenticationRecorder(factory.Object)
            .RecordAsync(FailedAuthenticationEventNames.AccessToken, "Access token expired", null, "203.0.113.7");

        activities.Verify(a => a.IncrementAggregatedFailedAuthenticationAsync(
            string.Empty, "203.0.113.7", "Access token expired", It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Once);
    }

    [Test]
    public void RecordAsync_ApplicationCannotBeCreated_CompletesWithoutFaulting()
    {
        var factory = new Mock<IJimApplicationFactory>();
        factory.Setup(f => f.Create()).Throws(new InvalidOperationException("database unavailable"));

        Assert.That(async () => await new FailedAuthenticationRecorder(factory.Object)
            .RecordAsync(FailedAuthenticationEventNames.ApiKey, "API key not found", "jim_ak_0000", null), Throws.Nothing);
    }
}
