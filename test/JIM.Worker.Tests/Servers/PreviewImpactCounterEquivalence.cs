// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview;
using JIM.Models.Preview;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The guarantee an adapter makes when it counts from its delta stream (#1530): the framework's one pass, feeding the
/// adapter's counter its own deltas, arrives at exactly the counts the adapter's own stage 2 does. Anything else and the
/// number an administrator consents to would depend on which path counted it.
/// </summary>
internal static class PreviewImpactCounterEquivalence
{
    public static async Task AssertCountsFromItsOwnDeltasAsync(IConfigurationChangePreviewAdapter adapter, PreviewContext context)
    {
        var counter = await adapter.CreateImpactCounterAsync(context);
        Assert.That(counter, Is.Not.Null, "an adapter that counts by evaluating counts in the framework's one pass");

        var singlePass = await PreviewImpactCounter.CountAsync(counter!, adapter.EvaluateDeltasAsync(context, CancellationToken.None));
        var ownStage = await adapter.CountImpactAsync(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(singlePass, Is.Not.Empty, "arrange: the scenario affects something, or the comparison proves nothing");
            Assert.That(singlePass, Is.EqualTo(ownStage), "the one pass counts what the adapter's own stage 2 counts");
        }
    }
}
