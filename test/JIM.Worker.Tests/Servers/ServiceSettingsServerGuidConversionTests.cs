// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class ServiceSettingsServerGuidConversionTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task GetSettingValueAsync_GuidValueType_ReturnsParsedGuidAsync()
    {
        var expected = Guid.NewGuid();
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Instance.Id"))
            .ReturnsAsync(new ServiceSetting
            {
                Key = "Instance.Id",
                DisplayName = "Service ID",
                Category = ServiceSettingCategory.Instance,
                ValueType = ServiceSettingValueType.Guid,
                Value = expected.ToString(),
                IsReadOnly = true
            });

        var result = await _application.ServiceSettings.GetSettingValueAsync<Guid>("Instance.Id");

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public async Task GetSettingValueAsync_GuidValueType_MalformedString_ReturnsDefaultAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Instance.Id"))
            .ReturnsAsync(new ServiceSetting
            {
                Key = "Instance.Id",
                DisplayName = "Service ID",
                Category = ServiceSettingCategory.Instance,
                ValueType = ServiceSettingValueType.Guid,
                Value = "not-a-guid",
                IsReadOnly = true
            });

        var result = await _application.ServiceSettings.GetSettingValueAsync<Guid>("Instance.Id");

        Assert.That(result, Is.EqualTo(Guid.Empty));
    }
}
