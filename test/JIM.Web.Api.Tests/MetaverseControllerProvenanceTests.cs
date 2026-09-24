// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core.DTOs;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the value provenance REST endpoints on <see cref="MetaverseController"/> (#399): the object summary
/// and the attribute detail, including their not-found cases and the DTO mapping.
/// </summary>
[TestFixture]
public class MetaverseControllerProvenanceTests
{
    private readonly Guid _mvoId = Guid.NewGuid();
    private const int AttributeId = 42;

    private Mock<IRepository> _repo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetObjectProvenanceAsync_MetaverseObjectExists_ReturnsMappedDtoAsync()
    {
        var model = new MetaverseObjectProvenance
        {
            MetaverseObjectId = _mvoId,
            Attributes = new List<MetaverseAttributeOriginSummary>
            {
                new()
                {
                    AttributeId = AttributeId,
                    AttributeName = "Department",
                    Origins = new List<ValueOrigin>
                    {
                        new() { Kind = ValueOriginKind.SynchronisationRule, ConnectedSystemId = 1, ConnectedSystemName = "HR", SyncRuleId = 2, SyncRuleName = "HR Import" }
                    }
                }
            }
        };
        _metaverseRepo.Setup(r => r.GetMetaverseObjectProvenanceAsync(_mvoId)).ReturnsAsync(model);

        var payload = await OkPayload<MetaverseObjectProvenanceDto>(_controller.GetObjectProvenanceAsync(_mvoId));

        var attribute = payload.Attributes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.MetaverseObjectId, Is.EqualTo(_mvoId));
            Assert.That(attribute.AttributeName, Is.EqualTo("Department"));
            Assert.That(attribute.Origins.Single().Kind, Is.EqualTo(ValueOriginKind.SynchronisationRule));
            Assert.That(attribute.Origins.Single().SyncRuleName, Is.EqualTo("HR Import"));
        }
    }

    [Test]
    public async Task GetObjectProvenanceAsync_MetaverseObjectDoesNotExist_ReturnsNotFoundAsync()
    {
        _metaverseRepo.Setup(r => r.GetMetaverseObjectProvenanceAsync(_mvoId)).ReturnsAsync((MetaverseObjectProvenance?)null);

        var result = await _controller.GetObjectProvenanceAsync(_mvoId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetObjectAttributeProvenanceAsync_AttributeDoesNotExist_ReturnsNotFoundAsync()
    {
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(AttributeId, false)).ReturnsAsync((JIM.Models.Core.MetaverseAttribute?)null);

        var result = await _controller.GetObjectAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetObjectAttributeProvenanceAsync_MetaverseObjectDoesNotExist_ReturnsNotFoundAsync()
    {
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(AttributeId, false)).ReturnsAsync(
            new JIM.Models.Core.MetaverseAttribute { Id = AttributeId, Name = "Department" });
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeIdAsync(_mvoId)).ReturnsAsync((int?)null);

        var result = await _controller.GetObjectAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetObjectAttributeProvenanceAsync_ExistsAsync()
    {
        const int metaverseObjectTypeId = 3;
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(AttributeId, false)).ReturnsAsync(
            new JIM.Models.Core.MetaverseAttribute { Id = AttributeId, Name = "Department", Type = JIM.Models.Core.AttributeDataType.Text });
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeIdAsync(_mvoId)).ReturnsAsync(metaverseObjectTypeId);
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeCurrentValuesAsync(_mvoId, AttributeId, It.IsAny<int>()))
            .ReturnsAsync((new List<ProvenanceValue>(), 0));
        _metaverseRepo.Setup(r => r.GetLastAttributeSetChangeAsync(_mvoId, AttributeId)).ReturnsAsync((ProvenanceChange?)null);
        _metaverseRepo.Setup(r => r.GetAttributeHistoryRawEntriesAsync(_mvoId, AttributeId, It.IsAny<int>()))
            .ReturnsAsync(new List<MetaverseAttributeHistoryRawEntry>());
        _connectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(metaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<JIM.Models.Logic.SyncRuleMapping>());

        var payload = await OkPayload<MetaverseAttributeProvenanceDto>(_controller.GetObjectAttributeProvenanceAsync(_mvoId, AttributeId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.MetaverseObjectId, Is.EqualTo(_mvoId));
            Assert.That(payload.MetaverseObjectTypeId, Is.EqualTo(metaverseObjectTypeId));
            Assert.That(payload.AttributeId, Is.EqualTo(AttributeId));
            Assert.That(payload.AttributeName, Is.EqualTo("Department"));
            Assert.That(payload.Sources, Is.Empty);
            Assert.That(payload.History, Is.Empty);
        }
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
}
