// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
using Tetron.PowerShellRemoting.WSMan;

namespace Tetron.PowerShellRemoting.Transport;

/// <summary>
/// Turns the out-of-process packets PowerShell's remoting engine writes (one XML packet per line, each acknowledged)
/// into WinRM shell operations, and WinRM output back into packets.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Session <c>Data</c> up to the end of INIT_RUNSPACEPOOL is the Create; later session data is a Send.</item>
///   <item><c>Command</c> is acknowledged at once; the pipeline's <c>Data</c> up to the end of its first message is the
///   Command; later data is a Send to it.</item>
///   <item><c>Signal</c> stops a pipeline; <c>Close</c> terminates a pipeline, or deletes the shell for the session.</item>
///   <item>A Receive pump per shell and per pipeline turns their stdout into <c>Data</c> packets.</item>
/// </list>
/// Packets are handled one at a time, in order, on a dedicated thread, so PowerShell's writer never blocks on the
/// network. Control operations share one connection; each Receive pump has its own, because a Receive may be held by
/// the server for the whole operation timeout. The approach follows PSWSMan (MIT, Jordan Borean), which first showed
/// PowerShell's custom transport API can carry WinRM.
/// </remarks>
internal sealed class OutOfProcWSManTranslator : IDisposable
{
    private const uint InitRunspacePool = 0x00010004;
    private const int EnvelopeOverhead = 4096;

    private readonly RemoteSessionOptions _options;
    private readonly WSManSessionSettings _settings;
    private readonly Guid _shellId;
    private readonly Action<string> _onPacket;
    private readonly Action<Exception> _onError;
    private readonly BlockingCollection<string> _requests = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ManualResetEventSlim _firstShellReceive = new();
    private readonly PsrpOpeningMessage _sessionOpening = new(InitRunspacePool);
    private readonly Dictionary<Guid, Pipeline> _pipelines = [];
    private readonly ConcurrentBag<WinRMConnection> _connections = [];
    private readonly WinRMConnection _control;
    private bool _shellCreated;
    private volatile bool _closing;
    private int _disposed;

    public OutOfProcWSManTranslator(RemoteSessionOptions options, Guid shellId, Action<string> onPacket, Action<Exception> onError)
    {
        _options = options;
        _shellId = shellId;
        _onPacket = onPacket;
        _onError = onError;
        _settings = new WSManSessionSettings(options.ConnectionUri, $"http://schemas.microsoft.com/powershell/{options.ConfigurationName}",
            Guid.NewGuid(), 153600, options.OperationTimeout);
        _control = NewConnection();
        Writer = new PacketWriter(_requests);
        new Thread(Run) { IsBackground = true, Name = $"Tetron.PowerShellRemoting {shellId}" }.Start();
    }

    /// <summary>The writer PowerShell sends its packets to, one per line.</summary>
    public TextWriter Writer { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _closing = true;
        _lifetime.Cancel();
        _requests.CompleteAdding();
        foreach (var connection in _connections)
            connection.Dispose();
    }

    private WinRMConnection NewConnection()
    {
        var connection = new WinRMConnection(_options);
        _connections.Add(connection);
        return connection;
    }

    private void Run()
    {
        foreach (var line in _requests.GetConsumingEnumerable())
        {
            try
            {
                Process(line);
            }
            catch (Exception error) when (error is not OperationCanceledException || !_closing)
            {
                if (!_closing)
                    _onError(error);
            }
        }
    }

    private void Process(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var packet = XElement.Parse(line);
        var psGuid = Guid.Parse(packet.Attribute("PSGuid")?.Value
                                ?? throw new InvalidDataException($"The {packet.Name.LocalName} packet has no PSGuid."));

        switch (packet.Name.LocalName)
        {
            case "Data":
                var stream = packet.Attribute("Stream")?.Value == "PromptResponse" ? "pr" : "stdin";
                var data = Convert.FromBase64String(packet.Value);
                if (psGuid == Guid.Empty)
                    OnSessionData(stream, data);
                else
                    OnPipelineData(psGuid, stream, data);
                break;

            case "Command":
                _pipelines[psGuid] = new Pipeline(psGuid);
                Emit($"<CommandAck PSGuid='{psGuid}' />");
                break;

            case "Signal":
                if (_pipelines.TryGetValue(psGuid, out var toStop) && toStop.Created)
                    Call(WSManMessages.Signal(_settings, _shellId, psGuid, WSManSignal.PowerShellStop));
                Emit($"<SignalAck PSGuid='{psGuid}' />");
                break;

            case "Close":
                if (psGuid == Guid.Empty)
                    OnSessionClose();
                else
                    OnPipelineClose(psGuid);
                break;

            default:
                throw new InvalidDataException($"Unknown PowerShell transport packet {packet.Name.LocalName}.");
        }
    }

    private void OnSessionData(string stream, byte[] data)
    {
        if (_shellCreated)
        {
            WaitForFirstShellReceive();
            SendChunked(stream, data, commandId: null);
        }
        else
        {
            _sessionOpening.Append(data);
            if (_sessionOpening.IsComplete)
            {
                Call(WSManMessages.Create(_settings, _shellId, Convert.ToBase64String(_sessionOpening.TakeAll())));
                _shellCreated = true;
                StartReceive(pipeline: null);
            }
        }

        Emit($"<DataAck PSGuid='{Guid.Empty}' />");
    }

    private void OnPipelineData(Guid commandId, string stream, byte[] data)
    {
        if (!_pipelines.TryGetValue(commandId, out var pipeline))
            throw new InvalidDataException($"Data arrived for unknown command {commandId}.");

        if (pipeline.Created)
        {
            SendChunked(stream, data, commandId);
        }
        else
        {
            pipeline.Opening.Append(data);
            if (pipeline.Opening.IsComplete)
            {
                Call(WSManMessages.Command(_settings, _shellId, commandId, Convert.ToBase64String(pipeline.Opening.TakeAll())));
                pipeline.Created = true;
                StartReceive(pipeline);
            }
        }

        Emit($"<DataAck PSGuid='{commandId}' />");
    }

    private void OnPipelineClose(Guid commandId)
    {
        if (_pipelines.Remove(commandId, out var pipeline))
        {
            pipeline.Receive.Cancel();
            if (pipeline.Created)
                Call(WSManMessages.Signal(_settings, _shellId, commandId, WSManSignal.Terminate));
        }

        Emit($"<CloseAck PSGuid='{commandId}' />");
    }

    private void OnSessionClose()
    {
        _closing = true;
        try
        {
            if (_shellCreated)
                Call(WSManMessages.Delete(_settings, _shellId));
        }
        catch (WSManFaultException)
        {
            // The shell goes either way when the session ends; PowerShell only waits for the acknowledgement.
        }
        finally
        {
            _lifetime.Cancel();
        }

        Emit($"<CloseAck PSGuid='{Guid.Empty}' />");
    }

    private void SendChunked(string stream, ReadOnlySpan<byte> data, Guid? commandId)
    {
        var chunkSize = (_settings.MaxEnvelopeSize - EnvelopeOverhead) / 4 * 3;
        do
        {
            var chunk = data[..Math.Min(chunkSize, data.Length)];
            Call(WSManMessages.Send(_settings, _shellId, stream, chunk.ToArray(), commandId));
            data = data[chunk.Length..];
        }
        while (data.Length > 0);
    }

    // Input for the shell must wait until the server has answered its first Receive.
    private void WaitForFirstShellReceive()
    {
        if (!_firstShellReceive.IsSet)
            _firstShellReceive.Wait(_lifetime.Token);
    }

    private void Call(byte[] envelope) =>
        WSManMessages.ThrowIfFault(_control.SendAsync(envelope, _lifetime.Token).GetAwaiter().GetResult());

    private void StartReceive(Pipeline? pipeline)
    {
        var connection = NewConnection();
        var cancellation = pipeline is null ? _lifetime.Token : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, pipeline.Receive.Token).Token;
        _ = Task.Run(() => ReceiveLoopAsync(connection, pipeline, cancellation), CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(WinRMConnection connection, Pipeline? pipeline, CancellationToken cancellationToken)
    {
        var psGuid = pipeline?.CommandId ?? Guid.Empty;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                WSManReceiveResult result;
                try
                {
                    var reply = await connection.SendAsync(WSManMessages.Receive(_settings, _shellId, pipeline?.CommandId), cancellationToken).ConfigureAwait(false);
                    result = WSManMessages.ParseReceive(reply);
                }
                catch (WSManFaultException fault) when (fault.IsOperationTimeout)
                {
                    continue;
                }

                foreach (var data in result.Data)
                    Emit($"<Data Stream='Default' PSGuid='{psGuid}'>{Convert.ToBase64String(data)}</Data>");

                if (pipeline is null)
                    _firstShellReceive.Set();
                if (result.CommandDone)
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The pipeline or the session closed.
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (!_closing && pipeline?.Receive.IsCancellationRequested != true)
                _onError(error);
        }
        finally
        {
            connection.Dispose();
        }
    }

    private void Emit(string packet)
    {
        if (Volatile.Read(ref _disposed) == 0)
            _onPacket(packet);
    }

    private sealed class Pipeline(Guid commandId)
    {
        public Guid CommandId { get; } = commandId;
        public PsrpOpeningMessage Opening { get; } = new(requiredMessageType: null);
        public CancellationTokenSource Receive { get; } = new();
        public bool Created { get; set; }
    }

    /// <summary>Collects what PowerShell writes into lines, one packet each, for the translator thread.</summary>
    private sealed class PacketWriter(BlockingCollection<string> lines) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value == '\n')
                {
                    if (!lines.IsAddingCompleted)
                        lines.Add(_line.ToString().TrimEnd('\r'));
                    _line.Clear();
                }
                else
                {
                    _line.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            foreach (var c in value ?? "")
                Write(c);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }
    }
}
