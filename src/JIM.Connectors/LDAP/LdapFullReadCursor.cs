// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Where a Full Import of a directory whose paging cookies are scoped to the connection stands between JIM's calls
/// (#2521): the searches still reading, and the objects they have read that JIM has not yet been handed.
/// </summary>
/// <remarks>
/// <para>
/// OpenLDAP, 389 Directory Server and a generic directory forget a connection's paging cookies as soon as another search
/// is sent on it, so each Container and Object Type is read by a search on a connection of its own. Those searches used
/// to be read to the end inside the first call, which handed JIM the whole directory as one page. Now up to Import
/// Concurrency searches read at once, each into one bounded buffer, and each call takes a page from the buffer. A full
/// buffer makes the searches wait, holding their connections and cookies, so what is held in memory is about one page
/// handed over, one page buffered and one directory page per search, whatever the size of the directory.
/// </para>
/// <para>
/// The searches are read in groups, in order (#2492's reference-dependency order of the Object Types): a group's
/// searches start once every search of the group before it has been read to the end.
/// </para>
/// <para>
/// A search that fails stops the others, and its error is what the next call throws: the read never reports itself
/// complete with objects unread, which would have JIM take every object it was not handed as deleted. Disposing stops
/// the searches and waits for each to close its connection; a search waiting on the directory finishes that request
/// first, which the import's Search Timeout bounds.
/// </para>
/// <para>
/// One cursor serves one import session: the Connector keeps it from the first call to <c>CloseImportConnection</c>.
/// </para>
/// </remarks>
internal sealed class LdapFullReadCursor : IDisposable
{
    /// <summary>
    /// The name of the pagination token that tells JIM there is more, and tells the next call to carry on from this
    /// cursor. Its value means nothing; the position is held here.
    /// </summary>
    internal const string ContinuationTokenName = "ldap-full-read";

    private readonly ConcurrentDictionary<int, string> _reading = new();
    private Channel<ConnectedSystemImportObject>? _buffer;
    private CancellationTokenSource? _stop;
    private Task? _readTask;
    private ExceptionDispatchInfo? _failure;
    private int _nextSearchKey;
    private long _objectsRead;
    private bool _disposed;

    /// <summary>
    /// The entries the searches discarded because an excluded Container carved them out. Shared by every call of the
    /// session, since the searches convert entries between calls; each call reports, and takes, what has accumulated.
    /// </summary>
    internal ExclusionDiscardTally Discards { get; } = new();

    /// <summary>Whether the read has been started, by a run's first call.</summary>
    internal bool IsStarted => _readTask != null;

    /// <summary>Completes once every search has finished and closed its connection, however it ended.</summary>
    internal Task Completion => _readTask ?? Task.CompletedTask;

    /// <summary>
    /// Starts reading.
    /// </summary>
    /// <param name="groups">The searches, in groups, in reading order; each group starts once the one before it is read.</param>
    /// <param name="concurrency">The most searches reading at once, each on a connection of its own.</param>
    /// <param name="bufferCapacity">The most objects held between the searches and JIM.</param>
    /// <param name="readSearchAsync">Reads one search to the end into the buffer, on a connection of its own that it
    /// opens and closes, and returns how many objects it wrote; it waits for room in the buffer, and stops when told to.</param>
    /// <param name="describe">Says what a search reads, for the step JIM shows while a call waits for objects.</param>
    /// <param name="logger">Where the read reports how it went.</param>
    /// <param name="cancellationToken">The run's cancellation, which stops the searches.</param>
    internal void Start<TSearch>(
        IReadOnlyList<IReadOnlyList<TSearch>> groups,
        int concurrency,
        int bufferCapacity,
        Func<TSearch, ChannelWriter<ConnectedSystemImportObject>, CancellationToken, Task<int>> readSearchAsync,
        Func<TSearch, string> describe,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readTask != null)
            throw new InvalidOperationException("This import session has already started its full read; a run reads the directory once.");

        _buffer = Channel.CreateBounded<ConnectedSystemImportObject>(new BoundedChannelOptions(Math.Max(1, bufferCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readTask = ReadAllAsync(groups, Math.Max(1, concurrency), readSearchAsync, describe, logger);
    }

    /// <summary>
    /// Takes up to <paramref name="limit"/> objects into <paramref name="page"/>, waiting for the searches where the
    /// buffer is short, and says whether anything is left to read. Throws a search's failure, whether it came before
    /// this call or during it. When the run is cancelled, returns what it has and says nothing is left: JIM checks for
    /// cancellation itself, and stops.
    /// </summary>
    internal async Task<bool> TakePageAsync(List<ConnectedSystemImportObject> page, int limit, CancellationToken cancellationToken)
    {
        var buffer = _buffer ?? throw new InvalidOperationException("The full read has not been started.");
        ThrowIfASearchFailed();

        bool more;
        try
        {
            while (page.Count < limit && await buffer.Reader.WaitToReadAsync(cancellationToken))
            {
                while (page.Count < limit && buffer.Reader.TryRead(out var importObject))
                    page.Add(importObject);
            }

            // A full page: wait for the next object, or for the searches to finish, so the last page is never an empty one.
            more = page.Count >= limit && await buffer.Reader.WaitToReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // The buffer completes once every search has stopped, a failed one included, so a short page or "no more" can
        // follow a failure; it is thrown, never handed over as the end of the read.
        ThrowIfASearchFailed();
        return more;
    }

    /// <summary>
    /// What the searches are reading now, for the step JIM shows while a call waits for objects; null when nothing is
    /// being read.
    /// </summary>
    internal string? DescribeReading()
    {
        // ToArray is the dictionary's own atomic snapshot. LINQ straight over it sizes its buffer from Count and then
        // enumerates, and a search finishing in between leaves it short, which throws.
        var reading = _reading.ToArray().OrderBy(search => search.Key).Select(search => search.Value).ToList();
        return reading.Count == 0 ? null : $"Fetching {string.Join(", ", reading)}...";
    }

    /// <summary>
    /// Stops the searches and waits for each to close its connection. Whatever was read and not handed over goes with
    /// the cursor, which the Connector lets go of.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stop?.Cancel();

        // ReadAllAsync records every failure rather than throwing, so this waits and nothing more.
        _readTask?.GetAwaiter().GetResult();
        _stop?.Dispose();
    }

    private void ThrowIfASearchFailed() => Volatile.Read(ref _failure)?.Throw();

    private async Task ReadAllAsync<TSearch>(
        IReadOnlyList<IReadOnlyList<TSearch>> groups,
        int concurrency,
        Func<TSearch, ChannelWriter<ConnectedSystemImportObject>, CancellationToken, Task<int>> readSearchAsync,
        Func<TSearch, string> describe,
        ILogger logger)
    {
        var stopwatch = Stopwatch.StartNew();
        var stop = _stop!.Token;
        var searches = 0;
        try
        {
            foreach (var group in groups)
            {
                if (stop.IsCancellationRequested)
                    break;

                // Up to the concurrency at once, each taking the group's next search as it finishes one. A group is read to
                // the end before the next starts.
                var queue = new ConcurrentQueue<TSearch>(group);
                var readers = Enumerable.Range(0, Math.Min(concurrency, group.Count))
                    .Select(_ => Task.Run(() => ReadSearchesAsync(queue, readSearchAsync, describe, stop), CancellationToken.None));
                await Task.WhenAll(readers);
                searches += group.Count;
            }
        }
        finally
        {
            _buffer!.Writer.TryComplete();
            stopwatch.Stop();

            if (_failure != null)
                logger.Warning("LdapFullReadCursor: The full read stopped on a failed search after {Elapsed}; the searches that finished had read {ObjectCount} objects",
                    stopwatch.Elapsed, Interlocked.Read(ref _objectsRead));
            else if (stop.IsCancellationRequested)
                logger.Information("LdapFullReadCursor: The full read was stopped after {Elapsed}; the searches that finished had read {ObjectCount} objects",
                    stopwatch.Elapsed, Interlocked.Read(ref _objectsRead));
            else
                logger.Information("LdapFullReadCursor: Read {ObjectCount} objects from {SearchCount} searches in {GroupCount} Object Type group(s) in {Elapsed}",
                    Interlocked.Read(ref _objectsRead), searches, groups.Count, stopwatch.Elapsed);
        }
    }

    private async Task ReadSearchesAsync<TSearch>(
        ConcurrentQueue<TSearch> queue,
        Func<TSearch, ChannelWriter<ConnectedSystemImportObject>, CancellationToken, Task<int>> readSearchAsync,
        Func<TSearch, string> describe,
        CancellationToken stop)
    {
        while (!stop.IsCancellationRequested && queue.TryDequeue(out var search))
        {
            var key = Interlocked.Increment(ref _nextSearchKey);
            _reading[key] = describe(search);
            try
            {
                Interlocked.Add(ref _objectsRead, await readSearchAsync(search, _buffer!.Writer, stop));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                // The first failure is the one reported; the rest are the others being stopped by it. Anything a search
                // throws while not told to stop is a failure, an unexpected cancellation included: treating it as the
                // search's end would hand JIM a read that looks complete.
                Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(ex), null);
                _stop!.Cancel();
                return;
            }
            catch (OperationCanceledException)
            {
                // Told to stop: the run was cancelled, the import closed, or another search failed.
                return;
            }
            finally
            {
                _reading.TryRemove(key, out _);
            }
        }
    }
}
