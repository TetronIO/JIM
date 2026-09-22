// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Text;
namespace JIM.Connectors.LDAP;

/// <summary>
/// The change source for directories that publish a draft-good-ldap-changelog (389 Directory Server, and the
/// generic fallback). The changelog is a flat container of numbered entries, one per write, each naming what
/// changed (targetDN) and how (changeType); the highest change number the last import saw is the watermark, and
/// a Delta Import reads the entries numbered after it, fetching each target's current state by DN.
/// <para>
/// Where the changelog is comes from the rootDSE (<c>changelog</c>), falling back to the conventional
/// <c>cn=changelog</c>. So does the watermark, when the rootDSE advertises <c>lastChangeNumber</c>; otherwise the
/// changelog is enumerated for it. Both are read off the rootDSE before this source is called.
/// </para>
/// <para>
/// <b>Every silence is an unknown, never a denial.</b> Only the directory's own definite answer (noSuchObject, an
/// explicit refusal) makes the changelog unavailable; a connection-level fault is reported as undetermined by the
/// readiness check and propagates from a read, because a failed connection is an error rather than an empty
/// directory. And a changelog whose base cannot be found is never proof of absence: 389 Directory Server answers
/// noSuchObject for a base the bound account may not read, so the texts name both causes (#1725).
/// </para>
/// <para>
/// A deletion cannot be fetched, so it is identified from the deleted entry the delete record carries in
/// <c>changes</c> (389 Directory Server writes it there once the Retro Changelog plug-in's
/// <c>nsslapd-log-deleted</c> is on; it is off by default). The readiness check reads that switch and refuses the
/// Delta Import when it is provably off, since deletions would otherwise go unnoticed; a delete record met with no
/// deleted entry in it is noted on the run rather than staged as a delete that names nothing. A rename's
/// <c>targetDN</c> is where the object was, so the new DN is built from <c>newRdn</c> and <c>newSuperior</c>, and
/// scope is judged there.
/// </para>
/// </summary>
internal sealed class LdapChangelogDeltaSource : ILdapDeltaSource
{
    /// <summary>The changelog attribute a Delta Import keys on; the same one the watermark is taken from.</summary>
    private const string ChangeNumberAttribute = "changeNumber";

    /// <summary>RFC 4511's "no attributes" marker, for a read that only asks whether the entry is there.</summary>
    private static readonly string[] NoAttributes = ["1.1"];

    /// <summary>
    /// What a Delta Import needs from a changelog entry: the number to advance the cursor by, the kind of change,
    /// the DN it names, and what a delete or a rename needs beyond that. On a delete record, <c>changes</c> holds
    /// the deleted entry as LDIF (389 Directory Server, once the Retro Changelog plug-in's <c>nsslapd-log-deleted</c>
    /// is on), which is the only way to learn its Object Type and entryUUID now that it is gone; on a modrdn record,
    /// <c>newRdn</c> and <c>newSuperior</c> say where the object now is, since <c>targetDN</c> is where it was. For
    /// adds and modifies the target's current state is fetched, so the LDIF of the write is not used.
    /// </summary>
    private static readonly string[] ChangeAttributes = [ChangeNumberAttribute, "changeType", "targetDN", ChangesAttribute, NewRdnAttribute, NewSuperiorAttribute];

    private const string ChangesAttribute = "changes";
    private const string NewRdnAttribute = "newRdn";
    private const string NewSuperiorAttribute = "newSuperior";

    /// <summary>What the plug-in probe reads: the one attribute that says whether delete records carry the deleted entry.</summary>
    private static readonly string[] LogDeletedAttributes = [LdapConnectorConstants.RETRO_CHANGELOG_LOG_DELETED_ATTRIBUTE];

    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapChangelogDeltaSource(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// Where the directory keeps its changelog: what its rootDSE says, or the conventional place when it says nothing.
    /// </summary>
    internal static string ChangelogDnFor(LdapConnectorRootDse rootDse) => rootDse.ChangelogDn ?? LdapConnectorConstants.DEFAULT_CHANGELOG_DN;

    #region Watermark

    /// <summary>
    /// Records the highest change number the changelog holds now, so the next Delta Import reads from there. The
    /// rootDSE's <c>lastChangeNumber</c> is taken when advertised; otherwise the changelog is enumerated and the
    /// maximum taken, since LDAP guarantees no order to the entries. Null, which makes the next Delta Import run as
    /// a Full Import, when no readable changelog was found: recording zero there would read as "every change since
    /// the beginning" against a changelog that may not exist.
    /// </summary>
    public Task CaptureWatermarkAsync(SearchResultEntry rootDseEntry, LdapConnectorRootDse rootDse, TimeSpan searchTimeout)
    {
        var changelogDn = ChangelogDnFor(rootDse);

        if (rootDse.AdvertisedLastChangeNumber is { } advertised)
        {
            _logger.Debug("LdapChangelogDeltaSource: The rootDSE advertises lastChangeNumber {LastChangeNumber}; recorded as the watermark without enumerating {ChangelogDn}",
                advertised, LogSanitiser.Sanitise(changelogDn));
            rootDse.LastChangeNumber = advertised;
            return Task.CompletedTask;
        }

        var probe = ProbeChangelog(changelogDn);
        if (probe.Outcome != ChangelogProbeOutcome.Readable)
        {
            _logger.Warning("LdapChangelogDeltaSource: No watermark was recorded because the changelog at {ChangelogDn} could not be read ({Outcome}{Detail}); the next Delta Import will run as a Full Import",
                LogSanitiser.Sanitise(changelogDn), probe.Outcome, probe.Detail == null ? string.Empty : ": " + probe.Detail);
            rootDse.LastChangeNumber = null;
            return Task.CompletedTask;
        }

        rootDse.LastChangeNumber = FindHighestChangeNumber(changelogDn, searchTimeout);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Enumerates the changelog for its highest change number. Zero when it is readable but empty. When the
    /// directory stops at its size limit, the highest of what it did answer: a watermark below the truth is the
    /// safe direction, since the next Delta Import then re-reads a few changes rather than skipping any. Null when
    /// the enumeration itself was refused or failed after the probe said the changelog was there.
    /// </summary>
    private long? FindHighestChangeNumber(string changelogDn, TimeSpan searchTimeout)
    {
        var request = new SearchRequest(changelogDn, $"({ChangeNumberAttribute}=*)", SearchScope.OneLevel, ChangeNumberAttribute);

        IReadOnlyList<SearchResultEntry> entries;
        try
        {
            entries = ((SearchResponse)_executor.SendRequest(request, searchTimeout)).Entries.Cast<SearchResultEntry>().ToList();
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            _logger.Warning("LdapChangelogDeltaSource: No watermark was recorded; the changelog at {ChangelogDn} was not found when enumerated", LogSanitiser.Sanitise(changelogDn));
            return null;
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded)
        {
            entries = PartialEntries(ex);
            _logger.Information("LdapChangelogDeltaSource: The directory stopped enumerating the changelog at {ChangelogDn} at its size limit after {Count} entries; the watermark is the highest of those, so the next Delta Import re-reads the rest",
                LogSanitiser.Sanitise(changelogDn), entries.Count);
        }
        catch (DirectoryOperationException ex)
        {
            _logger.Warning("LdapChangelogDeltaSource: No watermark was recorded; the directory refused to enumerate the changelog at {ChangelogDn}: {Message}",
                LogSanitiser.Sanitise(changelogDn), LogSanitiser.Sanitise(ex.Message));
            return null;
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // noSuchObject
        {
            _logger.Warning("LdapChangelogDeltaSource: No watermark was recorded; the changelog at {ChangelogDn} was not found when enumerated", LogSanitiser.Sanitise(changelogDn));
            return null;
        }
        catch (LdapException ex)
        {
            _logger.Warning("LdapChangelogDeltaSource: No watermark was recorded; enumerating the changelog at {ChangelogDn} failed: {Message}",
                LogSanitiser.Sanitise(changelogDn), LogSanitiser.Sanitise(ex.Message));
            return null;
        }

        return HighestChangeNumber(entries) ?? 0;
    }

    /// <summary>
    /// The highest change number among the entries, or null when none carries one that reads as a number.
    /// </summary>
    private long? HighestChangeNumber(IReadOnlyList<SearchResultEntry> entries)
    {
        long? highest = null;
        foreach (var entry in entries)
        {
            if (TryReadChangeNumber(entry, out var changeNumber) && (highest == null || changeNumber > highest))
                highest = changeNumber;
        }

        return highest;
    }

    private bool TryReadChangeNumber(SearchResultEntry entry, out long changeNumber)
    {
        var text = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, ChangeNumberAttribute);
        if (text != null && long.TryParse(text, out changeNumber))
            return true;

        _logger.Warning("LdapChangelogDeltaSource: The changelog entry {EntryDn} carries no changeNumber that reads as a number ({Value}); it is ignored",
            LogSanitiser.Sanitise(entry.DistinguishedName), LogSanitiser.Sanitise(text));
        changeNumber = 0;
        return false;
    }

    /// <summary>The entries the directory did answer before stopping at its size limit; none when it kept them back.</summary>
    private static IReadOnlyList<SearchResultEntry> PartialEntries(DirectoryOperationException ex) =>
        (ex.Response as SearchResponse)?.Entries.Cast<SearchResultEntry>().ToList() ?? [];

    #endregion

    #region Continuity and readiness

    /// <summary>
    /// Refuses to read from a watermark the changelog has been trimmed past: when the rootDSE advertises the
    /// oldest change number still held and it is beyond the one after the watermark, the changes between were
    /// discarded (389 Directory Server: the Retro Changelog plug-in's maximum age) and a Delta Import would skip
    /// them without noticing. A directory that does not advertise its first change number cannot be checked here.
    /// </summary>
    public void VerifyContinuity(LdapConnectorRootDse previous, LdapConnectorRootDse current)
    {
        if (current.FirstChangeNumber is not { } first || previous.LastChangeNumber is not { } last || last + 1 >= first)
            return;

        _logger.Warning("LdapChangelogDeltaSource: Refusing the Delta Import; the changelog now starts at change number {First} and the last import ended at {Last}",
            first, last);

        throw new CannotPerformDeltaImportException(
            $"The directory's changelog no longer holds the changes since the last import: it starts at change number {first}, " +
            $"and the last import ended at {last}, so changes between them were trimmed (389 Directory Server: the Retro Changelog plug-in's maximum age) " +
            "and cannot be imported. Run a Full Import, which also detects deletions by absence, to re-establish the baseline.");
    }

    /// <summary>
    /// Reads the changelog's own entry, asking for no attributes, to establish that it is there and that the
    /// account JIM connects as may read it. One finding, about the changelog's DN; the naming contexts are not
    /// consulted, because a changelog is directory-wide.
    /// </summary>
    public Task<IReadOnlyList<LdapDeltaSourceFinding>> VerifyReadinessAsync(LdapConnectorRootDse rootDse, IReadOnlyCollection<string> namingContexts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var changelogDn = ChangelogDnFor(rootDse);
        var probe = ProbeChangelog(changelogDn);

        LdapDeltaSourceFinding finding = probe.Outcome switch
        {
            ChangelogProbeOutcome.Readable => new LdapDeltaSourceFinding
            {
                Subject = changelogDn,
                Outcome = LdapDeltaSourceOutcome.Available,
                Detail = $"the changelog at {changelogDn} can be read"
            },
            ChangelogProbeOutcome.NotFound => new LdapDeltaSourceFinding
            {
                Subject = changelogDn,
                Outcome = LdapDeltaSourceOutcome.Unavailable,
                Detail = $"no changelog at {changelogDn}, or none the account JIM connects as may read",
                DeltaImportText = DescribeForDeltaImport(changelogDn, refusal: null),
                SchemaDiscoveryText = DescribeForSchemaDiscovery(changelogDn, refusal: null)
            },
            ChangelogProbeOutcome.Refused => new LdapDeltaSourceFinding
            {
                Subject = changelogDn,
                Outcome = LdapDeltaSourceOutcome.Unavailable,
                Detail = $"the directory refused to read the changelog at {changelogDn} ({probe.Detail})",
                DeltaImportText = DescribeForDeltaImport(changelogDn, probe.Detail),
                SchemaDiscoveryText = DescribeForSchemaDiscovery(changelogDn, probe.Detail)
            },
            _ => new LdapDeltaSourceFinding
            {
                Subject = changelogDn,
                Outcome = LdapDeltaSourceOutcome.CouldNotDetermine,
                Detail = probe.Detail!,
                DeltaImportText = DescribeUndetermined(changelogDn, probe.Detail!),
                SchemaDiscoveryText = DescribeUndetermined(changelogDn, probe.Detail!)
            }
        };

        if (finding.Outcome == LdapDeltaSourceOutcome.Available)
            _logger.Debug("LdapChangelogDeltaSource: {Detail}", finding.Detail);
        else
            _logger.Warning("LdapChangelogDeltaSource: The changelog at {ChangelogDn} is {Outcome}: {Detail}",
                LogSanitiser.Sanitise(changelogDn), finding.Outcome, finding.Detail);

        // A changelog that cannot be read makes the plug-in's recording moot, and a second probe of a connection that
        // has just failed would only fail the same way; so the plug-in is asked about only when the changelog is there.
        if (finding.Outcome != LdapDeltaSourceOutcome.Available)
            return Task.FromResult<IReadOnlyList<LdapDeltaSourceFinding>>([finding]);

        var deletedEntryRecording = VerifyDeletedEntryRecording();
        if (deletedEntryRecording.Outcome == LdapDeltaSourceOutcome.Available)
            _logger.Debug("LdapChangelogDeltaSource: {Detail}", deletedEntryRecording.Detail);
        else
            _logger.Warning("LdapChangelogDeltaSource: Deleted-entry recording by the Retro Changelog plug-in at {PluginDn} is {Outcome}: {Detail}",
                LdapConnectorConstants.RETRO_CHANGELOG_PLUGIN_DN, deletedEntryRecording.Outcome, deletedEntryRecording.Detail);

        return Task.FromResult<IReadOnlyList<LdapDeltaSourceFinding>>([finding, deletedEntryRecording]);
    }

    /// <summary>
    /// Reads the Retro Changelog plug-in's entry for <c>nsslapd-log-deleted</c>, to establish whether delete
    /// records carry the deleted entry, without which a deletion cannot be identified (see
    /// <see cref="IdentifyDeletion"/>). Only an entry read in full can prove the switch is off; an entry that is not
    /// found or cannot be read is an unknown, since 389 Directory Server answers noSuchObject for an entry the bound
    /// account may not read, and a directory of another kind has no such entry at all.
    /// </summary>
    private LdapDeltaSourceFinding VerifyDeletedEntryRecording()
    {
        const string pluginDn = LdapConnectorConstants.RETRO_CHANGELOG_PLUGIN_DN;
        var probe = ProbeEntry(pluginDn, LogDeletedAttributes);

        if (probe.Outcome == ChangelogProbeOutcome.Readable)
        {
            var logDeleted = LdapConnectorUtilities.GetEntryAttributeStringValue(probe.Entry!, LdapConnectorConstants.RETRO_CHANGELOG_LOG_DELETED_ATTRIBUTE);
            if (string.Equals(logDeleted, "on", StringComparison.OrdinalIgnoreCase))
            {
                return new LdapDeltaSourceFinding
                {
                    Subject = pluginDn,
                    Outcome = LdapDeltaSourceOutcome.Available,
                    Detail = $"{RetroChangelogPlugin} records deleted entries ({LogDeletedState(logDeleted)})"
                };
            }

            return new LdapDeltaSourceFinding
            {
                Subject = pluginDn,
                Outcome = LdapDeltaSourceOutcome.Unavailable,
                Detail = $"{RetroChangelogPlugin} is not recording deleted entries ({LogDeletedState(logDeleted)})",
                DeltaImportText = DescribeDeletedEntriesNotRecordedForDeltaImport(logDeleted),
                SchemaDiscoveryText = DescribeDeletedEntriesNotRecordedForSchemaDiscovery(logDeleted)
            };
        }

        var detail = probe.Outcome switch
        {
            ChangelogProbeOutcome.NotFound => "the entry was not found, or the account JIM connects as may not read it",
            ChangelogProbeOutcome.Refused => $"the directory refused to read it ({probe.Detail})",
            _ => probe.Detail!
        };

        var text = DescribeDeletedEntryRecordingUndetermined(detail);
        return new LdapDeltaSourceFinding
        {
            Subject = pluginDn,
            Outcome = LdapDeltaSourceOutcome.CouldNotDetermine,
            Detail = detail,
            DeltaImportText = text,
            SchemaDiscoveryText = text
        };
    }

    private enum ChangelogProbeOutcome
    {
        /// <summary>The directory answered the entry.</summary>
        Readable,

        /// <summary>The directory answered noSuchObject, or Success with no entry: nothing there, or nothing this account may read.</summary>
        NotFound,

        /// <summary>The directory refused the read outright, and said why.</summary>
        Refused,

        /// <summary>The connection failed before the directory could answer; nothing is known.</summary>
        CouldNotDetermine
    }

    /// <param name="Entry">The entry the directory answered, when <paramref name="Outcome"/> is <see cref="ChangelogProbeOutcome.Readable"/>.</param>
    private readonly record struct ChangelogProbe(ChangelogProbeOutcome Outcome, string? Detail, SearchResultEntry? Entry = null);

    /// <summary>
    /// A base-scope read of the changelog's entry, with no attributes, through the connection's own timeout: it
    /// answers one entry at most and is what both the readiness check and the watermark capture start with.
    /// </summary>
    private ChangelogProbe ProbeChangelog(string changelogDn) => ProbeEntry(changelogDn, NoAttributes);

    /// <summary>
    /// A base-scope read of one entry for the given attributes, through the connection's own timeout, classifying
    /// the directory's answer the one way every probe here is classified: found, not found, refused, or unknown.
    /// </summary>
    private ChangelogProbe ProbeEntry(string dn, string[] attributes)
    {
        var request = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, attributes);

        try
        {
            var response = (SearchResponse)_executor.SendRequest(request);
            return response.Entries.Count > 0
                ? new ChangelogProbe(ChangelogProbeOutcome.Readable, null, response.Entries[0])
                : new ChangelogProbe(ChangelogProbeOutcome.NotFound, null);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return new ChangelogProbe(ChangelogProbeOutcome.NotFound, null);
        }
        catch (DirectoryOperationException ex)
        {
            return new ChangelogProbe(ChangelogProbeOutcome.Refused, LogSanitiser.Sanitise(ex.Message));
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // noSuchObject
        {
            return new ChangelogProbe(ChangelogProbeOutcome.NotFound, null);
        }
        catch (LdapException ex)
        {
            return new ChangelogProbe(ChangelogProbeOutcome.CouldNotDetermine, $"the directory answered: {LogSanitiser.Sanitise(ex.Message)}");
        }
    }

    #endregion

    #region Texts

    /// <summary>
    /// What stops a Delta Import when the changelog cannot be read: why, what it would cost, and both ways out.
    /// </summary>
    /// <param name="refusal">The directory's own reason when it refused the read; null when it answered that there was nothing there to read.</param>
    internal static string DescribeForDeltaImport(string changelogDn, string? refusal)
    {
        var cause = refusal == null
            ? $"the directory provides no changelog at {changelogDn}, or none the account JIM connects as may read"
            : $"the directory refused to read the changelog at {changelogDn} ({refusal})";

        return $"Changes cannot be detected: {cause}, so additions, updates and deletions since the last import would go unnoticed. " +
            "Run a Full Import, which also detects deletions by absence, or enable the directory's changelog (389 Directory Server: the Retro Changelog plug-in) " +
            "and grant the account read access to it; the LDAP Connector documentation, under Service Account Permissions, gives the detail.";
    }

    /// <summary>
    /// What Schema Discovery warns when the changelog cannot be read, so an administrator learns that Delta Import
    /// is not available while setting the Connected System up rather than from an import that refused to run.
    /// </summary>
    /// <param name="refusal">The directory's own reason when it refused the read; null when it answered that there was nothing there to read.</param>
    internal static string DescribeForSchemaDiscovery(string changelogDn, string? refusal)
    {
        var cause = refusal == null
            ? $"This directory publishes no changelog at {changelogDn} that the account JIM connects as may read"
            : $"The directory refused to read the changelog at {changelogDn} ({refusal})";

        return $"{cause}, so Delta Import is not available; Full Import works as normal and also detects deletions by absence. " +
            "To use Delta Import, enable the directory's changelog (389 Directory Server: the Retro Changelog plug-in) and grant the account read access to it; " +
            "see the LDAP Connector documentation, Service Account Permissions.";
    }

    /// <summary>
    /// The one text for an unknown, in both places: what could not be confirmed, why, and what it would mean.
    /// </summary>
    internal static string DescribeUndetermined(string changelogDn, string detail) =>
        $"JIM could not confirm that the account it connects as can read the changelog at {changelogDn}: {detail}. " +
        "If it cannot, Delta Imports from this directory detect no changes.";

    /// <summary>The plug-in as every text about deleted-entry recording names it.</summary>
    private const string RetroChangelogPlugin = "the Retro Changelog plug-in (" + LdapConnectorConstants.RETRO_CHANGELOG_PLUGIN_DN + ")";

    /// <summary>How the plug-in's switch stands, for the texts: what it is set to, or that it is not set at all.</summary>
    private static string LogDeletedState(string? value) =>
        value == null
            ? $"{LdapConnectorConstants.RETRO_CHANGELOG_LOG_DELETED_ATTRIBUTE} is not set"
            : $"{LdapConnectorConstants.RETRO_CHANGELOG_LOG_DELETED_ATTRIBUTE} is {value}";

    /// <summary>
    /// What stops a Delta Import when the plug-in is provably not recording deleted entries: what it would cost, and
    /// both ways out (turn the switch on and restart, or a Full Import).
    /// </summary>
    internal static string DescribeDeletedEntriesNotRecordedForDeltaImport(string? logDeleted) =>
        $"Deletions cannot be detected: {RetroChangelogPlugin} is not recording deleted entries ({LogDeletedState(logDeleted)}), " +
        "so objects deleted in the directory would stay in JIM. " +
        $"Set {LdapConnectorConstants.RETRO_CHANGELOG_LOG_DELETED_ATTRIBUTE} to on on that entry and restart the directory, or run a Full Import, which detects deletions by absence; " +
        "the LDAP Connector documentation, under Service Account Permissions, gives the detail.";

    /// <summary>
    /// What Schema Discovery warns when the plug-in is not recording deleted entries, so an administrator learns
    /// that Delta Import is not available while setting the Connected System up.
    /// </summary>
    internal static string DescribeDeletedEntriesNotRecordedForSchemaDiscovery(string? logDeleted) =>
        $"The Retro Changelog plug-in ({LdapConnectorConstants.RETRO_CHANGELOG_PLUGIN_DN}) is not recording deleted entries ({LogDeletedState(logDeleted)}), " +
        "so Delta Import is not available until it does; Full Import works as normal and also detects deletions by absence. " +
        $"To use Delta Import, set {LdapConnectorConstants.RETRO_CHANGELOG_LOG_DELETED_ATTRIBUTE} to on on that entry and restart the directory; " +
        "see the LDAP Connector documentation, Service Account Permissions.";

    /// <summary>
    /// The one text for not knowing whether the plug-in records deleted entries, in both places: what could not be
    /// confirmed, why, what to grant, and what it would mean.
    /// </summary>
    internal static string DescribeDeletedEntryRecordingUndetermined(string detail) =>
        $"JIM could not confirm that {RetroChangelogPlugin} records deleted entries: {detail}. " +
        "Grant the account it connects as read access to that entry (the LDAP Connector documentation, under Service Account Permissions, asks for read on cn=config for 389 Directory Server); " +
        "if the plug-in is not recording deleted entries, Delta Imports would miss deletions.";

    /// <summary>
    /// The warning a Delta Import carries for a deletion it read but could not stage, because the changelog record
    /// carried no deleted entry to identify it by: what happened, what it costs, and both ways out.
    /// </summary>
    internal static string DescribeUnidentifiedDeletion(string deletedDn) =>
        $"The deletion of {deletedDn} could not be identified: the changelog record carries no deleted entry, so the object stays in JIM. " +
        $"On 389 Directory Server, set {LdapConnectorConstants.RETRO_CHANGELOG_LOG_DELETED_ATTRIBUTE} to on on {RetroChangelogPlugin} and restart the directory, " +
        "so that delete records carry the deleted entry; or run a Full Import, which detects deletions by absence.";

    #endregion

    #region Reading changes

    /// <inheritdoc />
    public bool HasBaseline(LdapConnectorRootDse previous) => previous.LastChangeNumber.HasValue;

    /// <summary>
    /// Reads every changelog entry numbered after the watermark and turns each into an import object: a deletion
    /// directly, anything else by fetching the target's current state. The changelog is read in one search, and
    /// when the directory stops that at its size limit the search resumes from the highest change number seen plus
    /// one; the change number is an exact cursor, so nothing is missed or read twice. A changelog that cannot be
    /// read fails the run: a Delta Import that quietly imported nothing would leave JIM believing nothing changed.
    /// </summary>
    /// <exception cref="CannotPerformDeltaImportException">The changelog was not found, or the directory refused to read it, or it answered nothing within its size limit.</exception>
    public async Task ReadChangesAsync(LdapDeltaReadContext context, ConnectedSystemImportResult result, CancellationToken cancellationToken)
    {
        // The shell has already established HasBaseline before calling.
        var previousChangeNumber = context.PreviousRootDse.LastChangeNumber!.Value;
        var changelogDn = ChangelogDnFor(context.CurrentRootDse ?? context.PreviousRootDse);

        await context.Host.EnterPhaseAsync(LdapConnectorPhases.QueryChanges, $"Querying changelog since change number {previousChangeNumber:N0}...");
        _logger.Debug("LdapChangelogDeltaSource: Querying {ChangelogDn} for changes since changeNumber {PreviousChange}", LogSanitiser.Sanitise(changelogDn), previousChangeNumber);

        var readBefore = result.ImportObjects.Count;
        var skippedOutOfScope = 0;
        var cursor = previousChangeNumber + 1;

        while (!cancellationToken.IsCancellationRequested)
        {
            var (entries, limited) = SearchChangelog(changelogDn, cursor, context.SearchTimeout);
            _logger.Debug("LdapChangelogDeltaSource: Found {Count} changelog entries from change number {Cursor}", entries.Count, cursor);

            var highestSeen = ProcessEntries(entries, context, result, ref skippedOutOfScope, cancellationToken);
            if (!limited)
                break;

            if (highestSeen == null)
            {
                throw new CannotPerformDeltaImportException(
                    $"Changes cannot be detected: the directory answered no entries within its size limit when reading the changelog at {changelogDn} from change number {cursor}, " +
                    "so the changes since the last import cannot be read. Raise the directory's size limit for the account JIM connects as, or run a Full Import, which also detects deletions by absence.");
            }

            _logger.Information("LdapChangelogDeltaSource: The directory stopped at its size limit after {Count} changelog entries; continuing from change number {Next}",
                entries.Count, highestSeen.Value + 1);
            cursor = highestSeen.Value + 1;
        }

        if (cancellationToken.IsCancellationRequested)
            _logger.Debug("LdapChangelogDeltaSource: Cancellation requested. Stopping");

        if (skippedOutOfScope > 0)
            _logger.Information("LdapChangelogDeltaSource: Skipped {SkippedCount} changelog entries for objects outside the selected Container scope", skippedOutOfScope);

        await context.Host.ReportObjectsReadAsync(result.ImportObjects.Count - readBefore);
    }

    /// <summary>
    /// One search of the changelog from a change number. Returns what the directory answered and whether it
    /// stopped at its size limit before answering everything; throws when it cannot be read at all.
    /// </summary>
    private (IReadOnlyList<SearchResultEntry> Entries, bool Limited) SearchChangelog(string changelogDn, long fromChangeNumber, TimeSpan searchTimeout)
    {
        // LDAP (RFC 4515) defines ">=" but not ">", which is why the cursor is the watermark plus one.
        var request = new SearchRequest(changelogDn, $"({ChangeNumberAttribute}>={fromChangeNumber})", SearchScope.OneLevel, ChangeAttributes);

        try
        {
            return (((SearchResponse)_executor.SendRequest(request, searchTimeout)).Entries.Cast<SearchResultEntry>().ToList(), false);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            throw NotFound(changelogDn);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded)
        {
            return (PartialEntries(ex), true);
        }
        catch (DirectoryOperationException ex)
        {
            var detail = LogSanitiser.Sanitise(ex.Message);
            _logger.Warning("LdapChangelogDeltaSource: The directory refused to read the changelog at {ChangelogDn}: {Message}", LogSanitiser.Sanitise(changelogDn), detail);
            throw new CannotPerformDeltaImportException(DescribeForDeltaImport(changelogDn, detail));
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // noSuchObject
        {
            throw NotFound(changelogDn);
        }
        // Any other LdapException is a connection-level fault and propagates: a failed connection is an error, not
        // a silent success.
    }

    private CannotPerformDeltaImportException NotFound(string changelogDn)
    {
        _logger.Warning("LdapChangelogDeltaSource: No changelog was found at {ChangelogDn}, or none the account JIM connects as may read", LogSanitiser.Sanitise(changelogDn));
        return new CannotPerformDeltaImportException(DescribeForDeltaImport(changelogDn, refusal: null));
    }

    /// <summary>
    /// Turns changelog entries into import objects, and returns the highest change number among them (null when
    /// none carried one), which is where a size-limited search resumes from.
    /// </summary>
    private long? ProcessEntries(IReadOnlyList<SearchResultEntry> entries, LdapDeltaReadContext context, ConnectedSystemImportResult result, ref int skippedOutOfScope, CancellationToken cancellationToken)
    {
        long? highestSeen = null;

        foreach (var changeEntry in entries)
        {
            if (cancellationToken.IsCancellationRequested)
                return highestSeen;

            if (TryReadChangeNumber(changeEntry, out var changeNumber) && (highestSeen == null || changeNumber > highestSeen))
                highestSeen = changeNumber;

            var changeType = LdapConnectorUtilities.GetEntryAttributeStringValue(changeEntry, "changeType")?.ToLowerInvariant();
            var targetDn = LdapConnectorUtilities.GetEntryAttributeStringValue(changeEntry, "targetDN");

            if (string.IsNullOrEmpty(targetDn))
                continue;

            // The changelog states the kind of change, so it is used directly; an unknown kind falls back to NotSet
            // so JIM decides from whether the object is already staged.
            var objectChangeType = changeType switch
            {
                "add" => ObjectChangeType.Added,
                "modify" => ObjectChangeType.Updated,
                "delete" => ObjectChangeType.Deleted,
                "modrdn" or "moddn" => ObjectChangeType.Updated,
                _ => ObjectChangeType.NotSet
            };

            // On a rename, targetDN is where the object was; where it is now is what gets fetched, and what is judged
            // against scope: a move out of the selected Containers is not an update, a move into them is.
            var isRename = changeType is "modrdn" or "moddn";
            var subjectDn = isRename ? RenamedDn(changeEntry, targetDn) : targetDn;

            // Filter by Container scope: the changelog is directory-wide, so it reports changes to objects this
            // Connected System does not import. A full import only reads the selected Containers, each to its own
            // scope; without this, a delta import would bring in objects a full import never would.
            if (!LdapConnectorUtilities.IsDnInScope(subjectDn, context.ScopeDecidingContainers))
            {
                skippedOutOfScope++;
                continue;
            }

            if (objectChangeType == ObjectChangeType.Deleted)
            {
                var deleted = IdentifyDeletion(changeEntry, targetDn, context);
                if (deleted != null)
                    result.ImportObjects.Add(deleted);
            }
            else
            {
                // For adds, modifies and renames, the changelog names the object; its current state is fetched by DN.
                var currentObject = context.Host.GetObjectByDn(subjectDn, objectChangeType);
                if (currentObject != null)
                    result.ImportObjects.Add(currentObject);
            }
        }

        return highestSeen;
    }

    /// <summary>
    /// Where a renamed object now is: its new RDN under the new superior when the record names one, otherwise under
    /// the parent it had. The DN the record names when it carries no new RDN, or that DN cannot be parsed, since
    /// there is then nothing better to fetch.
    /// </summary>
    private string RenamedDn(SearchResultEntry changeEntry, string targetDn)
    {
        var newRdn = LdapConnectorUtilities.GetEntryAttributeStringValue(changeEntry, NewRdnAttribute);
        if (string.IsNullOrEmpty(newRdn))
        {
            _logger.Debug("LdapChangelogDeltaSource: The rename record {EntryDn} carries no newRdn; fetching the DN it names, {TargetDn}",
                LogSanitiser.Sanitise(changeEntry.DistinguishedName), LogSanitiser.Sanitise(targetDn));
            return targetDn;
        }

        var newSuperior = LdapConnectorUtilities.GetEntryAttributeStringValue(changeEntry, NewSuperiorAttribute);
        if (string.IsNullOrEmpty(newSuperior))
        {
            var (rdn, parentDn) = LdapConnectorUtilities.ParseDistinguishedName(targetDn);
            if (rdn == null)
            {
                _logger.Warning("LdapChangelogDeltaSource: The rename record {EntryDn} names a targetDN that does not parse as a DN; fetching it as named",
                    LogSanitiser.Sanitise(changeEntry.DistinguishedName));
                return targetDn;
            }

            // A single-RDN target has no parent: the new RDN is the whole new DN.
            newSuperior = parentDn;
        }

        return string.IsNullOrEmpty(newSuperior) ? newRdn : $"{newRdn},{newSuperior}";
    }

    /// <summary>
    /// The deletion as an import object, identified from the deleted entry the record carries in <c>changes</c>.
    /// Null when there is nothing to identify it by: a record with no deleted entry in it is noted on the run, with
    /// the remedy, rather than staged as a delete that names nothing, which the import would discard unseen; a record
    /// whose entry is of a type this Connected System does not import is passed over, as a live one would be.
    /// </summary>
    private ConnectedSystemImportObject? IdentifyDeletion(SearchResultEntry changeEntry, string targetDn, LdapDeltaReadContext context)
    {
        var changes = ReadChanges(changeEntry);
        var identity = string.IsNullOrWhiteSpace(changes) ? null : LdapDeletedEntryIdentity.Parse(changes.Split('\n'));

        if (identity == null || identity.IsEmpty)
        {
            _logger.Warning("LdapChangelogDeltaSource: The changelog record {EntryDn} for the deletion of {TargetDn} carries no deleted entry, so the deletion cannot be identified and is not staged. " +
                "On 389 Directory Server, {Attribute} on {PluginDn} must be on for delete records to carry the deleted entry",
                LogSanitiser.Sanitise(changeEntry.DistinguishedName), LogSanitiser.Sanitise(targetDn),
                LdapConnectorConstants.RETRO_CHANGELOG_LOG_DELETED_ATTRIBUTE, LdapConnectorConstants.RETRO_CHANGELOG_PLUGIN_DN);
            context.Notes.Record(targetDn, DescribeUnidentifiedDeletion(targetDn));
            return null;
        }

        return identity.ToImportObject(targetDn, context.ObjectTypes, _logger);
    }

    /// <summary>
    /// The record's <c>changes</c> as text. The attribute has Octet String syntax, and System.DirectoryServices.Protocols
    /// hands a search-result value back as bytes rather than a string when it is not valid UTF-8 throughout, so both
    /// shapes are read; null when the record carries no single value.
    /// </summary>
    private static string? ReadChanges(SearchResultEntry changeEntry)
    {
        if (!changeEntry.Attributes.Contains(ChangesAttribute) || changeEntry.Attributes[ChangesAttribute].Count != 1)
            return null;

        return changeEntry.Attributes[ChangesAttribute][0] switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => null
        };
    }

    #endregion
}
