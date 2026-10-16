// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for a Connected System Object's unresolved references (#959): the values with the likely cause of
/// each, a page at a time, and the summary of causes and next steps the portal shows above them.
/// </summary>
[TestFixture]
public class UnresolvedReferenceEndpointTests
{
    private const int ConnectedSystemId = 3;
    private const int ManagerAttributeId = 12;
    private static readonly Guid ObjectId = Guid.Parse("6b1d55c3-0f0b-4f3e-9d1b-2b2b6f7e0959");

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        var repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);

        var application = new JimApplication(repository.Object);
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

        // An HR database: no Containers, so every value that is not ambiguous is simply not found.
        var person = new ConnectedSystemObjectType { Id = 1, Name = "person", Selected = true };
        person.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "employeeId", Type = AttributeDataType.Text, IsExternalId = true, ConnectedSystemObjectType = person });
        person.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = ManagerAttributeId, Name = "manager", Type = AttributeDataType.Reference, ReferencedObjectTypeId = 1, ConnectedSystemObjectType = person });
        _connectedSystems.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "HR Database",
            Partitions = [],
            ObjectTypes = [person]
        });
    }

    [Test]
    public async Task GetConnectedSystemObjectUnresolvedReferencesAsync_ObjectNotInTheConnectedSystem_ReturnsNotFoundAsync()
    {
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectTypeIdAsync(ConnectedSystemId, ObjectId)).ReturnsAsync((int?)null);

        var result = await _controller.GetConnectedSystemObjectUnresolvedReferencesAsync(ConnectedSystemId, ObjectId, new PaginationRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetConnectedSystemObjectUnresolvedReferencesAsync_Object_ReturnsThePageWithTheLikelyCauseOfEachAsync()
    {
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectTypeIdAsync(ConnectedSystemId, ObjectId)).ReturnsAsync(1);
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValuesRangeAsync(ObjectId, 0, 25, null, true))
            .ReturnsAsync(new RangeResultSet<ConnectedSystemObjectUnresolvedReferenceValue>
            {
                Results = [new ConnectedSystemObjectUnresolvedReferenceValue { AttributeId = ManagerAttributeId, AttributeName = "manager", Value = "EMP-48213" }],
                TotalResults = 1
            });

        var result = await _controller.GetConnectedSystemObjectUnresolvedReferencesAsync(ConnectedSystemId, ObjectId, new PaginationRequest());

        var page = (PaginatedResponse<ConnectedSystemObjectUnresolvedReference>)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.TotalCount, Is.EqualTo(1));
            Assert.That(page.Items.Single().AttributeName, Is.EqualTo("manager"));
            Assert.That(page.Items.Single().Value, Is.EqualTo("EMP-48213"));
            Assert.That(page.Items.Single().Cause, Is.EqualTo(UnresolvedReferenceCause.NotFound));
            Assert.That(page.Items.Single().FixLocation, Is.EqualTo(UnresolvedReferenceFixLocation.SourceSystem));
            Assert.That(page.Items.Single().Finding, Is.Not.Empty);
            Assert.That(page.Items.Single().Remedy, Is.Not.Empty);
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectUnresolvedReferencesAsync_LaterPageWithSearch_ReadsThatWindowAsync()
    {
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectTypeIdAsync(ConnectedSystemId, ObjectId)).ReturnsAsync(1);
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValuesRangeAsync(ObjectId, 20, 10, "EMP-4", true))
            .ReturnsAsync(new RangeResultSet<ConnectedSystemObjectUnresolvedReferenceValue> { Results = [], TotalResults = 23 });

        var result = await _controller.GetConnectedSystemObjectUnresolvedReferencesAsync(
            ConnectedSystemId, ObjectId, new PaginationRequest { Page = 3, PageSize = 10 }, "EMP-4");

        var page = (PaginatedResponse<ConnectedSystemObjectUnresolvedReference>)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Page, Is.EqualTo(3));
            Assert.That(page.PageSize, Is.EqualTo(10));
            Assert.That(page.TotalCount, Is.EqualTo(23));
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectUnresolvedReferenceSummaryAsync_ObjectNotInTheConnectedSystem_ReturnsNotFoundAsync()
    {
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectTypeIdAsync(ConnectedSystemId, ObjectId)).ReturnsAsync((int?)null);

        var result = await _controller.GetConnectedSystemObjectUnresolvedReferenceSummaryAsync(ConnectedSystemId, ObjectId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetConnectedSystemObjectUnresolvedReferenceSummaryAsync_Object_ReturnsTheCausesAndNextStepsAsync()
    {
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectTypeIdAsync(ConnectedSystemId, ObjectId)).ReturnsAsync(1);
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValueCountAsync(ObjectId)).ReturnsAsync(4);

        var result = await _controller.GetConnectedSystemObjectUnresolvedReferenceSummaryAsync(ConnectedSystemId, ObjectId);

        var summary = (ConnectedSystemObjectUnresolvedReferenceSummary)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.TotalCount, Is.EqualTo(4));
            Assert.That(summary.Causes.Select(c => (c.Cause, c.Count)), Is.EqualTo(new[] { (UnresolvedReferenceCause.NotFound, 4) }));
            Assert.That(summary.Fixes.Select(f => (f.Cause, f.Count)), Is.EqualTo(new[] { (UnresolvedReferenceCause.NotFound, 4) }));
            Assert.That(summary.LargestFix, Is.Null, "Nothing in JIM's configuration would address a value that is simply not found.");
        }
    }

    [Test]
    public async Task GetConnectedSystemUnresolvedReferenceSummaryAsync_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        var result = await _controller.GetConnectedSystemUnresolvedReferenceSummaryAsync(ConnectedSystemId + 1);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetConnectedSystemUnresolvedReferenceSummaryAsync_ConnectedSystem_ReturnsTheStepsWithTheObjectsBehindEachAsync()
    {
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceTotalsAsync(ConnectedSystemId))
            .ReturnsAsync(new ConnectedSystemUnresolvedReferenceTotals { ReferenceCount = 4, ObjectCount = 3 });

        var result = await _controller.GetConnectedSystemUnresolvedReferenceSummaryAsync(ConnectedSystemId);

        var summary = (ConnectedSystemUnresolvedReferenceSummary)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.ConnectedSystemId, Is.EqualTo(ConnectedSystemId));
            Assert.That(summary.TotalCount, Is.EqualTo(4));
            Assert.That(summary.ObjectCount, Is.EqualTo(3));
            Assert.That(summary.Fixes.Select(f => (f.Cause, f.Count, f.ObjectCount)), Is.EqualTo(new[] { (UnresolvedReferenceCause.NotFound, 4, (int?)3) }));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task GetConnectedSystemObjectsAsync_HasUnresolvedReferences_FiltersTheConnectorSpaceAsync(bool hasUnresolvedReferences)
    {
        _connectedSystems.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(new ConnectedSystem { Id = ConnectedSystemId, Name = "HR Database" });
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectHeadersAsync(ConnectedSystemId, 1, 50, null, null, true, null, null, null, hasUnresolvedReferences))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObjectHeader>
            {
                Results = [new ConnectedSystemObjectHeader { Id = ObjectId, UnresolvedReferenceCount = hasUnresolvedReferences ? 2 : 0 }],
                TotalResults = 1,
                CurrentPage = 1,
                PageSize = 50
            });

        var result = await _controller.GetConnectedSystemObjectsAsync(ConnectedSystemId, hasUnresolvedReferences: hasUnresolvedReferences);

        var page = (PaginatedResponse<ConnectedSystemObjectHeader>)((OkObjectResult)result).Value!;
        Assert.That(page.Items.Single().UnresolvedReferenceCount, Is.EqualTo(hasUnresolvedReferences ? 2 : 0),
            "The filter reaches the repository, and each header carries its object's count.");
    }
}
