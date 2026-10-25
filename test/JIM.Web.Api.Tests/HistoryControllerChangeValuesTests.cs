// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
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
/// The endpoints that page through one attribute's value changes on a change record (#2498): change history and an
/// Activity item's changes return each attribute's first values and its total, and these return the rest.
/// </summary>
[TestFixture]
public class HistoryControllerChangeValuesTests
{
    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private Mock<IMetaverseRepository> _metaverse = null!;
    private HistoryController _controller = null!;
    private ChangeValueRowQuery? _queried;

    [SetUp]
    public void SetUp()
    {
        _queried = null;
        var repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);
        _controller = new HistoryController(new Mock<ILogger<HistoryController>>().Object, new JimApplication(repository.Object));
    }

    [Test]
    public async Task GetConnectedSystemObjectChangeValuesAsync_PagesTheAttributesValueChangesOneByOneAsync()
    {
        var changeId = Guid.NewGuid();
        _connectedSystems
            .Setup(r => r.GetCsoChangeValueRowsAsync(It.IsAny<ChangeValueRowQuery>()))
            .Callback<ChangeValueRowQuery>(q => _queried = q)
            .ReturnsAsync(new RangeResultSet<CsoChangeValueRow>
            {
                Results =
                [
                    new CsoChangeValueRow { Value = new CsoValueChangeDto { ValueChangeType = ValueChangeType.Add, StringValue = "CN=Bob Jones" } }
                ],
                TotalResults = 41
            });

        var result = await _controller.GetConnectedSystemObjectChangeValuesAsync(
            changeId, "member", new PaginationRequest { Page = 3, PageSize = 20 }, search: "bob") as OkObjectResult;
        var response = result!.Value as PaginatedResponse<CsoValueChangeDto>;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_queried!.ChangeIds, Is.EqualTo(new[] { changeId }));
            Assert.That(_queried!.AttributeName, Is.EqualTo("member"));
            Assert.That(_queried!.SearchText, Is.EqualTo("bob"));
            Assert.That(_queried!.Offset, Is.EqualTo(40));
            Assert.That(_queried!.Count, Is.EqualTo(20));
            Assert.That(_queried!.IncludeTotalCount, Is.True);
            Assert.That(_queried!.Normalise, Is.False, "the API pages value changes as recorded, one per row");
            Assert.That(response!.Items.Single().StringValue, Is.EqualTo("CN=Bob Jones"));
            Assert.That(response!.TotalCount, Is.EqualTo(41));
            Assert.That(response!.Page, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task GetMetaverseObjectChangeValuesAsync_PagesTheAttributesValueChangesOneByOneAsync()
    {
        var changeId = Guid.NewGuid();
        _metaverse
            .Setup(r => r.GetMvoChangeValueRowsAsync(It.IsAny<ChangeValueRowQuery>()))
            .Callback<ChangeValueRowQuery>(q => _queried = q)
            .ReturnsAsync(new RangeResultSet<MvoChangeValueRow>
            {
                Results =
                [
                    new MvoChangeValueRow { Value = new MvoValueChangeDto { ValueChangeType = ValueChangeType.Remove, StringValue = "smtp:dan@example.com" } }
                ],
                TotalResults = 7
            });

        var result = await _controller.GetMetaverseObjectChangeValuesAsync(
            changeId, "proxyAddresses", new PaginationRequest { Page = 1, PageSize = 50 }) as OkObjectResult;
        var response = result!.Value as PaginatedResponse<MvoValueChangeDto>;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_queried!.ChangeIds, Is.EqualTo(new[] { changeId }));
            Assert.That(_queried!.AttributeName, Is.EqualTo("proxyAddresses"));
            Assert.That(_queried!.SearchText, Is.Null);
            Assert.That(_queried!.Offset, Is.Zero);
            Assert.That(_queried!.Normalise, Is.False);
            Assert.That(response!.Items.Single().ValueChangeType, Is.EqualTo(ValueChangeType.Remove));
            Assert.That(response!.TotalCount, Is.EqualTo(7));
        }
    }
}
