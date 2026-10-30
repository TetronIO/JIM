// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Tetron.PowerShellRemoting.WSMan;

/// <summary>
/// The WS-Management messages PowerShell remoting uses (MS-PSRP 3.1.5.3, MS-WSMV): the WinRS shell operations Create,
/// Command, Send, Receive, Signal and Delete, and the parsing of their replies.
/// </summary>
public static class WSManMessages
{
    private static readonly XNamespace S = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace A = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace W = "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd";
    private static readonly XNamespace P = "http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd";
    private static readonly XNamespace Rsp = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell";
    private static readonly XNamespace Pwsh = "http://schemas.microsoft.com/powershell";
    private static readonly XNamespace Fault = "http://schemas.microsoft.com/wbem/wsman/1/wsmanfault";
    private static readonly XNamespace Xml = XNamespace.Xml;

    private const string ActionCreate = "http://schemas.xmlsoap.org/ws/2004/09/transfer/Create";
    private const string ActionDelete = "http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete";
    private const string ShellActions = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/";
    private const string CommandStateDone = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Done";
    private const string Anonymous = "http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous";

    /// <summary>Opens a PowerShell shell (a runspace pool) carrying the opening PSRP fragments as base64.</summary>
    public static byte[] Create(WSManSessionSettings settings, Guid shellId, string creationXml)
    {
        var shell = new XElement(Rsp + "Shell",
            new XAttribute("ShellId", Id(shellId)),
            new XElement(Rsp + "InputStreams", "stdin pr"),
            new XElement(Rsp + "OutputStreams", "stdout"),
            new XElement(Pwsh + "creationXml", creationXml));
        return Envelope(settings, ActionCreate, shellId: null, shell,
            ("protocolversion", "2.3", MustComply: true));
    }

    /// <summary>Starts a pipeline, carrying its first PSRP message (usually CREATE_PIPELINE) as base64.</summary>
    public static byte[] Command(WSManSessionSettings settings, Guid shellId, Guid commandId, string arguments)
    {
        var commandLine = new XElement(Rsp + "CommandLine",
            new XAttribute("CommandId", Id(commandId)),
            new XElement(Rsp + "Command", ""),
            new XElement(Rsp + "Arguments", arguments));
        return Envelope(settings, ShellActions + "Command", shellId, commandLine,
            ("WINRS_SKIP_CMD_SHELL", "False", MustComply: false));
    }

    /// <summary>Sends further PSRP fragments to the shell or a pipeline, on <c>stdin</c> or <c>pr</c> (prompt response).</summary>
    public static byte[] Send(WSManSessionSettings settings, Guid shellId, string stream, byte[] data, Guid? commandId)
    {
        ArgumentNullException.ThrowIfNull(data);
        var element = new XElement(Rsp + "Stream", new XAttribute("Name", stream), Convert.ToBase64String(data));
        if (commandId is { } id)
            element.Add(new XAttribute("CommandId", Id(id)));
        return Envelope(settings, ShellActions + "Send", shellId, new XElement(Rsp + "Send", element));
    }

    /// <summary>Asks for output from the shell, or from one pipeline when <paramref name="commandId"/> is given.</summary>
    public static byte[] Receive(WSManSessionSettings settings, Guid shellId, Guid? commandId)
    {
        var desired = new XElement(Rsp + "DesiredStream", "stdout");
        if (commandId is { } id)
            desired.Add(new XAttribute("CommandId", Id(id)));
        return Envelope(settings, ShellActions + "Receive", shellId, new XElement(Rsp + "Receive", desired),
            ("WSMAN_CMDSHELL_OPTION_KEEPALIVE", "True", MustComply: false));
    }

    /// <summary>Signals a pipeline: terminate when it is finished with, or stop it running.</summary>
    public static byte[] Signal(WSManSessionSettings settings, Guid shellId, Guid commandId, WSManSignal signal)
    {
        var code = signal switch
        {
            WSManSignal.Terminate => "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/terminate",
            WSManSignal.PowerShellStop => "powershell/signal/crtl_c",
            _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, null)
        };
        var element = new XElement(Rsp + "Signal", new XAttribute("CommandId", Id(commandId)), new XElement(Rsp + "Code", code));
        return Envelope(settings, ShellActions + "Signal", shellId, element);
    }

    /// <summary>Closes the shell.</summary>
    public static byte[] Delete(WSManSessionSettings settings, Guid shellId) =>
        Envelope(settings, ActionDelete, shellId, body: null);

    /// <summary>Parses a reply and throws <see cref="WSManFaultException"/> if it is a SOAP fault.</summary>
    public static XDocument ThrowIfFault(byte[] reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(reply));
        }
        catch (XmlException error)
        {
            throw new WSManFaultException(0, "InvalidResponse", $"The WinRM server's reply is not valid XML: {error.Message}");
        }

        var fault = document.Root?.Element(S + "Body")?.Element(S + "Fault");
        if (fault is null)
            return document;

        var detail = fault.Element(S + "Detail")?.Element(Fault + "WSManFault");
        var code = uint.TryParse(detail?.Attribute("Code")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var subcode = fault.Element(S + "Code")?.Element(S + "Subcode")?.Element(S + "Value")?.Value ?? "";
        var message = detail?.Element(Fault + "Message")?.Value
                      ?? fault.Element(S + "Reason")?.Element(S + "Text")?.Value
                      ?? "no reason given";
        throw new WSManFaultException(code, subcode, message.Trim());
    }

    /// <summary>Parses a Receive reply into its stdout payloads and whether the command has finished.</summary>
    public static WSManReceiveResult ParseReceive(byte[] reply)
    {
        var response = ThrowIfFault(reply).Root?.Element(S + "Body")?.Element(Rsp + "ReceiveResponse")
                       ?? throw new WSManFaultException(0, "InvalidResponse", "The WinRM server's Receive reply has no ReceiveResponse.");

        var data = response.Elements(Rsp + "Stream")
            .Where(s => !string.IsNullOrWhiteSpace(s.Value))
            .Select(s => Convert.FromBase64String(s.Value))
            .ToList();
        var done = response.Elements(Rsp + "CommandState").Any(s => s.Attribute("State")?.Value == CommandStateDone);
        return new WSManReceiveResult(data, done);
    }

    private static byte[] Envelope(WSManSessionSettings settings, string action, Guid? shellId, XElement? body,
        params (string Name, string Value, bool MustComply)[] options)
    {
        var mustUnderstand = new XAttribute(S + "mustUnderstand", "true");
        var header = new XElement(S + "Header",
            new XElement(A + "To", settings.Endpoint.AbsoluteUri),
            new XElement(A + "ReplyTo", new XElement(A + "Address", new XAttribute(S + "mustUnderstand", "true"), Anonymous)),
            new XElement(A + "Action", mustUnderstand, action),
            new XElement(W + "MaxEnvelopeSize", new XAttribute(S + "mustUnderstand", "true"), settings.MaxEnvelopeSize.ToString(CultureInfo.InvariantCulture)),
            new XElement(A + "MessageID", $"uuid:{Id(Guid.NewGuid())}"),
            new XElement(W + "Locale", new XAttribute(S + "mustUnderstand", "false"), new XAttribute(Xml + "lang", "en-US")),
            new XElement(P + "DataLocale", new XAttribute(S + "mustUnderstand", "false"), new XAttribute(Xml + "lang", "en-US")),
            new XElement(P + "SessionId", new XAttribute(S + "mustUnderstand", "false"), $"uuid:{Id(settings.SessionId)}"),
            new XElement(W + "OperationTimeout", XmlConvert.ToString(settings.OperationTimeout)),
            new XElement(W + "ResourceURI", new XAttribute(S + "mustUnderstand", "true"), settings.ResourceUri));

        if (shellId is { } id)
            header.Add(new XElement(W + "SelectorSet", new XElement(W + "Selector", new XAttribute("Name", "ShellId"), Id(id))));

        if (options.Length > 0)
        {
            header.Add(new XElement(W + "OptionSet", new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                options.Select(o =>
                {
                    var option = new XElement(W + "Option", new XAttribute("Name", o.Name), o.Value);
                    if (o.MustComply)
                        option.Add(new XAttribute("MustComply", "true"));
                    return option;
                })));
        }

        var envelope = new XElement(S + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", S),
            new XAttribute(XNamespace.Xmlns + "a", A),
            new XAttribute(XNamespace.Xmlns + "w", W),
            new XAttribute(XNamespace.Xmlns + "p", P),
            new XAttribute(XNamespace.Xmlns + "rsp", Rsp),
            header,
            new XElement(S + "Body", body));

        return Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting));
    }

    private static string Id(Guid id) => id.ToString().ToUpperInvariant();
}
