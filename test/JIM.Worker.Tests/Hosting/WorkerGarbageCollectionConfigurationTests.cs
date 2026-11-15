// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;

namespace JIM.Worker.Tests.Hosting;

/// <summary>
/// The garbage collector settings the Worker ships with, read from the runtime configuration its build produces (the
/// SDK copies a referenced executable's <c>runtimeconfig.json</c> beside the tests). Measured at Scale500k25kGroups
/// (#2521): conserving memory at level 7 ran three full collections during a confirming Full Import's save instead of
/// one, and cut its peak from 20.4 to 16.9 GiB at no cost in time; most of what the heap held at that peak was garbage.
/// </summary>
[TestFixture]
public class WorkerGarbageCollectionConfigurationTests
{
    private static JsonElement ReadWorkerConfigProperties()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "JIM.Worker.runtimeconfig.json");
        Assert.That(File.Exists(path), Is.True, $"The Worker's runtime configuration was not found at {path}.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties").Clone();
    }

    [Test]
    public void WorkerRuntimeConfiguration_UsesServerGarbageCollection()
    {
        var properties = ReadWorkerConfigProperties();

        Assert.That(properties.GetProperty("System.GC.Server").GetBoolean(), Is.True);
    }

    [Test]
    public void WorkerRuntimeConfiguration_ConservesMemoryAtLevelSeven()
    {
        var properties = ReadWorkerConfigProperties();

        Assert.That(properties.TryGetProperty("System.GC.ConserveMemory", out var conserveMemory), Is.True,
            "The Worker's runtime configuration does not set System.GC.ConserveMemory.");
        Assert.That(conserveMemory.GetInt32(), Is.EqualTo(7));
    }
}
