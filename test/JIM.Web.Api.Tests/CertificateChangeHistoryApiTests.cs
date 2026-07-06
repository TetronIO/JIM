// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Trusted Certificate configuration change-history REST endpoints on
/// <see cref="CertificatesController"/>: the paged history list, the single-version detail, and the compare
/// endpoint, including the not-found cases. Trusted Certificates are Guid-keyed, exercising the Guid-keyed read
/// path at the controller layer.
/// </summary>
[TestFixture]
public class CertificateChangeHistoryApiTests
{
    private static readonly Guid CertificateId = Guid.Parse("6f9a1f3e-2b4c-4d5e-8f7a-9b0c1d2e3f4a");

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private JimApplication _application = null!;
    private CertificatesController _controller = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime When = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new CertificatesController(new Mock<ILogger<CertificatesController>>().Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetCertificateChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(2, ActivityTargetOperationType.Update, CertificateSnapJson(isEnabled: false)),
            Data(1, ActivityTargetOperationType.Create, CertificateSnapJson(isEnabled: true))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.TrustedCertificate, CertificateId)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.TrustedCertificate, CertificateId, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetCertificateChangeHistoryAsync(CertificateId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(2));
        Assert.That(payload.Items.Count(), Is.EqualTo(2));
        Assert.That(payload.Items.First().Summary, Is.EqualTo("Enabled"), "v2 changed the enabled flag versus v1");
        Assert.That(payload.Items.Last().Summary, Is.EqualTo("Created"), "v1 is the creation");
    }

    [Test]
    public async Task GetCertificateChangeAsync_ReturnsDetailWithDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.TrustedCertificate, CertificateId, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, CertificateSnapJson(isEnabled: false)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.TrustedCertificate, CertificateId, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, CertificateSnapJson(isEnabled: true)));

        var detail = await OkPayload<ConfigurationChangeDetail>(_controller.GetCertificateChangeAsync(CertificateId, 2));

        Assert.That(detail.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectGuidId, Is.EqualTo(CertificateId));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("Corp Root CA"));
        Assert.That(detail.Diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetCertificateChangeAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.GetCertificateChangeAsync(CertificateId, 99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CompareCertificateChangesAsync_ReturnsDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.TrustedCertificate, CertificateId, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, CertificateSnapJson(isEnabled: true)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.TrustedCertificate, CertificateId, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, CertificateSnapJson(isEnabled: false)));

        var diff = await OkPayload<ConfigurationDiff>(_controller.CompareCertificateChangesAsync(CertificateId, 1, 3));

        Assert.That(diff.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task CompareCertificateChangesAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.CompareCertificateChangesAsync(CertificateId, 1, 3);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static async Task<T> OkPayload<T>(Task<IActionResult> action) where T : class
    {
        var result = await action;
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var payload = ((OkObjectResult)result).Value as T;
        Assert.That(payload, Is.Not.Null);
        return payload!;
    }

    private string CertificateSnapJson(bool isEnabled) =>
        ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(
            new TrustedCertificate
            {
                Id = CertificateId,
                Name = "Corp Root CA",
                Thumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDD",
                Subject = "CN=Corp Root CA",
                Issuer = "CN=Corp Root CA",
                SerialNumber = "01FF",
                ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidTo = new DateTime(2036, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                SourceType = CertificateSourceType.Uploaded,
                IsEnabled = isEnabled
            }, HashKey));

    private static ConfigurationChangeActivityData Data(int version, ActivityTargetOperationType operation, string snapshotJson) => new()
    {
        ActivityId = Guid.NewGuid(),
        Version = version,
        Operation = operation,
        InitiatedByType = ActivityInitiatorType.User,
        InitiatedByName = "Tester",
        When = When,
        SnapshotJson = snapshotJson
    };
}
