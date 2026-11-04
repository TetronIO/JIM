// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.Tests;

[TestFixture]
public class RemoteSessionStatisticsTests
{
    [Test]
    public void RecordMessage_SeveralMessages_KeepsTheLargestEachWay()
    {
        var statistics = new RemoteSessionStatistics();

        statistics.RecordMessage(requestBytes: 10, responseBytes: 900);
        statistics.RecordMessage(requestBytes: 700, responseBytes: 20);
        statistics.RecordMessage(requestBytes: 30, responseBytes: 40);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statistics.LargestRequestBytes, Is.EqualTo(700));
            Assert.That(statistics.LargestResponseBytes, Is.EqualTo(900));
        }
    }

    [Test]
    public void RecordMessage_FromManyThreadsAtOnce_KeepsTheLargest()
    {
        var statistics = new RemoteSessionStatistics();

        Parallel.For(1, 10_001, i => statistics.RecordMessage(i, 10_001 - i));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statistics.LargestRequestBytes, Is.EqualTo(10_000));
            Assert.That(statistics.LargestResponseBytes, Is.EqualTo(10_000));
        }
    }
}
