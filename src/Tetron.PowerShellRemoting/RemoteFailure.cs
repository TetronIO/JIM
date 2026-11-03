// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation;
using System.Management.Automation.Remoting;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using Tetron.PowerShellRemoting.Encryption;
using Tetron.PowerShellRemoting.Kerberos;
using Tetron.PowerShellRemoting.WSMan;

namespace Tetron.PowerShellRemoting;

/// <summary>
/// A failure of a remote session or of a command run in it, classified and with the details a caller needs to report
/// it: a command's error record, or the transport's own exception as PowerShell wraps it. It never carries a secret.
/// </summary>
public sealed partial record RemoteFailure
{
    /// <summary>What kind of failure this is.</summary>
    public required RemoteFailureKind Kind { get; init; }

    /// <summary>The message to show an administrator.</summary>
    public required string Message { get; init; }

    /// <summary>The error record's fully qualified error ID, for a command's failure.</summary>
    public string? ErrorId { get; init; }

    /// <summary>The error record's reason: the name of the exception type the server reported.</summary>
    public string? Reason { get; init; }

    /// <summary>What the command was acting on when it failed, as the error record names it.</summary>
    public string? TargetName { get; init; }

    /// <summary>
    /// Exchange's own classification of a command's failure (<c>Cmdlet-ManagementObjectNotFoundException</c>), which
    /// Exchange puts in the error ID. Null for any other server.
    /// </summary>
    public string? FailureCategory { get; init; }

    /// <summary>True when the same call may succeed if retried: the endpoint was unreachable or did not answer.</summary>
    public bool IsTransient => Kind is RemoteFailureKind.Connection or RemoteFailureKind.Timeout;

    /// <summary>A command's failure from an error record it wrote.</summary>
    public static RemoteFailure FromErrorRecord(ErrorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var errorId = record.FullyQualifiedErrorId;
        var failureCategory = errorId is null ? null : ExchangeFailureCategory().Match(errorId);
        return new RemoteFailure
        {
            Kind = RemoteFailureKind.Command,
            Message = record.ErrorDetails?.Message ?? record.Exception?.Message ?? record.ToString(),
            ErrorId = errorId,
            Reason = NullIfEmpty(record.CategoryInfo.Reason),
            TargetName = NullIfEmpty(record.CategoryInfo.TargetName),
            FailureCategory = failureCategory is { Success: true } ? failureCategory.Groups[1].Value : null
        };
    }

    /// <summary>
    /// A failure from an exception that opening a session or running a command threw. Pass the invocation's state when
    /// there is one: a remote command that was stopped ends with a <see cref="RemoteException"/>, and only the state
    /// says it was stopped rather than failed.
    /// </summary>
    public static RemoteFailure FromException(Exception error, PSInvocationState? state = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (state == PSInvocationState.Stopped || Find<PipelineStoppedException>(error) is not null)
            return Transport(RemoteFailureKind.Stopped, error);

        // The innermost cause the transport recognises decides: a timeout or a refused socket is often wrapped in a
        // more general exception that says less.
        var causes = new List<Exception>();
        for (var cause = error; cause is not null; cause = cause.InnerException)
            causes.Add(cause);
        for (var i = causes.Count - 1; i >= 0; i--)
        {
            var kind = Classify(causes[i]);
            if (kind is not null)
                return Transport(kind.Value, causes[i]);
        }

        // A command that failed outright: PowerShell carries the server's error record with the exception.
        if (error is not PSRemotingTransportException && error is IContainsErrorRecord { ErrorRecord: { } record })
            return FromErrorRecord(record) with { Message = MessageOf(record, error) };

        return Transport(RemoteFailureKind.Protocol, error);
    }

    private static RemoteFailureKind? Classify(Exception cause) => cause switch
    {
        KerberosException => RemoteFailureKind.Authentication,
        WSManFaultException { Subcode: "AccessDenied" } => RemoteFailureKind.Authentication,
        WSManFaultException => RemoteFailureKind.Protocol,
        WinRMEncryptionException => RemoteFailureKind.Protocol,
        AuthenticationException => RemoteFailureKind.Certificate,
        TimeoutException => RemoteFailureKind.Timeout,
        SocketException => RemoteFailureKind.Connection,
        IOException => RemoteFailureKind.Connection,
        HttpRequestException => RemoteFailureKind.Connection,
        // Cancelled by the caller; a request that timed out carries a TimeoutException, which is found first.
        OperationCanceledException => RemoteFailureKind.Stopped,
        _ => null
    };

    private static RemoteFailure Transport(RemoteFailureKind kind, Exception cause) => new()
    {
        Kind = kind,
        Message = string.IsNullOrWhiteSpace(cause.Message) ? kind.ToString() : cause.Message
    };

    private static T? Find<T>(Exception error) where T : Exception
    {
        for (var cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is T found)
                return found;
        }

        return null;
    }

    private static string MessageOf(ErrorRecord record, Exception error) =>
        record.ErrorDetails?.Message ?? (string.IsNullOrWhiteSpace(error.Message) ? record.ToString() : error.Message);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    [GeneratedRegex(@"\[FailureCategory=([^\]]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex ExchangeFailureCategory();
}
