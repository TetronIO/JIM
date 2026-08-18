// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Data.Repositories;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncPreview;

/// <summary>
/// The read-only repository facade behind the #288 preview path (PRD requirement 8, plan Phase 2): one of the
/// defence-in-depth layers around the zero-side-effect guarantee. Reads delegate to the wrapped repository;
/// any write attempt throws <see cref="PreviewWriteAttemptedException"/>, converting an orchestration bug that
/// reaches for a write into a loud failure instead of a silent commit. The reflection sweep pins the
/// classification: every repository member whose name declares a mutation must throw, so a future member
/// misclassified as a read fails this suite rather than shipping a hole in the guarantee.
/// </summary>
[TestFixture]
public class ReadOnlySyncRepositoryGuardTests
{
    private Mock<ISyncRepository> _inner = null!;
    private ReadOnlySyncRepositoryGuard _guard = null!;

    [SetUp]
    public void SetUp()
    {
        _inner = new Mock<ISyncRepository>();
        _guard = new ReadOnlySyncRepositoryGuard(_inner.Object);
    }

    [Test]
    public void CreatePendingExportAsync_IsAWrite_Throws()
    {
        Assert.That(() => _guard.CreatePendingExportAsync(new PendingExport()),
            Throws.InstanceOf<PreviewWriteAttemptedException>().With.Message.Contain(nameof(ISyncRepository.CreatePendingExportAsync)));
    }

    [Test]
    public void DeletePendingExportAsync_IsAWrite_Throws()
    {
        Assert.That(() => _guard.DeletePendingExportAsync(new PendingExport()),
            Throws.InstanceOf<PreviewWriteAttemptedException>());
    }

    [Test]
    public void TryClaimConnectedSystemObjectForJoinAsync_IsAWrite_Throws()
    {
        // The export matching claim is the one write hiding inside an otherwise read-shaped flow; a preview
        // that reached it would join a live CSO to an MVO as a side effect of looking.
        Assert.That(() => _guard.TryClaimConnectedSystemObjectForJoinAsync(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow),
            Throws.InstanceOf<PreviewWriteAttemptedException>());
    }

    [Test]
    public void UpdateConnectedSystemObjectAsync_IsAWrite_Throws()
    {
        Assert.That(() => _guard.UpdateConnectedSystemObjectAsync(new ConnectedSystemObject()),
            Throws.InstanceOf<PreviewWriteAttemptedException>());
    }

    [Test]
    public async Task GetAllSyncRulesAsync_IsARead_DelegatesToTheWrappedRepository()
    {
        var rules = new List<SyncRule> { new() { Name = "rule" } };
        _inner.Setup(r => r.GetAllSyncRulesAsync()).ReturnsAsync(rules);

        var result = await _guard.GetAllSyncRulesAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.SameAs(rules));
            _inner.Verify(r => r.GetAllSyncRulesAsync(), Times.Once);
        }
    }

    [Test]
    public async Task GetPendingExportLightweightByConnectedSystemObjectIdAsync_IsARead_Delegates()
    {
        var csoId = Guid.NewGuid();
        var pe = new PendingExport { Id = Guid.NewGuid() };
        _inner.Setup(r => r.GetPendingExportLightweightByConnectedSystemObjectIdAsync(csoId)).ReturnsAsync(pe);

        var result = await _guard.GetPendingExportLightweightByConnectedSystemObjectIdAsync(csoId);

        Assert.That(result, Is.SameAs(pe));
    }

    [Test]
    public void EveryMutatingRepositoryMember_Throws()
    {
        // The sweep: invoke every ISyncRepository method whose name declares a mutation, with default
        // arguments, and require the guard to throw before touching them (which is also why default/null
        // arguments are safe here). A newly added write member is caught at compile time by the interface;
        // this catches the subtler failure of classifying a write as a delegating read.
        var mutatingVerbs = new[]
        {
            "Create", "Update", "Delete", "Add", "Remove", "Set", "Stamp", "Disconnect", "Bulk", "Save",
            "Truncate", "Mark", "TryClaim", "Claim", "Flush", "Insert", "Upsert", "Replace", "Reset",
            "Persist", "Write", "Apply", "Queue", "Enqueue", "Cancel", "Obsolete", "Expire", "Link",
            "Unlink", "Assign", "Increment", "Record", "Fixup", "Stage", "Release"
        };

        // Change-tracker state operations mutate nothing in the database; the guard delegates them so reused
        // read paths that tidy the tracker keep working. SetAutoDetectChangesEnabled is the one whose name
        // collides with a mutating verb.
        var trackerStateMembers = new[] { "SetAutoDetectChangesEnabled" };

        var mutatingMethods = typeof(ISyncRepository).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => mutatingVerbs.Any(v => m.Name.StartsWith(v, StringComparison.Ordinal)))
            .Where(m => !trackerStateMembers.Contains(m.Name))
            .ToList();

        Assert.That(mutatingMethods, Is.Not.Empty, "The sweep found no mutating members; the verb list is broken");

        var failures = new List<string>();
        foreach (var method in mutatingMethods)
        {
            var args = method.GetParameters()
                .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                .ToArray();
            try
            {
                var result = method.Invoke(_guard, args);
                // The guard throws synchronously, before any await, so a returned Task is a failure too.
                failures.Add($"{method.Name} did not throw");
                (result as IDisposable)?.Dispose();
            }
            catch (TargetInvocationException ex) when (ex.InnerException is PreviewWriteAttemptedException)
            {
                // The required outcome.
            }
            catch (TargetInvocationException ex)
            {
                failures.Add($"{method.Name} threw {ex.InnerException?.GetType().Name} instead of {nameof(PreviewWriteAttemptedException)}");
            }
        }

        Assert.That(failures, Is.Empty,
            "Every mutating repository member must throw PreviewWriteAttemptedException:\n" + string.Join("\n", failures));
    }
}
