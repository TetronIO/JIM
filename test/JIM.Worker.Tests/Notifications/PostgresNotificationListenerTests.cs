// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.PostgresData;
using NUnit.Framework;

namespace JIM.Worker.Tests.Notifications;

[TestFixture]
public class PostgresNotificationListenerTests
{
    [Test]
    public void GetReconnectDelay_FirstAttempt_ReturnsOneSecond()
    {
        Assert.That(PostgresNotificationListener.GetReconnectDelay(1), Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void GetReconnectDelay_SubsequentAttempts_DoubleEachTime()
    {
        Assert.That(PostgresNotificationListener.GetReconnectDelay(2), Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(PostgresNotificationListener.GetReconnectDelay(3), Is.EqualTo(TimeSpan.FromSeconds(4)));
        Assert.That(PostgresNotificationListener.GetReconnectDelay(4), Is.EqualTo(TimeSpan.FromSeconds(8)));
    }

    [Test]
    public void GetReconnectDelay_LargeAttempts_CapAtSixtySeconds()
    {
        Assert.That(PostgresNotificationListener.GetReconnectDelay(7), Is.EqualTo(TimeSpan.FromSeconds(60)));
        Assert.That(PostgresNotificationListener.GetReconnectDelay(100), Is.EqualTo(TimeSpan.FromSeconds(60)));
        Assert.That(PostgresNotificationListener.GetReconnectDelay(int.MaxValue), Is.EqualTo(TimeSpan.FromSeconds(60)));
    }

    [Test]
    public void GetReconnectDelay_ZeroOrNegativeAttempt_ReturnsOneSecond()
    {
        Assert.That(PostgresNotificationListener.GetReconnectDelay(0), Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(PostgresNotificationListener.GetReconnectDelay(-5), Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void ValidateChannelName_ValidChannelNames_DoNotThrow()
    {
        Assert.DoesNotThrow(() => PostgresNotificationListener.ValidateChannelName("jim_worker_task_change"));
        Assert.DoesNotThrow(() => PostgresNotificationListener.ValidateChannelName("jim_activity_progress"));
    }

    [Test]
    public void ValidateChannelName_InvalidChannelNames_Throw()
    {
        Assert.Throws<ArgumentException>(() => PostgresNotificationListener.ValidateChannelName("bad name"));
        Assert.Throws<ArgumentException>(() => PostgresNotificationListener.ValidateChannelName("bad\"name"));
        Assert.Throws<ArgumentException>(() => PostgresNotificationListener.ValidateChannelName("bad;drop table"));
        Assert.Throws<ArgumentException>(() => PostgresNotificationListener.ValidateChannelName(""));
        Assert.Throws<ArgumentException>(() => PostgresNotificationListener.ValidateChannelName("UPPERCASE"));
    }

    [Test]
    public void BuildListenerConnectionString_DisablesPoolingAndSetsKeepalive()
    {
        TestUtilities.SetEnvironmentVariables();

        var connectionString = JimDbContext.BuildListenerConnectionString();

        Assert.That(connectionString, Does.Contain("Pooling=false"));
        Assert.That(connectionString, Does.Contain("Keepalive=30"));
        Assert.That(connectionString, Does.Not.Contain("Minimum Pool Size"));
    }
}
