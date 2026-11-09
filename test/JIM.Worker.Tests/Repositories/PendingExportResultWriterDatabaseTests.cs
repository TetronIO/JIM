// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Exchange;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage of the export-result writer (#2851,
/// <see cref="JIM.Data.Repositories.ISyncRepository.UpdatePendingExportResultsAsync"/>): the one set-based statement that
/// marks the changes a batch sent, the statement that writes the changes it revised, and the Pending Export rows. Its
/// result must be exactly what writing every column of every change back (<c>UpdatePendingExportsAsync</c>) leaves, for
/// every change row of a batch mixing everything export decides on. The in-memory provider cannot see any of it.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent. <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportResultWriterDatabaseTests
{
    private const string SystemName = "Panoply AD";
    private string _connectionString = null!;

    private JimDbContext NewContext(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            // The Worker tracks by default (src/JIM.Worker/Program.cs), so the rows a batch loads are tracked.
            .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        if (interceptor != null)
            builder.AddInterceptors(interceptor);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL export-result writer tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    /// <summary>
    /// The sent changes are marked sent, their attempts counted on from what is stored; the revised ones get their value
    /// and holds; the Pending Export rows get every export-result column; and nothing else is written, not even a column
    /// that differs in memory.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportResultsAsync_FullyPopulatedBatch_WritesTheSentMarkTheRevisionsAndTheExportRowsOnlyAsync()
    {
        var s = await SeedAsync();
        var export = NewExport(s, PendingExportStatus.Executing);
        var sent = NewChange(s.DisplayName, "Ada", exportAttemptCount: 2, status: PendingExportAttributeChangeStatus.ExportedNotConfirmed);
        sent.LastImportedValue = "Ada (imported)";
        sent.ResolvedReferenceCsoId = s.CsoIds[1];
        var revised = NewChange(s.AccountExpires, null, awaitsInitialPassword: true);
        var untouched = NewChange(s.Manager, null, status: PendingExportAttributeChangeStatus.Pending);
        untouched.UnresolvedReferenceValue = Guid.NewGuid().ToString();
        export.AttributeValueChanges.AddRange([sent, revised, untouched]);
        await CreateAsync(export);

        var sentAt = new DateTime(2026, 11, 9, 10, 30, 15, DateTimeKind.Utc).AddTicks(1234560);
        await using (var ctx = NewContext())
        {
            var loaded = (await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync([export.Id])).Single();
            var loadedSent = loaded.AttributeValueChanges.Single(c => c.Id == sent.Id);
            var loadedRevised = loaded.AttributeValueChanges.Single(c => c.Id == revised.Id);
            var loadedUntouched = loaded.AttributeValueChanges.Single(c => c.Id == untouched.Id);

            loadedSent.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
            loadedSent.ExportAttemptCount = 3;
            loadedSent.LastExportedAt = sentAt;
            // Never written by the export-result writer: export does not change them once a batch is loaded.
            loadedSent.LastImportedValue = "changed in memory";
            loadedSent.ResolvedReferenceCsoId = null;
            loadedUntouched.UnresolvedReferenceValue = null;
            loadedUntouched.StringValue = "changed in memory";
            loadedRevised.StringValue = "9223372036854775807";
            loadedRevised.AwaitsInitialPassword = false;
            loadedRevised.AwaitsExchange = true;
            loaded.Status = PendingExportStatus.Exported;
            loaded.ErrorCount = 0;
            loaded.LastAttemptedAt = sentAt;
            loaded.LastErrorMessage = null;
            loaded.HasUnresolvedReferences = true;
            loaded.ChangeType = PendingExportChangeType.Update;

            await new PostgresDataRepository(ctx).Sync.UpdatePendingExportResultsAsync(new PendingExportResultWrite
            {
                PendingExports = [loaded], SentChanges = [loadedSent], SentAt = sentAt, RevisedChanges = [loadedRevised]
            });
        }

        await using var verify = NewContext();
        var stored = await verify.PendingExports.AsNoTracking().Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.Id == export.Id);
        var storedSent = stored.AttributeValueChanges.Single(c => c.Id == sent.Id);
        var storedRevised = stored.AttributeValueChanges.Single(c => c.Id == revised.Id);
        var storedUntouched = stored.AttributeValueChanges.Single(c => c.Id == untouched.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedSent.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(storedSent.ExportAttemptCount, Is.EqualTo(3), "counted on from the stored 2");
            Assert.That(storedSent.LastExportedAt, Is.EqualTo(sentAt.AddTicks(-(sentAt.Ticks % TimeSpan.TicksPerMicrosecond))));
            Assert.That(storedSent.StringValue, Is.EqualTo("Ada"));
            Assert.That(storedSent.LastImportedValue, Is.EqualTo("Ada (imported)"), "LastImportedValue is never written");
            Assert.That(storedSent.ResolvedReferenceCsoId, Is.EqualTo(s.CsoIds[1]), "ResolvedReferenceCsoId is never written");
            Assert.That(storedSent.AwaitsInitialPassword, Is.False);

            Assert.That(storedRevised.StringValue, Is.EqualTo("9223372036854775807"));
            Assert.That(storedRevised.AwaitsInitialPassword, Is.False);
            Assert.That(storedRevised.AwaitsExchange, Is.True);
            Assert.That(storedRevised.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending), "a revised change not sent keeps its status");
            Assert.That(storedRevised.ExportAttemptCount, Is.Zero);
            Assert.That(storedRevised.LastExportedAt, Is.Null);

            Assert.That(storedUntouched.UnresolvedReferenceValue, Is.EqualTo(untouched.UnresolvedReferenceValue), "UnresolvedReferenceValue is never written");
            Assert.That(storedUntouched.StringValue, Is.Null, "a change neither sent nor revised is not written");

            Assert.That(stored.Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(stored.HasUnresolvedReferences, Is.True);
            Assert.That(stored.LastAttemptedAt, Is.EqualTo(sentAt.AddTicks(-(sentAt.Ticks % TimeSpan.TicksPerMicrosecond))));
        }
    }

    /// <summary>
    /// The writer runs on the Worker's long-lived tracking context, which still tracks the rows the batch loaded: a later
    /// save on it must neither fail on them nor write their old values back.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportResultsAsync_OnAContextTrackingTheRows_LeavesALaterSaveNothingToWriteBackAsync()
    {
        var s = await SeedAsync();
        var export = NewExport(s, PendingExportStatus.Executing);
        var change = NewChange(s.DisplayName, "Ada", awaitsInitialPassword: true);
        export.AttributeValueChanges.Add(change);
        await CreateAsync(export);

        await using (var ctx = NewContext())
        {
            var tracked = await ctx.PendingExports.Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.Id == export.Id);
            Assert.That(ctx.Entry(tracked).State, Is.EqualTo(EntityState.Unchanged), "precondition: the batch's rows are tracked");
            var trackedChange = tracked.AttributeValueChanges.Single();
            var sentAt = DateTime.UtcNow;
            trackedChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
            trackedChange.ExportAttemptCount = 1;
            trackedChange.LastExportedAt = sentAt;
            trackedChange.AwaitsInitialPassword = false;
            tracked.Status = PendingExportStatus.Exported;

            await new PostgresDataRepository(ctx).Sync.UpdatePendingExportResultsAsync(new PendingExportResultWrite
            {
                PendingExports = [tracked], SentChanges = [trackedChange], SentAt = sentAt, RevisedChanges = [trackedChange]
            });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Entry(tracked).State, Is.EqualTo(EntityState.Detached));
                Assert.That(ctx.Entry(trackedChange).State, Is.EqualTo(EntityState.Detached));
            }
            Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing);
        }

        await using var verify = NewContext();
        var stored = await verify.PendingExportAttributeValueChanges.AsNoTracking().SingleAsync(c => c.Id == change.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(stored.ExportAttemptCount, Is.EqualTo(1));
            Assert.That(stored.AwaitsInitialPassword, Is.False);
        }
    }

    /// <summary>
    /// As the full writer orders them (#2173): a delivery waits while an export to the account is in flight, so a change
    /// held until an Initial Password is delivered must be written as held before its Pending Export leaves Executing.
    /// The two commit together, so the order is read inside the write's own transaction, just before the Pending Export's
    /// row is written.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportResultsAsync_WritesTheHeldChangeBeforeThePendingExportLeavesExecutingAsync()
    {
        var s = await SeedAsync();
        var export = NewExport(s, PendingExportStatus.Executing);
        export.LastAttemptedAt = DateTime.UtcNow;
        var change = NewChange(s.DisplayName, "Ada");
        export.AttributeValueChanges.Add(change);
        await CreateAsync(export);

        var heldWhenTheExportLeftExecuting = new List<bool>();
        var probe = new BeforeStatementInterceptor("UPDATE \"PendingExports\"", async command =>
        {
            await using var read = new NpgsqlCommand(
                """SELECT "AwaitsInitialPassword" AND "Status" = @sent FROM "PendingExportAttributeValueChanges" WHERE "Id" = @id""",
                (NpgsqlConnection)command.Connection!, (NpgsqlTransaction?)command.Transaction);
            read.Parameters.AddWithValue("sent", (int)PendingExportAttributeChangeStatus.ExportedPendingConfirmation);
            read.Parameters.AddWithValue("id", change.Id);
            heldWhenTheExportLeftExecuting.Add((bool)(await read.ExecuteScalarAsync())!);
        });

        await using (var ctx = NewContext(probe))
        {
            var loaded = (await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync([export.Id])).Single();
            var loadedChange = loaded.AttributeValueChanges.Single();
            var sentAt = DateTime.UtcNow;
            loadedChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
            loadedChange.ExportAttemptCount = 1;
            loadedChange.LastExportedAt = sentAt;
            loadedChange.AwaitsInitialPassword = true;
            loaded.Status = PendingExportStatus.Exported;
            await new PostgresDataRepository(ctx).Sync.UpdatePendingExportResultsAsync(new PendingExportResultWrite
            {
                PendingExports = [loaded], SentChanges = [loadedChange], SentAt = sentAt, RevisedChanges = [loadedChange]
            });
        }

        Assert.That(heldWhenTheExportLeftExecuting, Is.Not.Empty.And.All.True, "the hold is readable before the export stops being in flight");
    }

    /// <summary>
    /// A sent change whose row is gone cannot be marked, and the rest of the batch's results are recorded, as writing
    /// every column back records them: failing the write would record changes already written to the Connected System
    /// as a failed attempt, and send them again.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportResultsAsync_SentChangeWhoseRowIsGone_RecordsTheRestAsWritingEveryColumnBackDoesAsync()
    {
        var s = await SeedAsync();
        var export = NewExport(s, PendingExportStatus.Executing);
        var kept = NewChange(s.DisplayName, "Ada");
        var gone = NewChange(s.AccountExpires, "0");
        export.AttributeValueChanges.AddRange([kept, gone]);
        await CreateAsync(export);

        await using (var ctx = NewContext())
        {
            var loaded = (await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync([export.Id])).Single();
            await ctx.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "PendingExportAttributeValueChanges" WHERE "Id" = {gone.Id}""");
            var sentAt = DateTime.UtcNow;
            foreach (var change in loaded.AttributeValueChanges)
            {
                change.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
                change.ExportAttemptCount = 1;
                change.LastExportedAt = sentAt;
            }
            loaded.Status = PendingExportStatus.Exported;

            await new PostgresDataRepository(ctx).Sync.UpdatePendingExportResultsAsync(new PendingExportResultWrite
            {
                PendingExports = [loaded], SentChanges = loaded.AttributeValueChanges, SentAt = sentAt, RevisedChanges = []
            });
        }

        await using var verify = NewContext();
        var stored = await verify.PendingExports.AsNoTracking().Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.Id == export.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(stored.AttributeValueChanges.Single().Id, Is.EqualTo(kept.Id));
            Assert.That(stored.AttributeValueChanges.Single().Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        }
    }

    /// <summary>
    /// A change handed over as sent must hold the sent mark, or what is stored would not be what the batch holds: the write
    /// is refused before anything is written.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportResultsAsync_SentChangeWithoutTheSentMark_IsRefusedBeforeAnythingIsWrittenAsync()
    {
        var s = await SeedAsync();
        var export = NewExport(s, PendingExportStatus.Executing);
        var change = NewChange(s.DisplayName, "Ada");
        export.AttributeValueChanges.Add(change);
        await CreateAsync(export);

        await using (var ctx = NewContext())
        {
            var loaded = (await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync([export.Id])).Single();
            loaded.Status = PendingExportStatus.Exported;
            Assert.That(async () => await new PostgresDataRepository(ctx).Sync.UpdatePendingExportResultsAsync(new PendingExportResultWrite
            {
                PendingExports = [loaded], SentChanges = loaded.AttributeValueChanges, SentAt = DateTime.UtcNow, RevisedChanges = []
            }), Throws.InvalidOperationException.With.Message.Contains(change.Id.ToString()));
        }

        await using var verify = NewContext();
        Assert.That((await verify.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == export.Id)).Status, Is.EqualTo(PendingExportStatus.Executing));
    }

    /// <summary>
    /// The whole point (#2851): for a batch mixing everything export decides on, every change row and every Pending Export
    /// row persisted by the export-result writer is identical to what writing every column of every change back leaves.
    /// The batch is marked by export's own decision (what is writable now, which statuses are sent, the Initial Password
    /// and Exchange holds), on rows loaded as a batch loads them; it is written in two parts, as a batch writes the exports
    /// held until their Initial Passwords are staged after the rest; and the two writers' results are compared after each
    /// part, each written in a transaction rolled back so both start from the same rows.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportResultsAsync_MixedBatch_PersistsEveryRowAsWritingEveryColumnBackDoesAsync()
    {
        var s = await SeedAsync();

        // Written in part: sent Pending and ExportedNotConfirmed changes, one already awaiting confirmation, a reference
        // still unresolved, one resolved from an empty-string marker, an Initial Password hold cleared by this write, and a
        // value the Connector substitutes as it writes (a protected attribute it cannot clear).
        var partial = NewExport(s, PendingExportStatus.Executing, csoIndex: 0, hasUnresolvedReferences: true);
        var pending = NewChange(s.DisplayName, "Ada");
        pending.LastImportedValue = "Ada (as imported)";
        var notConfirmed = NewChange(s.DisplayName, "Ada Lovelace", exportAttemptCount: 3, status: PendingExportAttributeChangeStatus.ExportedNotConfirmed);
        notConfirmed.LastExportedAt = DateTime.UtcNow.AddDays(-1);
        var awaitingConfirmation = NewChange(s.DisplayName, "Countess", exportAttemptCount: 1,
            status: PendingExportAttributeChangeStatus.ExportedPendingConfirmation, awaitsInitialPassword: true);
        awaitingConfirmation.AwaitsExchange = true;
        awaitingConfirmation.LastExportedAt = DateTime.UtcNow.AddHours(-2);
        var unresolved = NewChange(s.Manager, null);
        unresolved.UnresolvedReferenceValue = Guid.NewGuid().ToString();
        var emptyMarker = NewChange(s.Manager, "CN=Charles Babbage,OU=People,DC=panoply,DC=local");
        emptyMarker.UnresolvedReferenceValue = "";
        emptyMarker.ResolvedReferenceCsoId = s.CsoIds[3];
        var holdCleared = NewChange(s.DisplayName, "A. Lovelace", awaitsInitialPassword: true);
        var substituted = NewChange(s.AccountExpires, null);
        partial.AttributeValueChanges.AddRange([pending, notConfirmed, awaitingConfirmation, unresolved, emptyMarker, holdCleared, substituted]);

        // A Create whose rule asks for an Initial Password, held until it is staged: written second, with a change the
        // Connector wrote with an interim value until the password arrives.
        var held = NewExport(s, PendingExportStatus.Executing, csoIndex: 1, changeType: PendingExportChangeType.Create);
        var heldName = NewChange(s.DisplayName, "Grace");
        var heldInterim = NewChange(s.AccountExpires, "0");
        var heldRetry = NewChange(s.DisplayName, "Grace Hopper", exportAttemptCount: 1, status: PendingExportAttributeChangeStatus.ExportedNotConfirmed);
        held.AttributeValueChanges.AddRange([heldName, heldInterim, heldRetry]);

        // A failed attempt: none of its changes is marked.
        var failed = NewExport(s, PendingExportStatus.Executing, csoIndex: 2);
        var failedChange = NewChange(s.DisplayName, "Edsger", awaitsInitialPassword: true);
        failed.AttributeValueChanges.Add(failedChange);

        // Written with Exchange recipient state: one change the Exchange queue now owns, one it no longer does.
        var exchange = NewExport(s, PendingExportStatus.Executing, csoIndex: 3);
        var recipientType = NewChange(s.ExchangeRecipientType, "UserMailbox");
        var alias = NewChange(s.ExchangeAlias, "ada");
        alias.AwaitsExchange = true;
        exchange.AttributeValueChanges.AddRange([recipientType, alias]);

        await CreateAsync(partial, held, failed, exchange);
        var exportIds = new[] { partial.Id, held.Id, failed.Id, exchange.Id };

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Sync;
        var batch = (await repository.GetPendingExportsByIdsAsync(exportIds)).ToDictionary(pe => pe.Id);
        PendingExportAttributeValueChange Loaded(PendingExport export, PendingExportAttributeValueChange change) =>
            batch[export.Id].AttributeValueChanges.Single(c => c.Id == change.Id);

        // As MarkBatchAsExecutingAsync takes it, before the Connector has the batch.
        var before = PendingExportAttributeValueChangeState.Snapshot(batch.Values);

        // The Connector substitutes the protected attribute's default, as the LDAP Connector does for accountExpires.
        Loaded(partial, substituted).StringValue = "9223372036854775807";

        // ProcessBatchSuccessAsync's decision, made with its own helpers.
        var sentAt = DateTime.UtcNow;
        var sent = new HashSet<PendingExportAttributeValueChange>(ReferenceEqualityComparer.Instance);
        void Written(PendingExport export, ConnectedSystemExportResult result, ExchangeRecipientDecision? decision)
        {
            var writtenChanges = ExportExecutionServer.WritableChanges(export);
            ExportExecutionServer.UpdateAttributeChangeStatusesAfterExport(writtenChanges, sentAt, sent);
            ExportExecutionServer.MarkChangesAwaitingInitialPassword(export, writtenChanges, result, SystemName);
            ExportExecutionServer.MarkChangesAwaitingExchange(writtenChanges, decision);
            export.Status = export.AttributeValueChanges.Any(ExportExecutionServer.IsUnresolvedReference) ? PendingExportStatus.Pending : PendingExportStatus.Exported;
            export.LastAttemptedAt = sentAt;
            export.ClearFailure();
        }
        Written(batch[partial.Id], ConnectedSystemExportResult.Succeeded(), decision: null);
        batch[partial.Id].ChangeType = PendingExportChangeType.Update;
        batch[held.Id].InitialPasswordFollows = true;
        Written(batch[held.Id], ConnectedSystemExportResult.Succeeded(Guid.NewGuid().ToString(), null, ["accountExpires"]), decision: null);
        Written(batch[exchange.Id], ConnectedSystemExportResult.Succeeded(),
            new ExchangeRecipientDecision { Kind = ExchangeRecipientDecisionKind.Stage });
        PendingExportRetryState.Of(batch[failed.Id]).AfterFailedAttempt("The directory went away", null, sentAt).ApplyTo(batch[failed.Id]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Select(c => c.Id), Is.EquivalentTo(new[]
            {
                pending.Id, notConfirmed.Id, emptyMarker.Id, holdCleared.Id, substituted.Id, heldName.Id, heldInterim.Id, heldRetry.Id,
                recipientType.Id, alias.Id
            }), "precondition: what export sends, an empty-string marker included");
            Assert.That(Loaded(held, heldInterim).AwaitsInitialPassword, Is.True, "precondition: the interim value is held");
            Assert.That(Loaded(exchange, recipientType).AwaitsExchange, Is.True, "precondition: the Exchange queue owns the recipient type");
            Assert.That(Loaded(exchange, alias).AwaitsExchange, Is.True, "precondition: and still the alias");
        }

        List<PendingExport>[] parts = [[batch[partial.Id], batch[failed.Id], batch[exchange.Id]], [batch[held.Id]]];

        async Task<List<List<string>>> WriteAsync(Func<List<PendingExport>, Task> write)
        {
            var states = new List<List<string>>();
            await using var transaction = await ctx.Database.BeginTransactionAsync();
            foreach (var part in parts)
            {
                await write(part);
                states.Add(await ReadRowsAsync(ctx, exportIds));
            }
            await transaction.RollbackAsync();
            return states;
        }

        var everyColumn = await WriteAsync(part => repository.UpdatePendingExportsAsync(part));
        var exportResults = await WriteAsync(part => repository.UpdatePendingExportResultsAsync(
            ExportExecutionServer.ExportResultsFor(part, sent, sentAt, before)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exportResults[0], Is.EqualTo(everyColumn[0]), "after the batch's first write");
            Assert.That(exportResults[1], Is.EqualTo(everyColumn[1]), "after the held exports are written");
            Assert.That(everyColumn[0], Is.Not.EqualTo(everyColumn[1]), "precondition: the second write changes rows the first left alone");
        }
    }

    #region Harness

    private sealed record Seeded(int SystemId, List<Guid> CsoIds, int DisplayName, int Manager, int AccountExpires, int ExchangeRecipientType, int ExchangeAlias);

    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = SystemName, ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType dataType, bool connectorDefined = false)
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = AttributePlurality.SingleValued,
                Selected = true, IsConnectorDefined = connectorDefined
            };
            type.Attributes.Add(attribute);
            return attribute;
        }
        var displayName = Attribute("displayName", AttributeDataType.Text);
        var manager = Attribute("manager", AttributeDataType.Reference);
        var accountExpires = Attribute("accountExpires", AttributeDataType.Text);
        var recipientType = Attribute(ExchangeRecipientAttributes.RecipientType, AttributeDataType.Text, connectorDefined: true);
        var alias = Attribute(ExchangeRecipientAttributes.Alias, AttributeDataType.Text, connectorDefined: true);
        var csos = Enumerable.Range(0, 4)
            .Select(_ => new ConnectedSystemObject { Type = type, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal })
            .ToList();
        seed.AddRange(connectorDefinition, system, type);
        seed.AddRange(csos);
        await seed.SaveChangesAsync();
        return new Seeded(system.Id, csos.Select(c => c.Id).ToList(), displayName.Id, manager.Id, accountExpires.Id, recipientType.Id, alias.Id);
    }

    private async Task CreateAsync(params PendingExport[] exports)
    {
        await using var write = NewContext();
        await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync(exports);
    }

    private static PendingExport NewExport(Seeded s, PendingExportStatus status, int csoIndex = 0, bool hasUnresolvedReferences = false,
        PendingExportChangeType changeType = PendingExportChangeType.Update) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = s.SystemId,
        ConnectedSystemObjectId = s.CsoIds[csoIndex],
        ChangeType = changeType,
        Status = status,
        CreatedAt = DateTime.UtcNow.AddMinutes(-csoIndex),
        LastAttemptedAt = DateTime.UtcNow.AddMinutes(-1),
        HasUnresolvedReferences = hasUnresolvedReferences,
        ErrorCount = 1,
        FailingSince = DateTime.UtcNow.AddDays(-1),
        LastErrorMessage = "An earlier attempt failed"
    };

    private static PendingExportAttributeValueChange NewChange(int attributeId, string? value, int exportAttemptCount = 0,
        PendingExportAttributeChangeStatus status = PendingExportAttributeChangeStatus.Pending, bool awaitsInitialPassword = false) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attributeId,
        ChangeType = PendingExportAttributeChangeType.Update,
        StringValue = value,
        Status = status,
        ExportAttemptCount = exportAttemptCount,
        AwaitsInitialPassword = awaitsInitialPassword
    };

    /// <summary>
    /// Every column of every Pending Export and change row of the batch, as stored, read on the writers' own connection
    /// and transaction.
    /// </summary>
    private static async Task<List<string>> ReadRowsAsync(JimDbContext ctx, Guid[] exportIds)
    {
        var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction?)ctx.Database.CurrentTransaction?.GetDbTransaction();
        var rows = new List<string>();
        foreach (var sql in new[]
        {
            """SELECT * FROM "PendingExports" WHERE "Id" = ANY(@ids) ORDER BY "Id" """,
            """SELECT * FROM "PendingExportAttributeValueChanges" WHERE "PendingExportId" = ANY(@ids) ORDER BY "Id" """
        })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("ids", exportIds);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i =>
                    $"{reader.GetName(i)}={(reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i) is DateTime dt ? dt.ToString("O") : reader.GetValue(i), CultureInfo.InvariantCulture))}")));
            }
        }
        return rows;
    }

    /// <summary>
    /// Runs an action just before any statement whose text contains a given fragment, on its way to the database, handed
    /// the statement so it can read on the statement's own connection and transaction.
    /// </summary>
    private sealed class BeforeStatementInterceptor(string commandTextFragment, Func<System.Data.Common.DbCommand, Task> before) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(commandTextFragment, StringComparison.Ordinal))
                await before(command);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    #endregion
}
