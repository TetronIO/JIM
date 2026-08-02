// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using System.Text.Json;

namespace JIM.Models.Activities;

/// <summary>
/// Turns a failure into the structured detail stored on <see cref="Activity.ErrorDetail"/>, and back again for the
/// portal to render.
/// </summary>
/// <remarks>
/// An error message is a sentence; some failures also have facts worth showing. A rejected LDAPS certificate is the
/// first: an administrator needs to see which certificate was presented, and by whom, to know whether to trust it,
/// renew it, or reach the directory by a different name.
/// </remarks>
public static class ActivityErrorDetail
{
    /// <summary>
    /// Identifies the payload's shape, so a reader can tell whether it recognises the content before parsing it.
    /// </summary>
    public const string ServerCertificateKind = "server-certificate";

    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Produces structured detail for the failures that have any, walking inner exceptions so a wrapped failure is
    /// still described. Returns null when there is nothing to add beyond the message.
    /// </summary>
    public static string? TryDescribe(Exception exception)
    {
        var certificateRejection = FindCertificateRejection(exception);
        if (certificateRejection == null)
            return null;

        return JsonSerializer.Serialize(new ActivityErrorDetailEnvelope
        {
            Kind = ServerCertificateKind,
            ServerCertificate = certificateRejection.Diagnostic
        }, SerialiserOptions);
    }

    /// <summary>
    /// Reads back detail written by <see cref="TryDescribe"/>. Returns null when the value is absent, unrecognised, or
    /// no longer parses, so a rendering caller can fall back to the plain message rather than failing.
    /// </summary>
    public static ServerCertificateDiagnostic? TryReadServerCertificate(string? errorDetail)
    {
        if (string.IsNullOrWhiteSpace(errorDetail))
            return null;

        try
        {
            var envelope = JsonSerializer.Deserialize<ActivityErrorDetailEnvelope>(errorDetail, SerialiserOptions);
            return envelope?.Kind == ServerCertificateKind ? envelope.ServerCertificate : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ServerCertificateRejectedException? FindCertificateRejection(Exception? exception)
    {
        while (exception != null)
        {
            if (exception is ServerCertificateRejectedException certificateRejection)
                return certificateRejection;

            exception = exception.InnerException;
        }

        return null;
    }
}
