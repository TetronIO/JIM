// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JIM.Web.Services;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class NotificationDebouncerTests
{
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FlushWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly List<IReadOnlyCollection<Guid>> _flushes = [];
    private SemaphoreSlim _flushSignal = null!;

    [SetUp]
    public void SetUp()
    {
        _flushes.Clear();
        _flushSignal = new SemaphoreSlim(0);
    }

    [TearDown]
    public void TearDown()
    {
        _flushSignal.Dispose();
    }

    private void RecordFlush(IReadOnlyCollection<Guid> keys)
    {
        lock (_flushes)
            _flushes.Add(keys);
        _flushSignal.Release();
    }

    [Test]
    public async Task Notify_SingleKey_InvokesCallbackOnceWithThatKeyAsync()
    {
        // Arrange
        using var debouncer = new NotificationDebouncer<Guid>(RecordFlush, QuietWindow);
        var key = Guid.NewGuid();

        // Act
        debouncer.Notify(key);
        var flushed = await _flushSignal.WaitAsync(FlushWaitTimeout);

        // Assert
        Assert.That(flushed, Is.True, "Expected the callback to be invoked after the quiet window.");
        Assert.That(_flushes, Has.Count.EqualTo(1));
        Assert.That(_flushes[0], Is.EquivalentTo(new[] { key }));
    }

    [Test]
    public async Task Notify_BurstOfSameKey_InvokesCallbackOnceWithSingleKeyAsync()
    {
        // Arrange
        using var debouncer = new NotificationDebouncer<Guid>(RecordFlush, QuietWindow);
        var key = Guid.NewGuid();

        // Act
        for (var i = 0; i < 10; i++)
            debouncer.Notify(key);
        var flushed = await _flushSignal.WaitAsync(FlushWaitTimeout);

        // Allow time for any (incorrect) second callback to arrive before asserting the count.
        await Task.Delay(QuietWindow * 3);

        // Assert
        Assert.That(flushed, Is.True, "Expected the callback to be invoked after the quiet window.");
        Assert.That(_flushes, Has.Count.EqualTo(1));
        Assert.That(_flushes[0], Is.EquivalentTo(new[] { key }));
    }

    [Test]
    public async Task Notify_BurstOfDistinctKeys_InvokesCallbackOnceWithAllKeysAsync()
    {
        // Arrange
        using var debouncer = new NotificationDebouncer<Guid>(RecordFlush, QuietWindow);
        var keys = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

        // Act
        foreach (var key in keys)
            debouncer.Notify(key);
        var flushed = await _flushSignal.WaitAsync(FlushWaitTimeout);

        // Allow time for any (incorrect) second callback to arrive before asserting the count.
        await Task.Delay(QuietWindow * 3);

        // Assert
        Assert.That(flushed, Is.True, "Expected the callback to be invoked after the quiet window.");
        Assert.That(_flushes, Has.Count.EqualTo(1));
        Assert.That(_flushes[0], Is.EquivalentTo(keys));
    }

    [Test]
    public async Task Notify_AfterPreviousFlush_InvokesCallbackAgainAsync()
    {
        // Arrange
        using var debouncer = new NotificationDebouncer<Guid>(RecordFlush, QuietWindow);
        var firstKey = Guid.NewGuid();
        var secondKey = Guid.NewGuid();

        // Act
        debouncer.Notify(firstKey);
        var firstFlushed = await _flushSignal.WaitAsync(FlushWaitTimeout);
        debouncer.Notify(secondKey);
        var secondFlushed = await _flushSignal.WaitAsync(FlushWaitTimeout);

        // Assert
        Assert.That(firstFlushed, Is.True, "Expected a callback for the first notification.");
        Assert.That(secondFlushed, Is.True, "Expected a fresh callback for a notification after a flush.");
        Assert.That(_flushes, Has.Count.EqualTo(2));
        Assert.That(_flushes[0], Is.EquivalentTo(new[] { firstKey }));
        Assert.That(_flushes[1], Is.EquivalentTo(new[] { secondKey }));
    }

    [Test]
    public async Task Dispose_WithPendingKeys_StopsCallbacksAsync()
    {
        // Arrange
        var debouncer = new NotificationDebouncer<Guid>(RecordFlush, TimeSpan.FromMilliseconds(500));

        // Act: dispose before the quiet window elapses; the pending key must never be flushed.
        debouncer.Notify(Guid.NewGuid());
        debouncer.Dispose();
        var flushed = await _flushSignal.WaitAsync(TimeSpan.FromMilliseconds(1500));

        // Assert
        Assert.That(flushed, Is.False, "Expected no callback after disposal.");
        Assert.That(_flushes, Is.Empty);
    }

    [Test]
    public void Notify_AfterDispose_DoesNotThrow()
    {
        // Arrange
        var debouncer = new NotificationDebouncer<Guid>(RecordFlush, QuietWindow);
        debouncer.Dispose();

        // Act & Assert
        Assert.That(() => debouncer.Notify(Guid.NewGuid()), Throws.Nothing);
    }
}
