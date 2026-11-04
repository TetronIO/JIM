// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Transactional.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// The states a change on the Exchange queue can be in (#2021, Phase 6b), as the REST API and PowerShell offer them: the
/// change states short of leaving the queue, so a filter cannot ask for a state no row holds.
/// </summary>
[TestFixture]
public class ExchangeQueueStateTests
{
    [Test]
    public void EveryQueueState_MapsToTheChangeStateOfTheSameNameAndBack()
    {
        foreach (var queueState in Enum.GetValues<ExchangeQueueState>())
        {
            var changeState = queueState.ToChangeState();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(changeState.ToString(), Is.EqualTo(queueState.ToString()));
                Assert.That(changeState.ToQueueState(), Is.EqualTo(queueState));
            }
        }
    }

    [Test]
    public void ChangeStatesShortOfLeavingTheQueue_AreExactlyTheQueueStates()
    {
        var offQueue = new[] { ExchangeChangeState.Delivered, ExchangeChangeState.Withdrawn, ExchangeChangeState.Replaced };

        Assert.That(Enum.GetValues<ExchangeChangeState>().Except(offQueue).Select(s => s.ToString()),
            Is.EquivalentTo(Enum.GetNames<ExchangeQueueState>()));
    }

    [TestCase(ExchangeChangeState.Delivered)]
    [TestCase(ExchangeChangeState.Withdrawn)]
    [TestCase(ExchangeChangeState.Replaced)]
    public void ToQueueState_StateReachedOnlyOffTheQueue_Throws(ExchangeChangeState state)
    {
        Assert.That(() => state.ToQueueState(), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
