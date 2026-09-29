// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Sync;
using Moq;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// The out-of-synchronisation derived-input mark collector (#1750, plan Phase 4, FR 9): which systems it marks
/// (transitively, excluding none), that it applies marks in one bulk call per flush, and that it is inert without a
/// graph.
/// </summary>
/// <remarks>
/// Configuration: HR (system 10) derives Email from Account Name; Directory (system 30) derives User Principal Name
/// from Email; AD (system 20) flows Region directly and hosts no derived flow.
/// </remarks>
[TestFixture]
public class DerivedInputMarkBatchTests
{
    private const int Hr = 10;
    private const int Ad = 20;
    private const int Directory = 30;

    private DerivedFlowTestModel _model = null!;
    private DerivedFlowGraph _graph = null!;
    private Mock<ISyncRepository> _repository = null!;
    private List<IReadOnlyCollection<DerivedInputChangeMark>> _calls = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new DerivedFlowTestModel();
        var hr = ImportRule(1, "HR Import", Hr);
        Expression(hr, 101, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"");
        var ad = ImportRule(2, "AD Import", Ad);
        Direct(ad, 102, _model.Region, "region");
        var directory = ImportRule(3, "Directory Import", Directory);
        Expression(directory, 103, _model.UserPrincipalName, "mv[\"Email\"]");
        _graph = new DerivedFlowGraph([hr, ad, directory], _model.Types, DerivedFlowGraphScope.EnabledMappingsOnly);

        _calls = [];
        _repository = new Mock<ISyncRepository>(MockBehavior.Strict);
        _repository.Setup(r => r.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(It.IsAny<IReadOnlyCollection<DerivedInputChangeMark>>()))
            .Callback<IReadOnlyCollection<DerivedInputChangeMark>>(marks => _calls.Add(marks.ToList()))
            .ReturnsAsync((IReadOnlyCollection<DerivedInputChangeMark> marks) => marks.Count);
    }

    [Test]
    public async Task Collect_InputReadTransitively_MarksEveryHostingSystemAndNoOtherAsync()
    {
        var batch = new DerivedInputMarkBatch(_graph, "test");
        var mvo = Person();

        batch.Collect(mvo, [Value(_model.AccountName)]);
        await batch.FlushAsync(_repository.Object);

        Assert.That(_calls.Single(), Is.EquivalentTo(new[]
        {
            new DerivedInputChangeMark(mvo.Id, Hr),
            new DerivedInputChangeMark(mvo.Id, Directory)
        }), "Email's host reads Account Name directly, User Principal Name's host through Email; AD hosts nothing");
    }

    [Test]
    public async Task Collect_DerivedAttributeItselfChanged_MarksOnlyItsReadersNotItsHostAsync()
    {
        // A direct edit of Email itself: HR's flow will reassert it by priority on its own next synchronisation, so
        // only Email's readers need the mark.
        var batch = new DerivedInputMarkBatch(_graph, "test");
        var mvo = Person();

        batch.Collect(mvo, [Value(_model.Email)]);
        await batch.FlushAsync(_repository.Object);

        Assert.That(_calls.Single(), Is.EqualTo(new[] { new DerivedInputChangeMark(mvo.Id, Directory) }));
    }

    [Test]
    public async Task Collect_AttributeNoDerivedFlowReads_FlushMakesNoRepositoryCallAsync()
    {
        var batch = new DerivedInputMarkBatch(_graph, "test");

        batch.Collect(Person(), [Value(_model.Region)]);
        var marked = await batch.FlushAsync(_repository.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(marked, Is.Zero);
            Assert.That(_calls, Is.Empty);
            Assert.That(batch.FlushCount, Is.Zero);
        }
    }

    [Test]
    public async Task Collect_EmptyChangeSet_MarksNothingAsync()
    {
        var batch = new DerivedInputMarkBatch(_graph, "test");

        batch.Collect(Person(), []);
        await batch.FlushAsync(_repository.Object);

        Assert.That(_calls, Is.Empty);
    }

    [Test]
    public async Task Collect_NoGraph_IsInertAndNeverCallsTheRepositoryAsync()
    {
        var batch = new DerivedInputMarkBatch(graph: null, "test");

        batch.Collect(Person(), [Value(_model.AccountName)]);
        await batch.FlushAsync(_repository.Object);
        batch.LogSummary();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.PendingCount, Is.Zero);
            Assert.That(_calls, Is.Empty);
        }
    }

    [Test]
    public async Task Collect_ManyObjectsAndRepeatedChanges_OneBulkCallOfDistinctMarksAsync()
    {
        var batch = new DerivedInputMarkBatch(_graph, "test");
        var people = Enumerable.Range(0, 50).Select(_ => Person()).ToList();

        foreach (var mvo in people)
        {
            // Additions and removals of the same attribute, as a recall re-electing a survivor stages them.
            batch.Collect(mvo, [Value(_model.AccountName), Value(_model.AccountName)]);
            batch.Collect(mvo, [Value(_model.Email)]);
        }
        await batch.FlushAsync(_repository.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_calls, Has.Count.EqualTo(1), "one repository call per flush, never one per object");
            Assert.That(_calls.Single(), Has.Count.EqualTo(100), "50 objects, two hosting systems each, deduplicated");
            Assert.That(batch.MarksRequested, Is.EqualTo(100));
            Assert.That(batch.MarksSet, Is.EqualTo(100));
        }
    }

    [Test]
    public async Task FlushAsync_TwoBatches_OneCallEachAndTotalsAccumulateAsync()
    {
        var batch = new DerivedInputMarkBatch(_graph, "test");

        batch.Collect(Person(), [Value(_model.AccountName)]);
        await batch.FlushAsync(_repository.Object);
        batch.Collect(Person(), [Value(_model.Email)]);
        await batch.FlushAsync(_repository.Object);
        await batch.FlushAsync(_repository.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_calls, Has.Count.EqualTo(2), "a flush with nothing pending makes no call");
            Assert.That(batch.FlushCount, Is.EqualTo(2));
            Assert.That(batch.MarksRequested, Is.EqualTo(3));
            Assert.That(batch.PendingCount, Is.Zero);
        }
    }

    [Test]
    public void Collect_MetaverseObjectWithNoId_Throws()
    {
        var batch = new DerivedInputMarkBatch(_graph, "test");
        var mvo = Person();
        mvo.Id = Guid.Empty;

        Assert.That(() => batch.Collect(mvo, [Value(_model.AccountName)]), Throws.ArgumentException);
    }

    [Test]
    public async Task Collect_MetaverseObjectWithNoTypeLoaded_MarksNothingAsync()
    {
        var batch = new DerivedInputMarkBatch(_graph, "test");
        var mvo = Person();
        mvo.Type = null!;

        batch.Collect(mvo, [Value(_model.AccountName)]);
        await batch.FlushAsync(_repository.Object);

        Assert.That(_calls, Is.Empty);
    }

    [Test]
    public async Task Collect_ValueCarryingOnlyItsAttributeNavigation_IsResolvedAsync()
    {
        // A value built this pass may carry no attribute id yet, only the navigation.
        var batch = new DerivedInputMarkBatch(_graph, "test");
        var mvo = Person();

        batch.Collect(mvo, [new MetaverseObjectAttributeValue { Attribute = _model.AccountName, StringValue = "x" }]);
        await batch.FlushAsync(_repository.Object);

        Assert.That(_calls.Single(), Has.Count.EqualTo(2));
    }

    private MetaverseObject Person() => new() { Id = Guid.NewGuid(), Type = _model.Person };

    private static MetaverseObjectAttributeValue Value(MetaverseAttribute attribute) =>
        new() { Attribute = attribute, AttributeId = attribute.Id, StringValue = "value" };
}
