// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.Tests;

[TestFixture]
public class RemoteSessionStatisticsTests
{
    [Test]
    public void RecordRequestAndResponse_SeveralMessages_KeepTheLargestEachWay()
    {
        var statistics = new RemoteSessionStatistics();

        statistics.RecordRequest(10);
        statistics.RecordResponse(900);
        statistics.RecordRequest(700);
        statistics.RecordResponse(20);
        statistics.RecordRequest(30);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statistics.LargestRequestBytes, Is.EqualTo(700));
            Assert.That(statistics.LargestResponseBytes, Is.EqualTo(900));
        }
    }

    /// <summary>
    /// A request is counted as it is sent, so one the server refuses (a 413 for being too large) still shows: it is the
    /// one a reader of the statistics most needs to see.
    /// </summary>
    [Test]
    public void RecordRequest_WithNoResponse_IsStillTheLargestRequest()
    {
        var statistics = new RemoteSessionStatistics();

        statistics.RecordRequest(1_400_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statistics.LargestRequestBytes, Is.EqualTo(1_400_000));
            Assert.That(statistics.LargestResponseBytes, Is.Zero);
        }
    }

    [Test]
    public void RecordRequestAndResponse_FromManyThreadsAtOnce_KeepTheLargest()
    {
        var statistics = new RemoteSessionStatistics();

        Parallel.For(1, 10_001, i =>
        {
            statistics.RecordRequest(i);
            statistics.RecordResponse(10_001 - i);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statistics.LargestRequestBytes, Is.EqualTo(10_000));
            Assert.That(statistics.LargestResponseBytes, Is.EqualTo(10_000));
        }
    }
}
