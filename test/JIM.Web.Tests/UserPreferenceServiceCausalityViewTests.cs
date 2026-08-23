// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Web.Services;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="UserPreferenceService"/>'s causality view whitelist (#1495). The service
/// validates stored view keys, and the whitelist silently drops anything it does not know: a
/// Spine selection was written nowhere and read back as null, so the choice reverted to Flow on
/// every navigation. Found at runtime, because the bUnit fake does not enforce the whitelist;
/// these tests pin the real service's list instead.
/// </summary>
[TestFixture]
public class UserPreferenceServiceCausalityViewTests
{
    private BunitContext _context = null!;
    private UserPreferenceService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new BunitContext();
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _service = new UserPreferenceService(_context.JSInterop.JSRuntime);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _context.DisposeAsync();
    }

    [TestCase("flow")]
    [TestCase("timeline")]
    [TestCase("graph")]
    [TestCase("spine")]
    public async Task SetCausalityViewAsync_EveryShippedView_IsPersisted(string view)
    {
        await _service.SetCausalityViewAsync(view);

        var writes = _context.JSInterop.Invocations
            .Where(i => i.Identifier == "jimPreferences.set")
            .ToList();
        Assert.That(writes, Has.Count.EqualTo(1),
            $"the whitelist silently dropped the \"{view}\" preference write");
        Assert.That(writes[0].Arguments, Does.Contain(view));
    }

    [TestCase("flow")]
    [TestCase("timeline")]
    [TestCase("graph")]
    [TestCase("spine")]
    public async Task GetCausalityViewAsync_EveryShippedView_IsReturned(string view)
    {
        _context.JSInterop.Setup<string?>("jimPreferences.get", "causalityView").SetResult(view);

        Assert.That(await _service.GetCausalityViewAsync(), Is.EqualTo(view));
    }

    [Test]
    public async Task SetCausalityViewAsync_UnknownValue_IsStillRejected()
    {
        await _service.SetCausalityViewAsync("carousel");

        Assert.That(_context.JSInterop.Invocations.Where(i => i.Identifier == "jimPreferences.set"),
            Is.Empty);
    }
}
