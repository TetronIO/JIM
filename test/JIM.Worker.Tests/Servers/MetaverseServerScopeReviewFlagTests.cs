// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Enums;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Export scope is re-evaluated only for objects a synchronisation visits, or that carry the
/// <see cref="MetaverseObject.ScopeReviewPending"/> flag. A Metaverse Object created or changed through
/// <see cref="JIM.Application.Servers.MetaverseServer"/> is written outside any synchronisation, so the server must
/// flag it, in the same save, or the object is never provisioned or deprovisioned until something unrelated touches
/// it (#2615).
/// </summary>
[TestFixture]
public class MetaverseServerScopeReviewFlagTests
{
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private JimApplication _jim = null!;
    private MetaverseObjectType _personType = null!;
    private MetaverseAttribute _displayNameAttr = null!;
    private MetaverseAttribute _emailAttr = null!;

    /// <summary>Whether the object was flagged at the moment the repository was asked to save it.</summary>
    private bool? _flaggedAtSave;

    /// <summary>The record of unsynchronised changes the object carried at the moment the repository was asked to save it.</summary>
    private string? _changesAtSave;

    [SetUp]
    public void SetUp()
    {
        _flaggedAtSave = null;
        _changesAtSave = null;
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockMetaverseRepo.Setup(r => r.CreateMetaverseObjectAsync(It.IsAny<MetaverseObject>()))
            .Callback<MetaverseObject>(mvo => { _flaggedAtSave = mvo.ScopeReviewPending; _changesAtSave = mvo.UnsynchronisedChangesJson; })
            .Returns(Task.CompletedTask);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectAsync(It.IsAny<MetaverseObject>()))
            .Callback<MetaverseObject>(mvo => { _flaggedAtSave = mvo.ScopeReviewPending; _changesAtSave = mvo.UnsynchronisedChangesJson; })
            .Returns(Task.CompletedTask);

        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((ServiceSetting?)null);

        var mockRepository = new Mock<IRepository>();
        mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);
        // Every Metaverse Object edit reads its type's import Synchronisation Rules, to mark the Connected Systems whose
        // Metaverse-Derived Attribute Flows read a changed attribute (#1750): none here.
        var connectedSystemsRepo = new Mock<IConnectedSystemRepository>();
        connectedSystemsRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(new List<JIM.Models.Logic.SyncRule>());
        mockRepository.Setup(r => r.ConnectedSystems).Returns(connectedSystemsRepo.Object);

        _jim = new JimApplication(mockRepository.Object, syncRepository: new JIM.InMemoryData.SyncRepository());

        _personType = new MetaverseObjectType { Id = 1, Name = Constants.BuiltInObjectTypes.User };
        _displayNameAttr = new MetaverseAttribute
        {
            Id = 1,
            Name = Constants.BuiltInAttributes.DisplayName,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _emailAttr = new MetaverseAttribute
        {
            Id = 2,
            Name = Constants.BuiltInAttributes.Email,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    private MetaverseObjectAttributeValue DisplayName(string value) =>
        new() { Id = Guid.NewGuid(), Attribute = _displayNameAttr, AttributeId = _displayNameAttr.Id, StringValue = value };

    private MetaverseObjectAttributeValue Email(string value) =>
        new() { Id = Guid.NewGuid(), Attribute = _emailAttr, AttributeId = _emailAttr.Id, StringValue = value };

    [Test]
    public async Task CreateMetaverseObjectAsync_NewObject_IsFlaggedForScopeReviewBeforeItIsSavedAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType };
        mvo.AttributeValues.Add(DisplayName("Jay Admin"));

        await _jim.Metaverse.CreateMetaverseObjectAsync(mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.ScopeReviewPending, Is.True);
            Assert.That(_flaggedAtSave, Is.True, "flagged before the save, so the flag is written in the same statement as the object");
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_WithAdditions_IsFlaggedForScopeReviewBeforeItIsSavedAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType };
        var addition = DisplayName("Jay Admin");
        mvo.AttributeValues.Add(addition);

        await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [addition], removals: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.ScopeReviewPending, Is.True);
            Assert.That(_flaggedAtSave, Is.True, "flagged before the save, so the flag is written in the same statement as the edit");
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_WithRemovals_IsFlaggedForScopeReviewAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType };

        await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: null, removals: [DisplayName("Jay Admin")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.ScopeReviewPending, Is.True);
            Assert.That(_flaggedAtSave, Is.True);
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_OperationalMetadataOnly_IsNotFlaggedAsync()
    {
        // A caller updating only operational metadata (a deletion date, say) passes no additions or removals: no
        // attribute value changed, so nothing about the object's export scope can have moved.
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType };

        await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.ScopeReviewPending, Is.False);
            Assert.That(_flaggedAtSave, Is.False, "the update was still saved");
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_EmptyAdditionsAndRemovals_IsNotFlaggedAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType };

        await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [], removals: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.ScopeReviewPending, Is.False);
            Assert.That(_flaggedAtSave, Is.False, "the update was still saved");
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_ObjectAlreadyFlagged_StaysFlaggedAsync()
    {
        // The flag is never cleared by a write outside synchronisation; only the synchronisation that reviews the
        // object clears it.
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType, ScopeReviewPending = true };

        await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo);

        Assert.That(mvo.ScopeReviewPending, Is.True);
    }

    #region Recording what changed (#2615)

    [Test]
    public async Task UpdateMetaverseObjectAsync_AdditionsAndRemovals_RecordsWhatChangedBeforeItIsSavedAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType };
        var added = Email("new@panoply.local");
        mvo.AttributeValues.Add(added);

        await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [added], removals: [Email("old@panoply.local")]);

        var recorded = MetaverseObjectUnsynchronisedChanges.FromJson(_changesAtSave);
        Assert.That(recorded, Is.Not.Null, "recorded before the save, so it is written in the same statement as the edit");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded!.AttributeIds, Is.EqualTo(new[] { _emailAttr.Id }));
            Assert.That(recorded.Removals.Select(r => (r.AttributeId, r.StringValue)), Is.EqualTo(new[] { (_emailAttr.Id, (string?)"old@panoply.local") }),
                "the removed value is stored; the added one is read from the object when the change is delivered");
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_SecondEditBeforeTheNextSynchronisation_MergesIntoWhatIsRecordedAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType };
        await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [Email("a@panoply.local")], removals: [Email("old@panoply.local")]);

        await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [DisplayName("Jay Admin")], removals: [DisplayName("J Admin")]);

        var recorded = MetaverseObjectUnsynchronisedChanges.FromJson(_changesAtSave)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded.AttributeIds, Is.EquivalentTo(new[] { _emailAttr.Id, _displayNameAttr.Id }));
            Assert.That(recorded.Removals.Select(r => r.StringValue), Is.EquivalentTo(new[] { "old@panoply.local", "J Admin" }),
                "the second edit adds to the first's record, it does not replace it");
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_OperationalMetadataOnly_RecordsNothingAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType };

        await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo);

        Assert.That(_changesAtSave, Is.Null);
    }

    [Test]
    public async Task CreateMetaverseObjectAsync_NewObject_RecordsNothingAsync()
    {
        // A provisioning Create exports every current value, so there is nothing a record could add.
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType };
        mvo.AttributeValues.Add(DisplayName("Jay Admin"));

        await _jim.Metaverse.CreateMetaverseObjectAsync(mvo);

        Assert.That(_changesAtSave, Is.Null);
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_RecordedChangesCannotBeRead_ReplacesThemAndStillSavesAsync()
    {
        // Failing the write over a record of an earlier one would fail a sign-in; an unreadable record could not have
        // been delivered anyway.
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType, UnsynchronisedChangesJson = "{ not json" };

        await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [Email("new@panoply.local")], removals: []);

        var recorded = MetaverseObjectUnsynchronisedChanges.FromJson(_changesAtSave);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded?.AttributeIds, Is.EqualTo(new[] { _emailAttr.Id }));
            Assert.That(_flaggedAtSave, Is.True, "the write was saved, and flagged");
        }
    }

    #endregion
}
