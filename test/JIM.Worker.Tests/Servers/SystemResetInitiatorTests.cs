// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that a factory reset refuses an initiator it could not attribute before it wipes anything. The reset
/// Activity is recorded after the wipe, so that it survives it; a refusal at that point leaves the data gone, the
/// caller told the reset was refused, and no record of who did it (#2096).
/// </summary>
[TestFixture]
public class SystemResetInitiatorTests
{
    private SeedingTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new SeedingTestHarness();
        _harness.PersistBuiltInConfiguration();
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [TestCase(ActivityInitiatorType.User)]
    [TestCase(ActivityInitiatorType.ApiKey)]
    public void ResetSystemAsync_PrincipalWithNoId_RefusesBeforeTheWipe(ActivityInitiatorType initiatorType)
    {
        Assert.That(
            async () => await _harness.Jim.System.ResetSystemAsync(initiatorType, null, "Someone", includeAdministrators: false),
            Throws.InstanceOf<InvalidOperationException>());
        _harness.SystemRepository.Verify(r => r.ResetSystemAsync(It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public void ResetSystemAsync_InitiatorNotSet_RefusesBeforeTheWipe()
    {
        Assert.That(
            async () => await _harness.Jim.System.ResetSystemAsync(ActivityInitiatorType.NotSet, null, null, includeAdministrators: false),
            Throws.InstanceOf<InvalidOperationException>());
        _harness.SystemRepository.Verify(r => r.ResetSystemAsync(It.IsAny<bool>()), Times.Never);
    }
}
