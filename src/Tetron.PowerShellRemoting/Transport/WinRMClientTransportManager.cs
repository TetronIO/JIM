// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation.Internal;
using System.Management.Automation.Remoting;
using System.Management.Automation.Remoting.Client;

namespace Tetron.PowerShellRemoting.Transport;

/// <summary>
/// PowerShell's side of the transport: it hands the runspace pool's packets to an <see cref="OutOfProcWSManTranslator"/>
/// and the translator's packets back to PowerShell.
/// </summary>
internal sealed class WinRMClientTransportManager : ClientSessionTransportManagerBase
{
    private readonly RemoteSessionOptions _options;
    private readonly Guid _runspacePoolId;
    private OutOfProcWSManTranslator? _translator;
    private volatile bool _closed;

    public WinRMClientTransportManager(RemoteSessionOptions options, Guid runspacePoolId, PSRemotingCryptoHelper cryptoHelper)
        : base(runspacePoolId, cryptoHelper)
    {
        _options = options;
        _runspacePoolId = runspacePoolId;
    }

    public override void CreateAsync()
    {
        try
        {
            _translator = new OutOfProcWSManTranslator(_options, _runspacePoolId, HandleDataReceived, ReportError);
            SetMessageWriter(_translator.Writer);
            SendOneItem();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ReportError(error);
        }
    }

    protected override void CleanupConnection()
    {
        _closed = true;
        _translator?.Dispose();
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        if (isDisposing)
            _translator?.Dispose();
    }

    /// <summary>Fails the session with <paramref name="error"/> as its reason, keeping its message intact.</summary>
    private void ReportError(Exception error)
    {
        if (_closed)
            return;

        var transportError = error as PSRemotingTransportException ?? new PSRemotingTransportException(error.Message, error);
        RaiseErrorHandler(new TransportErrorOccuredEventArgs(transportError, TransportMethodEnum.Unknown));
    }
}
