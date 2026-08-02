// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using JIM.Models.Staging;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;
using JIM.Scim.Serialisation;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Carries prepared export operations to a service provider's <c>/Bulk</c> endpoint (RFC 7644 section
/// 3.7) instead of sending each as its own request.
/// <para>
/// Bulk buys throughput and nothing else, so everything here is about what it must not cost. JIM pairs
/// an outcome with the change that produced it, and a bulk response is neither ordered nor guaranteed
/// complete, so outcomes are correlated rather than counted and an operation the provider said nothing
/// about is reported as failed rather than assumed applied. Recording a change as exported when it
/// never happened is the corrupted state the Synchronisation Integrity rules exist to prevent, and it
/// would surface only as drift nobody could explain.
/// </para>
/// </summary>
internal sealed class ScimBulkExporter
{
    private readonly ScimHttpClient _client;
    private readonly ScimProviderCapabilities _capabilities;
    private readonly ScimBulkEndpointState _endpointState;
    private readonly ILogger _logger;

    public ScimBulkExporter(ScimHttpClient client, ScimProviderCapabilities capabilities, ScimBulkEndpointState endpointState, ILogger logger)
    {
        _client = client;
        _capabilities = capabilities;
        _endpointState = endpointState;
        _logger = logger;
    }

    /// <summary>
    /// Whether the next batch should go through <c>/Bulk</c> at all: the provider has to advertise it,
    /// and must not already have proved this run that it does not have it.
    /// </summary>
    public bool IsUsable => _capabilities.SupportsBulk && !_endpointState.IsUnavailable;

    /// <summary>
    /// Applies every prepared operation, returning the outcome of each keyed by its position in the
    /// Pending Export batch.
    /// </summary>
    /// <param name="operations">The operations to apply, each carrying the index of the Pending Export it came from.</param>
    /// <param name="sendIndividually">
    /// The per-object path, used where an operation cannot travel in a batch (it exceeds the provider's
    /// payload limit on its own) or where the provider turns out to have no bulk endpoint. Only ever
    /// called where nothing can already have been applied.
    /// </param>
    public async Task<Dictionary<int, ConnectedSystemExportResult>> ExecuteAsync(
        IReadOnlyList<ScimBulkExportOperation> operations,
        Func<ScimExportOperation, CancellationToken, Task<ConnectedSystemExportResult>> sendIndividually,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<int, ConnectedSystemExportResult>();
        var batches = Batch(operations, out var oversized);

        // An operation too large for any bulk request is not too large for a request of its own: the
        // limit is the bulk endpoint's, not the provider's.
        foreach (var operation in oversized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[operation.Index] = await sendIndividually(operation.Operation, cancellationToken);
        }

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (index, result) in await SendBatchAsync(batch, sendIndividually, cancellationToken))
                results[index] = result;
        }

        _logger.Debug("SCIM export: sent {OperationCount} operation(s) as {BatchCount} bulk request(s), plus {OversizedCount} sent individually for exceeding the bulk payload limit.",
            operations.Count, batches.Count, oversized.Count);

        return results;
    }

    #region batching
    /// <summary>
    /// Splits the operations into requests that respect both limits the provider advertises. RFC 7644
    /// section 3.7 makes those limits binding, and a provider rejects an oversized batch outright, so
    /// exceeding one fails every change in it rather than merely being impolite.
    /// </summary>
    /// <param name="oversized">
    /// Operations no batch can hold, because one alone exceeds the payload limit.
    /// </param>
    private List<List<ScimBulkExportOperation>> Batch(IReadOnlyList<ScimBulkExportOperation> operations, out List<ScimBulkExportOperation> oversized)
    {
        var maximumOperations = _capabilities.BulkMaxOperations is > 0
            ? _capabilities.BulkMaxOperations.Value
            : ScimConnectorConstants.DefaultBulkMaxOperations;

        // Measured rather than guessed: the envelope is whatever an operationless request serialises to,
        // so the budget stays right if the message model gains a member.
        var envelopeSize = MeasureBytes(new ScimBulkRequest());
        var payloadBudget = _capabilities.BulkMaxPayloadSize;

        var batches = new List<List<ScimBulkExportOperation>>();
        oversized = [];

        var current = new List<ScimBulkExportOperation>();
        var currentSize = envelopeSize;

        foreach (var operation in operations)
        {
            // The separator between this operation and the one before it inside the Operations array.
            var operationSize = MeasureBytes(ToBulkOperation(operation)) + 1;

            if (payloadBudget.HasValue && envelopeSize + operationSize > payloadBudget.Value)
            {
                oversized.Add(operation);
                continue;
            }

            var wouldExceedPayload = payloadBudget.HasValue && currentSize + operationSize > payloadBudget.Value;
            if (current.Count > 0 && (current.Count >= maximumOperations || wouldExceedPayload))
            {
                batches.Add(current);
                current = [];
                currentSize = envelopeSize;
            }

            current.Add(operation);
            currentSize += operationSize;
        }

        if (current.Count > 0)
            batches.Add(current);

        return batches;
    }

    private static int MeasureBytes(object payload)
    {
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload, ScimJson.Options));
    }

    private static ScimBulkOperation ToBulkOperation(ScimBulkExportOperation operation)
    {
        return new ScimBulkOperation
        {
            Method = operation.Operation.Method.Method,
            // Unique across the whole export batch, so unique within any bulk request built from it.
            BulkId = operation.Index.ToString(CultureInfo.InvariantCulture),
            // Bulk operation paths are rooted at the service provider's base, unlike the relative paths
            // a standalone request composes onto it.
            Path = "/" + operation.Operation.Path,
            Data = operation.Operation.Body,
            Version = operation.Operation.EntityTag
        };
    }
    #endregion

    #region sending
    private async Task<Dictionary<int, ConnectedSystemExportResult>> SendBatchAsync(
        List<ScimBulkExportOperation> batch,
        Func<ScimExportOperation, CancellationToken, Task<ConnectedSystemExportResult>> sendIndividually,
        CancellationToken cancellationToken)
    {
        var request = new ScimBulkRequest { Operations = batch.Select(ToBulkOperation).ToList() };

        ScimBulkResponse? response;
        try
        {
            response = await _client.PostAsync<ScimBulkResponse>(ScimEndpoints.Bulk, request, cancellationToken);
        }
        catch (ScimRequestException ex) when (IsEndpointMissing(ex))
        {
            // The provider advertised bulk and has no such endpoint. Nothing can have been applied, so
            // resending the changes individually is safe, and it is the only whole-request failure where
            // that is true.
            _endpointState.MarkUnavailable();
            _logger.Warning("SCIM export: the service provider advertises bulk operations but its {Endpoint} endpoint answered HTTP {StatusCode}. Falling back to one request per object for the rest of this run; turn the 'Use Bulk Operations' setting off to stop asking.",
                ScimEndpoints.Bulk, (int?)ex.StatusCode);

            return await SendIndividuallyAsync(batch, sendIndividually, cancellationToken);
        }
        catch (ScimRequestException ex) when (ex.StatusCode == HttpStatusCode.RequestEntityTooLarge && batch.Count > 1)
        {
            // The provider rejected the request before applying any of it, which the payload limit it
            // advertised evidently did not describe. Halving is safe for the same reason.
            _logger.Warning("SCIM export: the service provider rejected a bulk request of {OperationCount} operation(s) as too large, despite it fitting the limit it advertises. Splitting it and retrying.",
                batch.Count);

            var half = batch.Count / 2;
            var firstHalf = await SendBatchAsync(batch.Take(half).ToList(), sendIndividually, cancellationToken);
            var secondHalf = await SendBatchAsync(batch.Skip(half).ToList(), sendIndividually, cancellationToken);

            foreach (var (index, result) in secondHalf)
                firstHalf[index] = result;

            return firstHalf;
        }
        catch (ScimRequestException ex)
        {
            // The request left JIM and failed. How far the provider got is unknowable, so resending the
            // operations individually could apply changes a second time, creating duplicate resources.
            // Reporting them failed leaves the Pending Exports in place for the next run, by which time
            // a confirming import has said what actually landed.
            _logger.Error(ex, "SCIM export: a bulk request of {OperationCount} operation(s) failed. What the service provider applied before failing is unknown, so every change in it is reported as failed rather than resent.",
                batch.Count);

            return batch.ToDictionary(operation => operation.Index, _ => UnknownOutcomeFailure(ex));
        }

        return Correlate(batch, response);
    }

    private static async Task<Dictionary<int, ConnectedSystemExportResult>> SendIndividuallyAsync(
        List<ScimBulkExportOperation> batch,
        Func<ScimExportOperation, CancellationToken, Task<ConnectedSystemExportResult>> sendIndividually,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<int, ConnectedSystemExportResult>(batch.Count);

        foreach (var operation in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[operation.Index] = await sendIndividually(operation.Operation, cancellationToken);
        }

        return results;
    }

    /// <summary>
    /// A bulk endpoint that is absent rather than broken. Each of these is answered before the provider
    /// looks at the operations, which is what makes resending them individually safe.
    /// </summary>
    private static bool IsEndpointMissing(ScimRequestException exception)
    {
        return exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed;
    }
    #endregion

    #region correlating
    /// <summary>
    /// Pairs each reported outcome with the change that produced it, and fails anything the provider
    /// left unreported.
    /// </summary>
    private Dictionary<int, ConnectedSystemExportResult> Correlate(List<ScimBulkExportOperation> batch, ScimBulkResponse? response)
    {
        var results = new Dictionary<int, ConnectedSystemExportResult>(batch.Count);
        var byBulkId = batch.ToDictionary(operation => operation.Index.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);

        // Only paths appearing once can identify an operation on their own, which is the fallback for a
        // provider that echoes no bulkId. RFC 7644 requires bulkId only on a create, so omitting it
        // elsewhere is conformant and losing track of every update and delete over it is not acceptable.
        var byPath = batch
            .GroupBy(operation => operation.Operation.Path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

        foreach (var reported in response?.Operations ?? [])
        {
            var matched = Match(reported, byBulkId, byPath);
            if (matched == null)
            {
                _logger.Warning("SCIM export: the service provider's bulk response reported an operation JIM cannot match to a change it sent, so the outcome has been discarded. Its bulkId was {BulkId} and its location {Location}.",
                    LogSanitiser.Sanitise(reported.BulkId), LogSanitiser.Sanitise(reported.Location));
                continue;
            }

            if (results.ContainsKey(matched.Index))
            {
                _logger.Warning("SCIM export: the service provider's bulk response reported the same change more than once. The first outcome has been kept, because there is no basis for preferring a later one.");
                continue;
            }

            results[matched.Index] = Interpret(reported, matched);
        }

        // Silence is not consent. A provider that stopped early says nothing about what it never
        // reached, and recording those as exported would delete the Pending Exports with the changes
        // still absent from the provider.
        foreach (var unreported in batch.Where(operation => !results.ContainsKey(operation.Index)))
            results[unreported.Index] = NoOutcomeFailure();

        return results;
    }

    private static ScimBulkExportOperation? Match(
        ScimBulkOperationResult reported,
        Dictionary<string, ScimBulkExportOperation> byBulkId,
        Dictionary<string, ScimBulkExportOperation> byPath)
    {
        if (!string.IsNullOrWhiteSpace(reported.BulkId) && byBulkId.TryGetValue(reported.BulkId, out var byId))
            return byId;

        if (string.IsNullOrWhiteSpace(reported.Location))
            return null;

        var location = Uri.TryCreate(reported.Location, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : reported.Location;

        return byPath
            .Where(candidate => location.EndsWith("/" + candidate.Key, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Value)
            .FirstOrDefault();
    }

    private ConnectedSystemExportResult Interpret(ScimBulkOperationResult reported, ScimBulkExportOperation prepared)
    {
        if (reported.Succeeded)
            return Applied(reported, prepared);

        // A delete of a resource already gone has reached the intended end state; failing would leave a
        // Pending Export retrying for ever against a provider that has already done what was asked.
        if (prepared.Operation.Method == HttpMethod.Delete && reported.StatusCode == (int)HttpStatusCode.NotFound)
            return ConnectedSystemExportResult.Succeeded();

        var error = reported.ReadError();
        _logger.Warning("SCIM export: the service provider rejected a bulk {Method} of {Path} with HTTP {Status}.",
            prepared.Operation.Method.Method, LogSanitiser.Sanitise(prepared.Operation.Path), LogSanitiser.Sanitise(reported.Status));

        return ConnectedSystemExportResult.Failed(Describe(reported, prepared, error), ScimExportErrorClassifier.Classify(reported.StatusCode, error?.ScimType));
    }

    private static ConnectedSystemExportResult Applied(ScimBulkOperationResult reported, ScimBulkExportOperation prepared)
    {
        if (prepared.Operation.Method != HttpMethod.Post)
            return ConnectedSystemExportResult.Succeeded(prepared.Operation.ResourceId);

        var externalId = reported.ReadResourceId();

        return externalId == null
            ? ConnectedSystemExportResult.Failed("The service provider accepted the create inside a bulk request but reported no location or id for it, so JIM has nothing to identify the new resource by.")
            : ConnectedSystemExportResult.Succeeded(externalId);
    }
    #endregion

    #region reporting
    private static string Describe(ScimBulkOperationResult reported, ScimBulkExportOperation prepared, ScimError? error)
    {
        var message = new StringBuilder()
            .Append($"The service provider rejected the {prepared.Operation.Method.Method} of ")
            .Append($"{LogSanitiser.Sanitise(prepared.Operation.Path)} inside a bulk request, ")
            .Append($"reporting HTTP {LogSanitiser.Sanitise(reported.Status)}.");

        // The scimType is a protocol keyword and safe to include; detail is provider-authored free text.
        if (error?.ScimType != null)
            message.Append($" SCIM error type: {LogSanitiser.Sanitise(error.ScimType)}.");
        if (error?.Detail != null)
            message.Append($" Provider detail: {LogSanitiser.Sanitise(error.Detail)}");

        return message.ToString();
    }

    private static ConnectedSystemExportResult UnknownOutcomeFailure(ScimRequestException exception)
    {
        return ConnectedSystemExportResult.Failed(
            "The bulk request carrying this change failed after it was sent, so JIM cannot tell whether the service provider applied it. " +
            "It is reported as failed and left in place rather than sent again, because resending a change that did apply would duplicate it. " +
            $"The next import will show what actually landed. The bulk request failed with: {exception.Message}");
    }

    private static ConnectedSystemExportResult NoOutcomeFailure()
    {
        return ConnectedSystemExportResult.Failed(
            "The service provider did not report an outcome for this change in its bulk response, so JIM cannot confirm it was applied. " +
            "It is reported as failed rather than assumed successful; the next import will show whether it reached the service provider.");
    }
    #endregion
}
