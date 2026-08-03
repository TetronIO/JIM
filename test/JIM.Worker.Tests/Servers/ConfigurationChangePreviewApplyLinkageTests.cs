// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The link from a configuration change back to the preview an administrator read before making it (#827/#1114).
///
/// Without it the two Activities are unrelated rows and the audit answers "what changed" but not "what were they
/// told it would do". <see cref="Activity.PreviewActivityId"/> exists for exactly that question, and until this
/// change nothing wrote it.
///
/// Deliberately optional on every apply path: a preview is an affordance, not a precondition, and a save made
/// without one must still succeed and still record its own Activity.
/// </summary>
[TestFixture]
public class ConfigurationChangePreviewApplyLinkageTests
{
    private const int ObjectTypeId = 42;

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private JimApplication _jim = null!;
    private Activity? _createdActivity;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _metaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>())).Returns(Task.CompletedTask);

        // Change capture off: this fixture is about the preview link, and snapshot capture needs a hash key and a
        // protection service it would otherwise have to stand up for no benefit here.
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = "false"
            });

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task UpdateMetaverseObjectTypeAsync_UserInitiatedWithPreview_LinksTheApplyActivityToThePreviewAsync()
    {
        var previewActivityId = Guid.NewGuid();

        await _jim.Metaverse.UpdateMetaverseObjectTypeAsync(BuildObjectType(), NewUser(), changeReason: null,
            previewActivityId: previewActivityId);

        Assert.That(_createdActivity, Is.Not.Null);
        Assert.That(_createdActivity!.PreviewActivityId, Is.EqualTo(previewActivityId),
            "the apply Activity must name the preview the administrator read, or the audit cannot show what they were told");
    }

    [Test]
    public async Task UpdateMetaverseObjectTypeAsync_ApiKeyInitiatedWithPreview_LinksTheApplyActivityToThePreviewAsync()
    {
        var previewActivityId = Guid.NewGuid();

        await _jim.Metaverse.UpdateMetaverseObjectTypeAsync(BuildObjectType(), NewApiKey(), changeReason: null,
            previewActivityId: previewActivityId);

        Assert.That(_createdActivity, Is.Not.Null);
        Assert.That(_createdActivity!.PreviewActivityId, Is.EqualTo(previewActivityId),
            "an API-key caller can preview too, so its apply Activity carries the same link");
    }

    [Test]
    public async Task UpdateMetaverseObjectTypeAsync_NoPreview_LeavesTheLinkUnsetAsync()
    {
        await _jim.Metaverse.UpdateMetaverseObjectTypeAsync(BuildObjectType(), NewUser());

        Assert.That(_createdActivity, Is.Not.Null);
        Assert.That(_createdActivity!.PreviewActivityId, Is.Null,
            "a preview is an affordance, not a precondition; a save without one records no link rather than a fabricated one");
    }

    private static MetaverseObjectType BuildObjectType() => new()
    {
        Id = ObjectTypeId,
        Name = "Robot",
        PluralName = "Robots",
        DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
        DeletionGracePeriod = TimeSpan.FromDays(30)
    };

    private static MetaverseObject NewUser() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin User" };

    private static ApiKey NewApiKey() => new() { Id = Guid.NewGuid(), Name = "Automation" };
}
