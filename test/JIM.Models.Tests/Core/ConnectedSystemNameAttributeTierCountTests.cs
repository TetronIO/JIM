// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// Guard for the SQL paths that coalesce the naming tiers explicitly. In-memory callers iterate
/// <see cref="ObjectNaming.ConnectedSystemNameAttributes"/> and pick up a new tier for free, but the
/// repository queries cannot: EF Core has to translate a fixed expression, so each tier is written out
/// as its own subquery coalesced in preference order. Those clauses would silently ignore an added
/// tier, sorting and labelling by a stale subset while the detail pages resolved the new one.
/// <para>
/// If this fails, extend the tier coalescing in <c>ConnectedSystemRepository</c>
/// (GetConnectedSystemObjectHeadersAsync sort and projection, GetCsoChangeHistoryAsync reference
/// labels) and <c>ActivitiesRepository.GetActivityRunProfileExecutionItemHeadersAsync</c> (search, sort
/// and projection), then update the expected count here.
/// </para>
/// </summary>
[TestFixture]
public class ConnectedSystemNameAttributeTierCountTests
{
    [Test]
    public void ConnectedSystemNameAttributes_TierCount_MatchesHandWrittenSqlCoalescing()
    {
        Assert.That(ObjectNaming.ConnectedSystemNameAttributes, Has.Count.EqualTo(3),
            "The SQL name-resolution clauses coalesce exactly three tiers; see this fixture's summary for the sites to extend.");
    }

    [Test]
    public void MetaverseNameAttributes_ContainsDisplayNameThenCommonName()
    {
        // The Metaverse side has no hand-written tier coalescing (its resolved name is denormalised into
        // MetaverseObjects.CachedDisplayName), but the order is still contractual: it decides what that
        // cache holds, and therefore how the Metaverse list sorts.
        Assert.That(ObjectNaming.MetaverseNameAttributes, Is.EqualTo(new[]
        {
            Constants.BuiltInAttributes.DisplayName,
            Constants.BuiltInAttributes.CommonName
        }));
    }
}
