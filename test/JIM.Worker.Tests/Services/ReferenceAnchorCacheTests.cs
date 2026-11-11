// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Pins what the export's deferred pass remembers of the anchors it has read (#2605). Users are members of many groups,
/// so the pass's chunks name the same objects again and again (7.68 million lookups for about 790,000 objects at
/// Scale750k40kGroups); an anchor read once is not read again. Only an anchor that cannot change during the pass is
/// remembered: one an object already holds, of an object no export of the pass gives an anchor.
/// </summary>
[TestFixture]
public class ReferenceAnchorCacheTests
{
    [Test]
    public void TakeKnown_NothingRemembered_LeavesEveryIdToFetch()
    {
        var cache = new ReferenceAnchorCache(new HashSet<Guid>());
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var anchors = new Dictionary<Guid, ReferenceAnchorSummary>();

        var toFetch = cache.TakeKnown(ids, anchors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(toFetch, Is.EquivalentTo(ids));
            Assert.That(anchors, Is.Empty);
        }
    }

    [Test]
    public void Remember_AnchorHeld_AnswersItNextTimeWithoutFetching()
    {
        var cache = new ReferenceAnchorCache(new HashSet<Guid>());
        var known = Guid.NewGuid();
        var unseen = Guid.NewGuid();
        var anchor = new ReferenceAnchorSummary(Guid.NewGuid(), "cn=ada,ou=people,dc=glitterband,dc=local", IsSecondaryExternalId: true);
        cache.Remember(new Dictionary<Guid, ReferenceAnchorSummary> { [known] = anchor });
        var anchors = new Dictionary<Guid, ReferenceAnchorSummary>();

        var toFetch = cache.TakeKnown([known, unseen], anchors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(toFetch, Is.EqualTo(new[] { unseen }));
            Assert.That(anchors, Is.EquivalentTo(new Dictionary<Guid, ReferenceAnchorSummary> { [known] = anchor }));
            Assert.That(cache.Lookups, Is.EqualTo(2));
            Assert.That(cache.Hits, Is.EqualTo(1));
            Assert.That(cache.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void Remember_ObjectWithoutAnAnchorYet_IsFetchedAgain()
    {
        // An object created earlier in the pass may gain its anchor later in it.
        var cache = new ReferenceAnchorCache(new HashSet<Guid>());
        var waiting = Guid.NewGuid();
        cache.Remember(new Dictionary<Guid, ReferenceAnchorSummary> { [waiting] = new(Guid.NewGuid(), AnchorValue: null, IsSecondaryExternalId: false) });

        var toFetch = cache.TakeKnown([waiting], new Dictionary<Guid, ReferenceAnchorSummary>());

        Assert.That(toFetch, Is.EqualTo(new[] { waiting }));
    }

    [Test]
    public void Remember_ObjectWhoseAnchorAnExportOfThePassSupplies_IsFetchedAgain()
    {
        // A rename changes the anchor an object already holds, once the export carrying it is written.
        var renamed = Guid.NewGuid();
        var cache = new ReferenceAnchorCache(new HashSet<Guid> { renamed });
        cache.Remember(new Dictionary<Guid, ReferenceAnchorSummary> { [renamed] = new(Guid.NewGuid(), "cn=old,ou=groups,dc=glitterband,dc=local", true) });

        var toFetch = cache.TakeKnown([renamed], new Dictionary<Guid, ReferenceAnchorSummary>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(toFetch, Is.EqualTo(new[] { renamed }));
            Assert.That(cache.Count, Is.Zero);
        }
    }

    [Test]
    public void Remember_AtCapacity_KeepsWhatItHoldsAndRemembersNothingMore()
    {
        var cache = new ReferenceAnchorCache(new HashSet<Guid>(), capacity: 2);
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        cache.Remember(ids.ToDictionary(id => id, id => new ReferenceAnchorSummary(Guid.NewGuid(), $"anchor-{id:N}", false)));

        var anchors = new Dictionary<Guid, ReferenceAnchorSummary>();
        var toFetch = cache.TakeKnown(ids, anchors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Count, Is.EqualTo(2));
            Assert.That(anchors, Has.Count.EqualTo(2));
            Assert.That(toFetch, Has.Count.EqualTo(1));
            Assert.That(anchors.Keys.Concat(toFetch), Is.EquivalentTo(ids), "every id is either answered or left to fetch");
        }
    }
}
