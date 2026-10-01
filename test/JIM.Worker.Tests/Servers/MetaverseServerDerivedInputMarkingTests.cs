// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.TestSupport;
using JIM.Worker.Tests.Services;
using Moq;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// <c>MetaverseServer.UpdateMetaverseObjectAsync</c> as a writer outside synchronisation (#1750, plan Phase 4, FR 9):
/// a direct edit marks the hosting systems of derived flows reading the changed attributes, reads nothing extra with
/// the feature off, and never fails the (already saved) edit over a dependency cycle. The end-to-end behaviour, delta
/// re-derivation included, is covered by <c>DerivedInputOutOfSyncMarkingWorkflowTests</c>.
/// </summary>
[TestFixture]
public class MetaverseServerDerivedInputMarkingTests
{
    private const int Hr = 10;
    private const int Directory = 30;

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private Mock<IMetaverseRepository> _mvRepo = null!;
    private JIM.InMemoryData.SyncRepository _syncRepo = null!;
    private DerivedFlowTestModel _model = null!;
    private List<SyncRule> _importRules = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _model = new DerivedFlowTestModel();

        _repo = new Mock<IRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _mvRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_mvRepo.Object);
        _mvRepo.Setup(r => r.UpdateMetaverseObjectAsync(It.IsAny<MetaverseObject>())).Returns(Task.CompletedTask);
        _mvRepo.Setup(r => r.GetMetaverseObjectTypeAsync(PersonTypeId, true)).ReturnsAsync(() => _model.Person);

        var hr = ImportRule(1, "HR Import", Hr);
        Expression(hr, 101, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"");
        var directory = ImportRule(3, "Directory Import", Directory);
        Expression(directory, 103, _model.UserPrincipalName, "mv[\"Email\"]");
        _importRules = [hr, directory];
        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(PersonTypeId)).ReturnsAsync(() => _importRules);

        _syncRepo = new JIM.InMemoryData.SyncRepository();
    }

    private JimApplication BuildApplication(bool flagEnabled = true)
    {
        _repo.Setup(r => r.ServiceSettings).Returns(flagEnabled
            ? InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled()
            : new InMemoryServiceSettingsRepository());
        return new JimApplication(_repo.Object, syncRepository: _syncRepo);
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_InputEdited_MarksHostingSystemsTransitivelyInOneCallAsync()
    {
        var jim = BuildApplication();
        var (mvo, addition) = PersonWithAccountName();

        await jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [addition], removals: [], initiatedByType: ActivityInitiatorType.User);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_syncRepo.DerivedInputMarkCalls, Has.Count.EqualTo(1));
            Assert.That(_syncRepo.DerivedInputMarkCalls.Single().Select(m => m.ConnectedSystemId), Is.EquivalentTo(new[] { Hr, Directory }));
            Assert.That(_syncRepo.DerivedInputMarkCalls.Single().Select(m => m.MetaverseObjectId).Distinct(), Is.EqualTo(new[] { mvo.Id }));
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_ValueRemoved_MarksAsAnAdditionDoesAsync()
    {
        var jim = BuildApplication();
        var (mvo, value) = PersonWithAccountName();

        await jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: null, removals: [value], initiatedByType: ActivityInitiatorType.User);

        Assert.That(_syncRepo.DerivedInputMarkCalls.Single().Select(m => m.ConnectedSystemId), Is.EquivalentTo(new[] { Hr, Directory }));
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_FlagOff_ReadsNoConfigurationAndMarksNothingAsync()
    {
        var jim = BuildApplication(flagEnabled: false);
        var (mvo, addition) = PersonWithAccountName();

        await jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [addition], removals: [], initiatedByType: ActivityInitiatorType.User);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_syncRepo.DerivedInputMarkCalls, Is.Empty);
            _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never,
                "with the feature off no graph is built, so no rule is read");
            _mvRepo.Verify(r => r.UpdateMetaverseObjectAsync(mvo), Times.Once, "the edit itself is saved as before");
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_NoChangeSet_ReadsNoConfigurationAsync()
    {
        var jim = BuildApplication();
        var (mvo, _) = PersonWithAccountName();

        await jim.Metaverse.UpdateMetaverseObjectAsync(mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_syncRepo.DerivedInputMarkCalls, Is.Empty);
            _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_EnabledDerivedFlowsFormACycle_SavesTheEditAndMarksNothingAsync()
    {
        // The edit is already saved and this path serves sign-in; a cycle (which fails every hosting synchronisation
        // hard) must not lock an administrator out of fixing it.
        var hr = _importRules[0];
        Expression(hr, 104, _model.AccountName, "mv[\"Email\"]");
        var jim = BuildApplication();
        var (mvo, addition) = PersonWithAccountName();

        await jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [addition], removals: [], initiatedByType: ActivityInitiatorType.User);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_syncRepo.DerivedInputMarkCalls, Is.Empty);
            _mvRepo.Verify(r => r.UpdateMetaverseObjectAsync(mvo), Times.Once);
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_AttributeNoDerivedFlowReads_MakesNoMarkCallAsync()
    {
        var jim = BuildApplication();
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _model.Person };
        var region = new MetaverseObjectAttributeValue { MetaverseObject = mvo, Attribute = _model.Region, AttributeId = _model.Region.Id, StringValue = "EMEA" };
        mvo.AttributeValues.Add(region);

        await jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [region], removals: [], initiatedByType: ActivityInitiatorType.User);

        Assert.That(_syncRepo.DerivedInputMarkCalls, Is.Empty);
    }

    private (MetaverseObject Mvo, MetaverseObjectAttributeValue Value) PersonWithAccountName()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _model.Person };
        var value = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = _model.AccountName,
            AttributeId = _model.AccountName.Id,
            StringValue = "jbloggs"
        };
        mvo.AttributeValues.Add(value);
        return (mvo, value);
    }
}
