// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation;
using System.Management.Automation.Remoting;
using System.Net.Sockets;
using System.Security.Authentication;
using Tetron.PowerShellRemoting.Kerberos;
using Tetron.PowerShellRemoting.WSMan;

namespace Tetron.PowerShellRemoting.Tests;

public class RemoteFailureTests
{
    private const string ExchangeErrorId =
        "[Server=EX1,RequestId=5d1c2b7e-0000-0000-0000-000000000000,TimeStamp=31/10/2026 15:00:00] " +
        "[FailureCategory=Cmdlet-ManagementObjectNotFoundException] 9F7B4C5F,Microsoft.Exchange.Management.RecipientTasks.GetMailbox";

    [Test]
    public void FromErrorRecord_ExchangeError_KeepsWhatAnRpeiNeedsIncludingExchangesFailureCategory()
    {
        var record = new ErrorRecord(new ItemNotFoundException("The operation couldn't be performed because object 'jim-missing' couldn't be found."),
            ExchangeErrorId, ErrorCategory.NotSpecified, "jim-missing");

        var failure = RemoteFailure.FromErrorRecord(record);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Kind, Is.EqualTo(RemoteFailureKind.Command));
            Assert.That(failure.Message, Does.Contain("couldn't be found"));
            Assert.That(failure.ErrorId, Is.EqualTo(ExchangeErrorId));
            Assert.That(failure.FailureCategory, Is.EqualTo("Cmdlet-ManagementObjectNotFoundException"));
            Assert.That(failure.TargetName, Is.EqualTo("jim-missing"));
            Assert.That(failure.Reason, Is.EqualTo(nameof(ItemNotFoundException)));
            Assert.That(failure.IsTransient, Is.False);
        }
    }

    [Test]
    public void FromErrorRecord_ErrorFromAnyOtherServer_HasNoFailureCategory()
    {
        var record = new ErrorRecord(new ItemNotFoundException("not found"), "PathNotFound,Microsoft.PowerShell.Commands.GetItemCommand",
            ErrorCategory.ObjectNotFound, "/no/such/path");

        Assert.That(RemoteFailure.FromErrorRecord(record).FailureCategory, Is.Null);
    }

    [TestCaseSource(nameof(TransportFailures))]
    public void FromException_TransportFailure_IsClassified(Exception error, RemoteFailureKind kind, bool transient)
    {
        var failure = RemoteFailure.FromException(error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Kind, Is.EqualTo(kind));
            Assert.That(failure.IsTransient, Is.EqualTo(transient));
            Assert.That(failure.Message, Is.Not.Empty);
        }
    }

    [Test]
    public void FromException_RemoteExceptionFromAStoppedInvocation_IsStopped()
    {
        var failure = RemoteFailure.FromException(new RemoteException("The pipeline has been stopped."), PSInvocationState.Stopped);

        Assert.That(failure.Kind, Is.EqualTo(RemoteFailureKind.Stopped));
    }

    [Test]
    public void FromException_RemoteExceptionFromAFailedInvocation_IsTheCommandsOwnError()
    {
        var failure = RemoteFailure.FromException(new RemoteException("Cannot find path '/no/such/path' because it does not exist."), PSInvocationState.Failed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Kind, Is.EqualTo(RemoteFailureKind.Command));
            Assert.That(failure.Message, Does.Contain("/no/such/path"));
        }
    }

    private static IEnumerable<TestCaseData> TransportFailures()
    {
        // As PowerShell reports them: the transport's error, wrapped in a PSRemotingTransportException.
        static Exception Wrapped(Exception inner) => new PSRemotingTransportException(inner.Message, inner);

        yield return new TestCaseData(Wrapped(new KerberosException("gss_acquire_cred_with_password", 0x000D0000, 0x96C73A18, "Preauthentication failed")),
            RemoteFailureKind.Authentication, false).SetName("Wrong password");
        yield return new TestCaseData(Wrapped(new WSManFaultException(5, "AccessDenied", "The WinRM server refused Kerberos authentication.")),
            RemoteFailureKind.Authentication, false).SetName("Refused by the server");
        yield return new TestCaseData(Wrapped(new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused))),
            RemoteFailureKind.Connection, true).SetName("Connection refused");
        yield return new TestCaseData(Wrapped(new HttpRequestException("The SSL connection could not be established.",
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback."))),
            RemoteFailureKind.Certificate, false).SetName("Untrusted certificate");
        yield return new TestCaseData(Wrapped(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 6 seconds elapsing.",
            new TimeoutException("The operation was canceled."))),
            RemoteFailureKind.Timeout, true).SetName("Request timeout");
        yield return new TestCaseData(Wrapped(new WSManFaultException(2150858817, "w:ActionNotSupported", "Unsupported action")),
            RemoteFailureKind.Protocol, false).SetName("WS-Management fault");
        yield return new TestCaseData(Wrapped(new HttpRequestException("An error occurred while sending the request.",
            new IOException("Unable to read data from the transport connection: Connection reset by peer."))),
            RemoteFailureKind.Connection, true).SetName("Connection reset");
        yield return new TestCaseData(new PipelineStoppedException(), RemoteFailureKind.Stopped, false).SetName("Stopped locally");
        yield return new TestCaseData(new OperationCanceledException(), RemoteFailureKind.Stopped, false).SetName("Cancelled by the caller");
    }
}
