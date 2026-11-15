// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Text.Json;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A directory whose paging cookies are scoped to the connection (OpenLDAP, 389 Directory Server, a generic directory)
/// forgets them as soon as another search is sent on the same connection, so each Container and Object Type is read on
/// a connection of its own. Until #2521 the Connector read every one of those searches to the end inside the first
/// call and handed JIM the whole directory as a single page (about 3.5 GB at 500,000 users). These tests drive the full
/// read against a scripted directory, through the connection factory each search's connection comes from, to show that
/// it now hands JIM one page per call while its searches read ahead into a bounded buffer, and that everything else the
/// read returns is as it was.
/// </summary>
[TestFixture]
public class LdapConnectorImportPagedFullReadTests
{
    private const string CorpDn = "dc=corp,dc=local";
    private const string PeopleDn = "ou=people," + CorpDn;
    private const string LeaversDn = "ou=leavers," + PeopleDn;
    private const string GroupsDn = "ou=groups," + CorpDn;
    private const string Person = "inetOrgPerson";
    private const string Group = "groupOfNames";
    private const int PersonTypeId = 1;
    private const int GroupTypeId = 2;
    private const int LeaversContainerId = 12;
    private static readonly ILogger Logger = Serilog.Core.Logger.None;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private ScriptedDirectory _directory = null!;
    private Mock<ILdapOperationExecutor> _executor = null!;
    private List<SearchRequest> _sentOnTheImportsConnection = null!;
    private LdapFullReadCursor _cursor = null!;
    private CancellationTokenSource _run = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = new ScriptedDirectory();
        _sentOnTheImportsConnection = [];
        _cursor = new LdapFullReadCursor();
        _run = new CancellationTokenSource();

        // The import's own connection answers the rootDSE (OpenLDAP) and finds nothing else; every object search goes to
        // a connection of its own, from the factory.
        _executor = new Mock<ILdapOperationExecutor>();
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) => AnswerOnTheImportsConnection(request));
        _executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Returns((DirectoryRequest request, TimeSpan _) => AnswerOnTheImportsConnection(request));
        _executor.Setup(x => x.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .ReturnsAsync((DirectoryRequest request) => AnswerOnTheImportsConnection(request));
    }

    [TearDown]
    public void TearDown()
    {
        _cursor.Dispose();
        _run.Dispose();
    }

    [Test]
    public async Task GetFullImportObjectsAsync_ConnectionScopedDirectory_HandsOverBoundedPagesUntilEverySearchIsReadAsync()
    {
        _directory.AddPeople(25);
        _directory.AddGroups(7);

        var pages = await ReadEveryPageAsync(pageSize: 10, concurrency: 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages, Has.Count.EqualTo(4), "32 objects at 10 a page");
            Assert.That(pages.Select(p => p.ImportObjects.Count), Is.EqualTo(new[] { 10, 10, 10, 2 }));
            Assert.That(pages.Take(3).Select(p => p.PaginationTokens.Count), Is.All.EqualTo(1), "every page but the last says there is more");
            Assert.That(pages[^1].PaginationTokens, Is.Empty, "the last page says the read is complete");
            Assert.That(ExternalIds(pages), Is.EquivalentTo(_directory.AllEntryUuids), "every object is handed over once");
            Assert.That(pages.Select(p => p.CompleteRead), Is.All.True, "every page of a full read says it read every object");
            Assert.That(_directory.Connections, Has.Count.EqualTo(4), "one connection per Container and Object Type");
            Assert.That(_directory.Connections.Select(c => c.Disposed), Is.All.True, "every connection is closed once its search is read");
            Assert.That(_sentOnTheImportsConnection.Where(r => r.Filter?.ToString()?.StartsWith("(objectClass=", StringComparison.Ordinal) == true), Is.Empty,
                "no object search is sent on the import's own connection");
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_ConnectionScopedDirectory_ReturnsTheWatermarkAndDirectoryRecordOnTheFirstPageOnlyAsync()
    {
        _directory.AddPeople(25);

        var pages = await ReadEveryPageAsync(pageSize: 10, concurrency: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages, Has.Count.EqualTo(3));
            Assert.That(pages[0].PersistedConnectorData, Is.Not.Null, "the first page carries the record of the directory");
            Assert.That(pages[0].PartitionWatermarks, Is.Not.Null, "the first page carries the Partition watermarks, taken before anything was read");
            Assert.That(pages[0].PartitionWatermarks!.Keys, Is.EquivalentTo(new[] { 1 }));
            Assert.That(pages.Skip(1).Select(p => p.PersistedConnectorData), Is.All.Null, "later pages leave the record as the first page set it");
            Assert.That(pages.Skip(1).Select(p => p.PartitionWatermarks), Is.All.Null, "later pages leave the watermarks as the first page set them");
            Assert.That(_sentOnTheImportsConnection.Count(r => string.IsNullOrEmpty(r.DistinguishedName)), Is.EqualTo(1), "the rootDSE is read once, on the first page");
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_ASearchFails_ACallFailsWithItsErrorAndNoPageClaimsTheReadCompleteAsync()
    {
        // The directory stops the people search at its limit on that search's third page. The failure reaches JIM on the
        // first call that starts or ends after it was recorded: which call that is depends on how far the search has run
        // ahead, so how many pages go over first is not pinned here. What must hold is that no page handed over says the
        // read was complete, which would have JIM take every object not yet handed over as deleted.
        _directory.AddPeople(60);
        _directory.FailSearchAt(PeopleDn, Person, page: 3, ResultCode.SizeLimitExceeded);

        var pages = new List<ConnectedSystemImportResult>();
        var failure = Assert.ThrowsAsync<CannotPerformImportException>(async () =>
        {
            var tokens = new List<ConnectedSystemPaginationToken>();
            do
            {
                var page = await NewImport(tokens, pageSize: 10, concurrency: 1).GetFullImportObjectsAsync();
                pages.Add(page);
                tokens = page.PaginationTokens;
            } while (tokens.Count > 0);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain("stopped the import of inetOrgPerson objects from people at its search limit"));
            Assert.That(pages.Select(p => p.PaginationTokens.Count), Is.All.EqualTo(1), "every page handed over said there was more");
            Assert.That(_directory.Connections.Select(c => c.Disposed), Is.All.True, "the failed search's connection is closed");
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_ImportConnectionClosedPartWay_StopsTheSearchesAndClosesEveryConnectionAsync()
    {
        _directory.AddPeople(100);
        _directory.AddGroups(100);

        var first = await NewImport([], pageSize: 10, concurrency: 4).GetFullImportObjectsAsync();
        var openWhileReading = _directory.Connections.Count(c => !c.Disposed);

        // What CloseImportConnection does with the cursor.
        _cursor.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.PaginationTokens, Has.Count.EqualTo(1), "the first page says there is more");
            Assert.That(openWhileReading, Is.GreaterThan(0), "the searches are still reading between calls");
            Assert.That(_directory.Connections.Select(c => c.Disposed), Is.All.True, "closing the import closes every search's connection");
            Assert.That(_cursor.Completion.IsCompleted, Is.True, "no search is left running once the import is closed");
        }

        var pagesServed = _directory.PagesServed;
        await Task.Delay(100);
        Assert.That(_directory.PagesServed, Is.EqualTo(pagesServed), "nothing reads the directory after the import is closed");
    }

    [Test]
    public async Task GetFullImportObjectsAsync_RunCancelledPartWay_StopsTheSearchesAndClosesEveryConnectionAsync()
    {
        _directory.AddPeople(100);
        _directory.AddGroups(100);

        var first = await NewImport([], pageSize: 10, concurrency: 4).GetFullImportObjectsAsync();
        await _run.CancelAsync();
        await _cursor.Completion.WaitAsync(Patience);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.PaginationTokens, Has.Count.EqualTo(1), "the first page says there is more");
            Assert.That(_directory.Connections.Select(c => c.Disposed), Is.All.True, "cancelling the run closes every search's connection");
            Assert.That(_directory.PagesServed, Is.LessThan(20), "the directory is not read to the end after the run is cancelled");
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_JimSlowerThanTheDirectory_TheSearchWaitsOnceTheBufferIsFullAsync()
    {
        // One search of 100 entries, 10 to a directory page. Once the first page is handed over, the search may read
        // ahead by one page into the buffer and hold one more page while it waits for room, and no further.
        _directory.AddPeople(100);

        var first = await NewImport([], pageSize: 10, concurrency: 1).GetFullImportObjectsAsync();
        await Task.Delay(300);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.ImportObjects, Has.Count.EqualTo(10));
            Assert.That(_directory.PagesServed, Is.LessThanOrEqualTo(3), "the search waits for JIM rather than reading the directory into memory");
            Assert.That(_directory.Connections.Single().Disposed, Is.False, "the waiting search keeps its connection, and its paging cookie");
        }

        var rest = await ReadEveryPageAsync(pageSize: 10, concurrency: 1, first.PaginationTokens);
        Assert.That(rest.Sum(p => p.ImportObjects.Count) + first.ImportObjects.Count, Is.EqualTo(100), "the search carries on from its cookie as JIM asks for more");
    }

    [Test]
    public async Task GetFullImportObjectsAsync_ObjectTypeReadOrderHanded_ReadsEachGroupOnceTheGroupsBeforeItAreReadAsync()
    {
        // The schema lists people first; the order JIM hands says groups first. Every group search is read to the end
        // before any people search starts, whatever the concurrency allows.
        _directory.AddPeople(30);
        _directory.AddGroups(30);

        var pages = await ReadEveryPageAsync(pageSize: 10, concurrency: 4, order: [[GroupTypeId], [PersonTypeId]]);

        var types = pages.SelectMany(p => p.ImportObjects).Select(o => o.ObjectType).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(types, Has.Count.EqualTo(60));
            Assert.That(types.Take(30), Is.All.EqualTo(Group), "every group is handed over before any person");
            Assert.That(types.Skip(30), Is.All.EqualTo(Person));
            Assert.That(_directory.Searches.FindIndex(s => s.ObjectClass == Person), Is.GreaterThan(_directory.Searches.FindLastIndex(s => s.ObjectClass == Group)),
                "no people search is sent until every group search has been read");
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_ObjectTypeTheOrderDoesNotName_IsReadAfterEveryTypeItDoesNameAsync()
    {
        _directory.AddPeople(15);
        _directory.AddGroups(15);

        var pages = await ReadEveryPageAsync(pageSize: 10, concurrency: 4, order: [[GroupTypeId]]);

        var types = pages.SelectMany(p => p.ImportObjects).Select(o => o.ObjectType).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(types, Has.Count.EqualTo(30), "a selected type the order leaves out is still read");
            Assert.That(types.Take(15), Is.All.EqualTo(Group));
            Assert.That(types.Skip(15), Is.All.EqualTo(Person));
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_EntriesUnderAnExcludedContainer_EachPageReportsOnlyItsOwnDiscardsAsync()
    {
        // JIM adds up each page's discard counts, so a count reported on two pages would be counted twice.
        _directory.AddPeople(20);
        _directory.AddLeavers(12);

        var pages = await ReadEveryPageAsync(pageSize: 5, concurrency: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages.Sum(p => p.ImportObjects.Count), Is.EqualTo(20), "the leavers are not imported");
            Assert.That(pages.SelectMany(p => p.EntriesDiscardedByExclusion).Where(d => d.ContainerId == LeaversContainerId).Sum(d => d.EntriesDiscarded),
                Is.EqualTo(12), "every discarded entry is reported once across the run's pages");
        }
    }

    [Test]
    public async Task GetFullImportObjectsAsync_EachCall_EntersTheFetchStepAndReportsWhatItHandsOverAsync()
    {
        _directory.AddPeople(25);
        var progress = new RecordingConnectorProgress();

        var tokens = new List<ConnectedSystemPaginationToken>();
        var pages = 0;
        do
        {
            var page = await NewImport(tokens, pageSize: 10, concurrency: 1, progress: progress).GetFullImportObjectsAsync();
            tokens = page.PaginationTokens;
            pages++;
        } while (tokens.Count > 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages, Is.EqualTo(3));
            Assert.That(progress.PhaseKeys.Count(k => k == LdapConnectorPhases.Fetch), Is.EqualTo(3), "each call is shown fetching");
            Assert.That(progress.ObjectsRead, Is.EqualTo(new[] { 10, 10, 5 }), "each call counts the objects it handed over");
        }
    }

    [Test]
    public async Task GetDeltaImportObjectsAsync_PartitionWithoutWatermark_ReadsEveryObjectAPageAtATimeAsync()
    {
        // A Delta Import that reads every object instead (#2720) reads the same way: a page per call, each saying it is a
        // complete read and carrying the warning, the watermarks on the first page only.
        _directory.AddPeople(25);

        var pages = new List<ConnectedSystemImportResult>();
        var tokens = new List<ConnectedSystemPaginationToken>();
        do
        {
            var page = await NewImport(tokens, pageSize: 10, concurrency: 2, runType: ConnectedSystemRunType.DeltaImport).GetDeltaImportObjectsAsync();
            pages.Add(page);
            tokens = page.PaginationTokens;
        } while (tokens.Count > 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages.Select(p => p.ImportObjects.Count), Is.EqualTo(new[] { 10, 10, 5 }));
            Assert.That(ExternalIds(pages), Is.EquivalentTo(_directory.AllEntryUuids));
            Assert.That(pages.Select(p => p.CompleteRead), Is.All.True);
            Assert.That(pages.Select(p => p.WarningErrorType), Is.All.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(pages[0].PartitionWatermarks, Is.Not.Null);
            Assert.That(pages.Skip(1).Select(p => p.PartitionWatermarks), Is.All.Null);
        }
    }

    [Test]
    public void GetFullImportObjectsAsync_ContinuationOfAReadThisSessionNeverStarted_Refuses()
    {
        // A continuation reaching a Connector that never served the first page (a new instance mid-run) must not be
        // mistaken for a read with nothing left, which would have JIM take every object it holds as deleted.
        var continuation = new List<ConnectedSystemPaginationToken> { new(LdapFullReadCursor.ContinuationTokenName, "more") };

        Assert.ThrowsAsync<InvalidOperationException>(() => NewImport(continuation, pageSize: 10, concurrency: 1).GetFullImportObjectsAsync());
    }

    #region Helpers

    private LdapConnectorImport NewImport(List<ConnectedSystemPaginationToken> tokens, int pageSize, int concurrency,
        IReadOnlyList<IReadOnlyList<int>>? order = null, RecordingConnectorProgress? progress = null, ConnectedSystemRunType runType = ConnectedSystemRunType.FullImport)
    {
        // The record of the directory the last import left, which a Delta Import reads its change source from.
        var record = JsonSerializer.Serialize(new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP });
        var import = new LdapConnectorImport(ConnectedSystem(),
            new ConnectedSystemRunProfile { Name = runType.ToString(), RunType = runType, PageSize = pageSize },
            new LdapConnection("localhost"), _directory.Open, concurrency, tokens, record, new Dictionary<int, string?> { [1] = null },
            null, "ldap1.corp.local", _ => true, Logger, _run.Token, progress ?? new RecordingConnectorProgress(), _cursor, order);
        import.Executor = _executor.Object;
        return import;
    }

    /// <summary>Calls the import as JIM does, handing each page's tokens to the next call, until a page says it is the last.</summary>
    private async Task<List<ConnectedSystemImportResult>> ReadEveryPageAsync(int pageSize, int concurrency, List<ConnectedSystemPaginationToken>? tokens = null,
        IReadOnlyList<IReadOnlyList<int>>? order = null)
    {
        var pages = new List<ConnectedSystemImportResult>();
        tokens ??= [];
        do
        {
            // As the Connector does with each call's result.
            var import = NewImport(tokens, pageSize, concurrency, order);
            var page = await import.GetFullImportObjectsAsync();
            import.ReportEntriesDiscardedByExclusion(page);
            pages.Add(page);
            tokens = page.PaginationTokens;
            Assert.That(pages, Has.Count.LessThan(1000), "the read never ends");
        } while (tokens.Count > 0);

        await _cursor.Completion.WaitAsync(Patience);
        return pages;
    }

    private static List<string> ExternalIds(IEnumerable<ConnectedSystemImportObject> objects) =>
        objects.Select(o => o.Attributes.Single(a => a.Name.Equals("entryUUID", StringComparison.OrdinalIgnoreCase)).StringValues.Single()).ToList();

    private static List<string> ExternalIds(IEnumerable<ConnectedSystemImportResult> pages) => ExternalIds(pages.SelectMany(p => p.ImportObjects));

    private DirectoryResponse AnswerOnTheImportsConnection(DirectoryRequest directoryRequest)
    {
        var request = (SearchRequest)directoryRequest;
        lock (_sentOnTheImportsConnection)
            _sentOnTheImportsConnection.Add(request);

        // The rootDSE says OpenLDAP; a read of one entry (the accesslog a Delta Import checks it can read) finds it.
        if (string.IsNullOrEmpty(request.DistinguishedName))
            return LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry(string.Empty, ("vendorName", "OpenLDAP")));

        return request.Scope == SearchScope.Base
            ? LdapTestResponses.SearchResponseWith(request.DistinguishedName)
            : LdapTestResponses.EmptySearchResponse();
    }

    private static ConnectedSystem ConnectedSystem()
    {
        var people = new ConnectedSystemContainer { Id = 11, ExternalId = PeopleDn, Name = "people", Selected = true };
        people.AddChildContainer(new ConnectedSystemContainer { Id = LeaversContainerId, ExternalId = LeaversDn, Name = "leavers", Excluded = true });
        var groups = new ConnectedSystemContainer { Id = 13, ExternalId = GroupsDn, Name = "groups", Selected = true };

        return new ConnectedSystem
        {
            Name = "Directory",
            ObjectTypes = [ObjectType(PersonTypeId, Person), ObjectType(GroupTypeId, Group)],
            Partitions = [new ConnectedSystemPartition { Id = 1, ExternalId = CorpDn, Name = "corp.local", Selected = true, Containers = [people, groups] }]
        };
    }

    private static ConnectedSystemObjectType ObjectType(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Selected = true,
        Attributes =
        [
            new ConnectedSystemObjectTypeAttribute { Id = id * 10, Name = "entryUUID", Type = AttributeDataType.Text, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Id = id * 10 + 1, Name = "objectClass", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, Selected = true }
        ]
    };

    /// <summary>
    /// A scripted directory that pages each subtree search by its paging cookie, as OpenLDAP does, and records every
    /// connection the import opened, every search it sent and every page it was served.
    /// </summary>
    private sealed class ScriptedDirectory
    {
        private readonly object _lock = new();
        private readonly List<(string Dn, string ObjectClass, string EntryUuid)> _entries = [];
        private readonly Dictionary<(string Base, string ObjectClass, int Page), ResultCode> _failures = [];
        private readonly List<ScriptedConnection> _connections = [];
        private readonly List<(string Base, string ObjectClass, int Page)> _searches = [];

        internal IReadOnlyList<string> AllEntryUuids
        {
            get { lock (_lock) return _entries.Where(e => !e.Dn.EndsWith(LeaversDn, StringComparison.Ordinal)).Select(e => e.EntryUuid).ToList(); }
        }

        internal List<ScriptedConnection> Connections
        {
            get { lock (_lock) return [.. _connections]; }
        }

        internal List<(string Base, string ObjectClass, int Page)> Searches
        {
            get { lock (_lock) return [.. _searches]; }
        }

        internal int PagesServed
        {
            get { lock (_lock) return _searches.Count; }
        }

        internal void AddPeople(int count) => Add(count, i => $"uid=person{i},{PeopleDn}", Person);

        internal void AddLeavers(int count) => Add(count, i => $"uid=leaver{i},{LeaversDn}", Person);

        internal void AddGroups(int count) => Add(count, i => $"cn=group{i},{GroupsDn}", Group);

        internal void FailSearchAt(string searchBase, string objectClass, int page, ResultCode resultCode) =>
            _failures[(searchBase, objectClass, page)] = resultCode;

        internal ILdapDedicatedConnection Open()
        {
            var connection = new ScriptedConnection(this);
            lock (_lock)
                _connections.Add(connection);
            return connection;
        }

        private void Add(int count, Func<int, string> dnOf, string objectClass)
        {
            for (var i = 0; i < count; i++)
                _entries.Add((dnOf(i), objectClass, Guid.NewGuid().ToString()));
        }

        internal DirectoryResponse Answer(ScriptedConnection connection, SearchRequest request)
        {
            Assert.That(connection.Disposed, Is.False, "a search was sent on a closed connection");
            var filter = request.Filter.ToString()!;
            var objectClass = filter["(objectClass=".Length..^1];
            var paging = request.Controls.OfType<PageResultRequestControl>().Single();
            var offset = paging.Cookie is { Length: > 0 } cookie ? BitConverter.ToInt32(cookie) : 0;
            var page = offset / paging.PageSize + 1;

            List<(string Dn, string ObjectClass, string EntryUuid)> matching;
            lock (_lock)
            {
                _searches.Add((request.DistinguishedName, objectClass, page));
                matching = _entries.Where(e => e.ObjectClass == objectClass && e.Dn.EndsWith(request.DistinguishedName, StringComparison.Ordinal)).ToList();
            }

            if (_failures.TryGetValue((request.DistinguishedName, objectClass, page), out var resultCode))
                throw new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(resultCode, "The size limit was exceeded"), "The size limit was exceeded");

            var entries = matching.Skip(offset).Take(paging.PageSize)
                .Select(e => LdapTestResponses.EntryWithValues(e.Dn, ("objectClass", ["top", e.ObjectClass]), ("entryUUID", [e.EntryUuid])))
                .ToArray();
            var next = offset + paging.PageSize;
            return LdapTestResponses.SearchResponseWithPagingCookie(next < matching.Count ? BitConverter.GetBytes(next) : [], entries);
        }
    }

    private sealed class ScriptedConnection(ScriptedDirectory directory) : ILdapDedicatedConnection
    {
        private volatile bool _disposed;

        internal bool Disposed => _disposed;

        public DirectoryResponse SendRequest(DirectoryRequest request) => directory.Answer(this, (SearchRequest)request);

        public DirectoryResponse SendRequest(DirectoryRequest request, TimeSpan timeout) => directory.Answer(this, (SearchRequest)request);

        public Task<DirectoryResponse> SendRequestAsync(DirectoryRequest request) => Task.FromResult(directory.Answer(this, (SearchRequest)request));

        public void Dispose() => _disposed = true;
    }

    #endregion
}
