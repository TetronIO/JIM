// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using JIM.Models.Staging;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Tells an export the directory refused apart from one that failed for any other reason (#2118).
/// <para>
/// A directory that answers a change with a failing result code has considered it and said no: the bind account lacks
/// the rights, the entry breaks the schema, a mandatory attribute is missing, the parent is not there. None of that is a
/// fault in JIM, and reported unclassified it lands as an Unhandled Error, which reads as one and fails the Activity.
/// Classified, insufficientAccessRights (50) becomes <see cref="ConnectedSystemExportErrorType.AccessDenied"/>, naming the
/// rights to grant, and every other refusal becomes <see cref="ConnectedSystemExportErrorType.RejectedByConnectedSystem"/>,
/// carrying the directory's own words.
/// </para>
/// <para>
/// Only a result the directory itself returned counts. Codes from 81 upwards are the client library's (server down,
/// timeout, connect error), and a directory that was busy, unavailable or out of time did not consider the change at
/// all; those stay unclassified, as does any exception that did not come from the directory. The more specific
/// classifiers (uniqueness, an account that cannot be enabled without a password) are consulted first.
/// </para>
/// </summary>
internal static class LdapDirectoryRefusalClassifier
{
    // The highest result code a directory returns (other, 80). Codes above it are raised by the client library.
    private const int HighestServerResultCode = (int)ResultCode.Other;

    // Server result codes that do not mean the directory refused the change: success and its informational siblings,
    // and the directory being unable to consider it just now.
    private static readonly HashSet<ResultCode> NotRefusals =
    [
        ResultCode.Success,
        ResultCode.CompareFalse,
        ResultCode.CompareTrue,
        ResultCode.Referral,
        ResultCode.ReferralV2,
        ResultCode.SaslBindInProgress,
        ResultCode.TimeLimitExceeded,
        ResultCode.Busy,
        ResultCode.Unavailable
    ];

    /// <summary>
    /// The result for an export the directory refused, or null when the failure is not a refusal.
    /// </summary>
    /// <param name="exception">The exception the export failed with: the library's <see cref="DirectoryOperationException"/>
    /// carrying the response, or the <see cref="LdapException"/> the Connector raised itself from a non-success one.</param>
    internal static ConnectedSystemExportResult? FailureFor(Exception exception)
    {
        if (!TryGetRefusal(exception, out var resultCode))
            return null;

        return resultCode == ResultCode.InsufficientAccessRights
            ? ConnectedSystemExportResult.Failed(
                "The directory refused the change because the account JIM connects as does not have permission to make it " +
                "(insufficientAccessRights). Grant the Connected System's bind account the rights to create, modify, move " +
                "or delete this object and write its attributes, or narrow the Attribute Flows to what it may write. " +
                $"The directory said: {DirectoryMessage(exception)}",
                ConnectedSystemExportErrorType.AccessDenied)
            : ConnectedSystemExportResult.Failed(
                $"The directory refused the change ({resultCode}): {DirectoryMessage(exception)}",
                ConnectedSystemExportErrorType.RejectedByConnectedSystem);
    }

    // The directory's own diagnostic where the response carries one; the library's message for the result code otherwise.
    private static string DirectoryMessage(Exception exception) => exception switch
    {
        DirectoryOperationException { Response.ErrorMessage: { Length: > 0 } serverMessage } => serverMessage.Trim(),
        _ => exception.Message.Trim()
    };

    private static bool TryGetRefusal(Exception exception, out ResultCode resultCode)
    {
        int? code = exception switch
        {
            DirectoryOperationException { Response: { } response } => (int)response.ResultCode,
            LdapException ldapException => ldapException.ErrorCode,
            _ => null
        };

        resultCode = (ResultCode)(code ?? 0);
        return code is > 0 and <= HighestServerResultCode && !NotRefusals.Contains(resultCode);
    }
}
