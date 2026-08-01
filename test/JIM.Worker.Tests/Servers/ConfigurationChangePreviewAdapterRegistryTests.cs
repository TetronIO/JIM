// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview;
using JIM.Models.Preview;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The registry decides which adapter evaluates a proposed change. Every way it can be wrong is silent at runtime
/// and wrong in the worst possible place: an administrator reads a confident preview of a change that was never
/// evaluated. So it refuses ambiguity rather than resolving it, and says which surface is missing rather than
/// answering nothing.
/// </summary>
[TestFixture]
public class ConfigurationChangePreviewAdapterRegistryTests
{
    [Test]
    public void Get_RegisteredSurface_ReturnsItsAdapter()
    {
        var adapter = new FakeAdapter(ConfigurationChangePreviewSurface.MetaverseObjectType);
        var registry = new ConfigurationChangePreviewAdapterRegistry([adapter]);

        Assert.That(registry.Get(ConfigurationChangePreviewSurface.MetaverseObjectType), Is.SameAs(adapter));
    }

    [Test]
    public void Get_SurfaceWithNoAdapter_ThrowsNamingTheSurface()
    {
        var registry = new ConfigurationChangePreviewAdapterRegistry([new FakeAdapter(ConfigurationChangePreviewSurface.ConnectedSystem)]);

        Assert.That(() => registry.Get(ConfigurationChangePreviewSurface.SynchronisationRule),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("SynchronisationRule"));
    }

    [Test]
    public void Constructor_TwoAdaptersForOneSurface_Throws()
    {
        // Last-one-wins would mean a preview is evaluated by whichever adapter happened to register second, which
        // no test and no log would ever reveal.
        var duplicate = new[]
        {
            new FakeAdapter(ConfigurationChangePreviewSurface.ConnectedSystem),
            new FakeAdapter(ConfigurationChangePreviewSurface.ConnectedSystem)
        };

        Assert.That(() => new ConfigurationChangePreviewAdapterRegistry(duplicate),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("ConnectedSystem"));
    }

    [Test]
    public void Constructor_AdapterWithNoSurface_Throws()
    {
        // NotSet is the uninitialised default, so an adapter carrying it has not declared what it serves.
        Assert.That(() => new ConfigurationChangePreviewAdapterRegistry([new FakeAdapter(ConfigurationChangePreviewSurface.NotSet)]),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void HasAdapterFor_UnregisteredSurface_IsFalse()
    {
        // A surface without an adapter is the normal state during the adapter roll-out: it keeps the save-time
        // acknowledgement and shows no preview. Asking must therefore be cheap and must not throw.
        var registry = new ConfigurationChangePreviewAdapterRegistry([new FakeAdapter(ConfigurationChangePreviewSurface.MetaverseObjectType)]);

        Assert.Multiple(() =>
        {
            Assert.That(registry.HasAdapterFor(ConfigurationChangePreviewSurface.MetaverseObjectType), Is.True);
            Assert.That(registry.HasAdapterFor(ConfigurationChangePreviewSurface.ConnectedSystem), Is.False);
        });
    }

    [Test]
    public void RegisteredSurfaces_ListsEverySurfaceServed()
    {
        var registry = new ConfigurationChangePreviewAdapterRegistry([
            new FakeAdapter(ConfigurationChangePreviewSurface.MetaverseObjectType),
            new FakeAdapter(ConfigurationChangePreviewSurface.ConnectedSystem)
        ]);

        Assert.That(registry.RegisteredSurfaces, Is.EquivalentTo(new[]
        {
            ConfigurationChangePreviewSurface.MetaverseObjectType,
            ConfigurationChangePreviewSurface.ConnectedSystem
        }));
    }

    /// <summary>
    /// A do-nothing adapter. The framework's own tests drive it rather than any real surface, which is what lets the
    /// orchestration be tested before the first adapter exists.
    /// </summary>
    private sealed class FakeAdapter(ConfigurationChangePreviewSurface surface) : IConfigurationChangePreviewAdapter
    {
        public ConfigurationChangePreviewSurface Surface { get; } = surface;

        public Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context) => Task.FromResult(new List<PreviewValidationFinding>());

        public Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context) => Task.FromResult(new PreviewCostEstimate(0));

        public Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context) => Task.FromResult(new List<PreviewImpactCount>());

        public IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context, CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<PreviewDelta>();
    }
}
