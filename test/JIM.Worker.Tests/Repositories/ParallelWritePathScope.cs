// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.PostgresData;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Makes a <c>RequiresPostgres</c> test genuinely take the parallel COPY write path, and fails loudly rather
/// than silently falling back if it cannot.
/// </summary>
/// <remarks>
/// <para>
/// The bulk write paths choose parallel COPY only when <c>SyncRepository</c> could build a connection string of
/// its own, which it does from the <c>JIM_DB_*</c> environment variables rather than from the DbContext (the
/// context may have been created from options with no discoverable connection string). Tests configure their
/// context from <c>JIM_TEST_RESET_*</c> instead, so those variables are absent, the constructor swallows the
/// failure, and every "parallel path" test quietly runs the single-connection path instead.
/// </para>
/// <para>
/// That is a false green of the worst kind: the assertions still pass, so the suite reports the parallel path
/// as covered while nothing has ever executed it. Any behaviour wired into only one of the two paths, which is
/// the whole hazard these tests exist to catch, ships undetected. Wrapping the repository's construction and
/// use in this scope points the variables at the same database the fixture is verifying against, and
/// <see cref="AssertEngaged"/> asserts the precondition instead of assuming it.
/// </para>
/// </remarks>
public sealed class ParallelWritePathScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new();

    private ParallelWritePathScope()
    {
    }

    /// <summary>
    /// Points the JIM_DB_* variables at the fixture's test database for the lifetime of the scope, restoring
    /// whatever was there before on disposal. Ignores the test when the environment cannot support the parallel
    /// path, so it is skipped visibly rather than passing without exercising it.
    /// </summary>
    public static ParallelWritePathScope Enter()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping parallel write path test.");

        // JimDbContext.BuildConnectionString has no port setting, so the parallel path can only reach a
        // database on the default port. Ignoring here keeps a non-default port an explicit skip rather than a
        // run that quietly writes RPEIs into whichever database is listening on 5432.
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT");
        if (!string.IsNullOrEmpty(port) && port != "5432")
            Assert.Ignore($"JIM_TEST_RESET_PORT is {port}; the parallel write path can only reach the default port.");

        var scope = new ParallelWritePathScope();
        scope.Set(Constants.Config.DatabaseHostname, Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost");
        scope.Set(Constants.Config.DatabaseName, dbName);
        scope.Set(Constants.Config.DatabaseUsername, Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres");
        scope.Set(Constants.Config.DatabasePassword, Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres");
        return scope;
    }

    /// <summary>
    /// Asserts the parallel path will actually be taken for a batch of the given size. This is the same
    /// predicate the write paths themselves use, so a test that calls it can no longer pass by falling back.
    /// </summary>
    public static void AssertEngaged(int itemCount)
    {
        Assert.That(() => JimDbContext.BuildConnectionString(), Throws.Nothing,
            "the repository builds its own connection string for parallel writes; without one it silently uses the single-connection path");
        Assert.That(itemCount, Is.GreaterThanOrEqualTo(ParallelBatchWriter.GetWriteParallelism() * 50),
            "the batch is below the threshold at which the parallel path engages");
    }

    /// <summary>
    /// The batch size at which the parallel path engages, for tests that need to build one that crosses it.
    /// </summary>
    public static int Threshold => ParallelBatchWriter.GetWriteParallelism() * 50;

    private void Set(string name, string? value)
    {
        _previous[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
            Environment.SetEnvironmentVariable(name, value);
    }
}
