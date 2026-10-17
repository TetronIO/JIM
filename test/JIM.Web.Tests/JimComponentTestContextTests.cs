// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The disposal every component test ends with. bUnit 2.11.3 queues the disposal behind whatever holds the renderer's
/// dispatcher, then clears its list of root components straight away, so a component still at work when the test ends
/// is either never disposed or fails the teardown with "Collection was modified".
/// </summary>
[TestFixture]
public class JimComponentTestContextTests : JimComponentTestContext
{
    [Test]
    public async Task DisposeRenderedComponentsAsync_WhileTheDispatcherIsBusy_StillDisposesEveryComponentAsync()
    {
        var component = Render<DisposalRecorder>().Instance;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // Another thread holds the dispatcher, as a component's own continuation does when it finishes a delay.
        var busy = Task.Run(() => Renderer.Dispatcher.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait();
        }));
        entered.Wait();

        var disposing = DisposeRenderedComponentsAsync();
        release.Set();
        await busy;
        await disposing;

        Assert.That(component.Disposed, Is.True);
    }

    private sealed class DisposalRecorder : ComponentBase, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "recorder");
    }
}
