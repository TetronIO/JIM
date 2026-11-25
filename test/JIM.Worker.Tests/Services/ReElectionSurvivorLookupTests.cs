// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Tests for <see cref="ReElectionSurvivorLookup"/> (#3059): a page's re-elections find the joined Connected System
/// Objects of the objects predicted to need one with a single bulk read, and of any other object singly, as before.
/// </summary>
[TestFixture]
public class ReElectionSurvivorLookupTests
{
    private readonly List<Guid> _singleCalls = [];
    private readonly List<IReadOnlyCollection<Guid>> _batchCalls = [];

    private ReElectionSurvivorLookup NewLookup() => new(
        mvoId =>
        {
            _singleCalls.Add(mvoId);
            return Task.FromResult(new List<ConnectedSystemObject> { new() { Id = Guid.NewGuid(), MetaverseObjectId = mvoId } });
        },
        mvoIds =>
        {
            _batchCalls.Add(mvoIds);
            return Task.FromResult(mvoIds.ToDictionary(id => id, id => new List<ConnectedSystemObject> { new() { Id = Guid.NewGuid(), MetaverseObjectId = id } }));
        });

    [SetUp]
    public void SetUp()
    {
        _singleCalls.Clear();
        _batchCalls.Clear();
    }

    [Test]
    public async Task GetAsync_PredictedObject_ReadsEveryPredictedObjectOnceInBulkAndServesThemFromItAsync()
    {
        var predicted = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var lookup = NewLookup();
        lookup.BeginPage(predicted);

        var results = new List<List<ConnectedSystemObject>>();
        foreach (var mvoId in predicted)
            results.Add(await lookup.GetAsync(mvoId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_batchCalls, Has.Count.EqualTo(1), "one bulk read for the page's predicted objects");
            Assert.That(_batchCalls[0], Is.EquivalentTo(predicted));
            Assert.That(_singleCalls, Is.Empty);
            Assert.That(results.Select(list => list.Single().MetaverseObjectId), Is.EqualTo(predicted.Cast<Guid?>()),
                "each object gets its own joined objects");
        }
    }

    [Test]
    public async Task GetAsync_ObjectNotPredicted_IsReadSinglyAndStartsNoBulkReadAsync()
    {
        var lookup = NewLookup();
        lookup.BeginPage([Guid.NewGuid(), Guid.NewGuid()]);
        var unpredicted = Guid.NewGuid();

        var result = await lookup.GetAsync(unpredicted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Single().MetaverseObjectId, Is.EqualTo(unpredicted));
            Assert.That(_singleCalls, Is.EqualTo(new[] { unpredicted }), "an object the prediction missed is read singly, as before");
            Assert.That(_batchCalls, Is.Empty, "and costs the page no bulk read");
        }
    }

    [Test]
    public async Task GetAsync_PredictedObjectTheBulkReadLeftOut_IsReadSinglyAsync()
    {
        var predicted = Guid.NewGuid();
        var leftOut = Guid.NewGuid();
        var lookup = new ReElectionSurvivorLookup(
            mvoId =>
            {
                _singleCalls.Add(mvoId);
                return Task.FromResult(new List<ConnectedSystemObject> { new() { Id = Guid.NewGuid(), MetaverseObjectId = mvoId } });
            },
            // Over the value budget, so the bulk read leaves it out.
            mvoIds => Task.FromResult(mvoIds.Where(id => id != leftOut).ToDictionary(id => id, _ => new List<ConnectedSystemObject>())));
        lookup.BeginPage([predicted, leftOut]);

        await lookup.GetAsync(predicted);
        await lookup.GetAsync(leftOut);

        Assert.That(_singleCalls, Is.EqualTo(new[] { leftOut }));
    }

    [Test]
    public async Task BeginPage_NextPage_ForgetsTheLastPagesReadsAsync()
    {
        var first = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var lookup = NewLookup();
        lookup.BeginPage(first);
        await lookup.GetAsync(first[0]);
        _singleCalls.Clear();
        _batchCalls.Clear();

        lookup.BeginPage([]);
        await lookup.GetAsync(first[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_singleCalls, Is.EqualTo(new[] { first[1] }),
                "the last page's bulk read is not reused, since the change tracker is cleared between pages");
            Assert.That(_batchCalls, Is.Empty);
        }
    }
}
