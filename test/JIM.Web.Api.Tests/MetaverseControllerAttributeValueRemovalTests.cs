// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface of the manual removal of Accumulate values (#3059, Phase 3): what each outcome answers. The
/// application behaviour behind it is covered by <c>MetaverseServerAccumulatedValueRemovalTests</c>.
/// </summary>
[TestFixture]
public class MetaverseControllerAttributeValueRemovalTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IMetaverseRepository> _mv = null!;
    private Mock<IConnectedSystemRepository> _cs = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;
    private MetaverseObject _person = null!;
    private MetaverseAttribute _auditLog = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _mv = new Mock<IMetaverseRepository>();
        _cs = new Mock<IConnectedSystemRepository>();
        var activities = new Mock<IActivityRepository>();
        var apiKeys = new Mock<IApiKeyRepository>();
        var settings = new Mock<IServiceSettingsRepository>();
        settings.Setup(r => r.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((ServiceSetting?)null);
        _repo.Setup(r => r.Metaverse).Returns(_mv.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_cs.Object);
        _repo.Setup(r => r.Activity).Returns(activities.Object);
        _repo.Setup(r => r.ApiKeys).Returns(apiKeys.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(settings.Object);
        _cs.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRule>());
        // The database gives an Activity its id as it is saved.
        activities.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Callback<Activity>(a => a.Id = Guid.NewGuid()).Returns(Task.CompletedTask);

        _application = new JimApplication(_repo.Object, syncRepository: new JIM.InMemoryData.SyncRepository());
        _controller = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, _application);

        var apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "Clean-up automation", KeyHash = "h", KeyPrefix = "p", IsEnabled = true, Created = DateTime.UtcNow };
        apiKeys.Setup(r => r.GetByIdAsync(apiKey.Id)).ReturnsAsync(apiKey);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKey.Id.ToString()),
            new(ClaimTypes.Name, apiKey.Name)
        }, "ApiKey"));
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };

        var type = new MetaverseObjectType { Id = 1, Name = "Person" };
        _auditLog = new MetaverseAttribute
        {
            Id = 10, Name = "Account Audit Log", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued,
            ContributionMode = AttributeContributionMode.Accumulate
        };
        type.Attributes.Add(_auditLog);
        _person = new MetaverseObject { Id = Guid.NewGuid(), Type = type };
        _person.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = _auditLog, AttributeId = _auditLog.Id, MetaverseObject = _person, StringValue = "J Smith"
        });
        _mv.Setup(r => r.GetMetaverseObjectAsync(_person.Id)).ReturnsAsync(_person);
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    [Test]
    public async Task RemoveObjectAttributeValuesAsync_ValueNoRuleSupplies_ReturnsTheRemovalAsync()
    {
        var result = await _controller.RemoveObjectAttributeValuesAsync(_person.Id, _auditLog.Id,
            new RemoveMetaverseObjectAttributeValuesRequest { Values = ["J Smith"] });

        var body = (RemoveMetaverseObjectAttributeValuesResponse)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.RemovedValues, Is.EqualTo(new[] { "J Smith" }));
            Assert.That(body.ActivityId, Is.Not.EqualTo(Guid.Empty));
        }
        _mv.Verify(r => r.RemoveMetaverseObjectAttributeValuesAsync(_person, It.IsAny<IReadOnlyCollection<Guid>>()), Times.Once);
    }

    [Test]
    public async Task RemoveObjectAttributeValuesAsync_ValueNotHeld_ReturnsBadRequestAsync()
    {
        var result = await _controller.RemoveObjectAttributeValuesAsync(_person.Id, _auditLog.Id,
            new RemoveMetaverseObjectAttributeValuesRequest { Values = ["Never held"] });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task RemoveObjectAttributeValuesAsync_NoSuchObject_ReturnsNotFoundAsync()
    {
        var result = await _controller.RemoveObjectAttributeValuesAsync(Guid.NewGuid(), _auditLog.Id,
            new RemoveMetaverseObjectAttributeValuesRequest { Values = ["J Smith"] });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task RemoveObjectAttributeValuesAsync_ValueARuleSupplies_ReturnsConflictNamingTheRuleAsync()
    {
        var hr = new ConnectedSystem { Id = 1, Name = "HR" };
        var displayName = new JIM.Models.Staging.ConnectedSystemObjectTypeAttribute { Id = 1000, Name = "DisplayName", Type = AttributeDataType.Text };
        var hrUser = new JIM.Models.Staging.ConnectedSystemObjectType { Id = 100, Name = "HrUser", ConnectedSystemId = hr.Id, Attributes = [displayName] };
        var rule = new SyncRule
        {
            Id = 50, Name = "HR Import", Direction = SyncRuleDirection.Import, ConnectedSystemId = hr.Id,
            ConnectedSystemObjectTypeId = hrUser.Id, ConnectedSystemObjectType = hrUser, MetaverseObjectTypeId = 1
        };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRuleId = rule.Id, SyncRule = rule, TargetMetaverseAttribute = _auditLog, TargetMetaverseAttributeId = _auditLog.Id,
            Sources = { new SyncRuleMappingSource { ConnectedSystemAttribute = displayName, ConnectedSystemAttributeId = displayName.Id } }
        });
        var account = new JIM.Models.Staging.ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = hr.Id, TypeId = hrUser.Id, Type = hrUser, MetaverseObject = _person, MetaverseObjectId = _person.Id
        };
        account.AttributeValues.Add(new JIM.Models.Staging.ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = displayName, AttributeId = displayName.Id, StringValue = "J Smith", ConnectedSystemObject = account
        });
        var sync = new JIM.InMemoryData.SyncRepository();
        sync.SeedConnectedSystem(hr);
        sync.SeedObjectType(hrUser);
        sync.SeedConnectedSystemObject(account);
        _application.Dispose();
        _application = new JimApplication(_repo.Object, syncRepository: sync);
        var context = _controller.ControllerContext;
        _controller = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, _application) { ControllerContext = context };
        _cs.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(1)).ReturnsAsync([rule]);
        _cs.Setup(r => r.GetObjectTypesAsync(hr.Id)).ReturnsAsync([hrUser]);
        _cs.Setup(r => r.GetConnectedSystemCoreAsync(hr.Id, It.IsAny<bool>())).ReturnsAsync(hr);

        var result = await _controller.RemoveObjectAttributeValuesAsync(_person.Id, _auditLog.Id,
            new RemoveMetaverseObjectAttributeValuesRequest { Values = ["J Smith"] });

        var conflict = (ConflictObjectResult)result;
        Assert.That(((ApiErrorResponse)conflict.Value!).Message, Does.Contain("HR Import"));
        _mv.Verify(r => r.RemoveMetaverseObjectAttributeValuesAsync(It.IsAny<MetaverseObject>(), It.IsAny<IReadOnlyCollection<Guid>>()), Times.Never);
    }
}
