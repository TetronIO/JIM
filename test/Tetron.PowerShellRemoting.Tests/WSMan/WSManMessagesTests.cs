// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Xml.Linq;
using Tetron.PowerShellRemoting.WSMan;

namespace Tetron.PowerShellRemoting.Tests.WSMan;

public class WSManMessagesTests
{
    private static readonly XNamespace S = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace A = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace W = "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd";
    private static readonly XNamespace Rsp = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell";
    private static readonly XNamespace Pwsh = "http://schemas.microsoft.com/powershell";

    private static readonly Guid ShellId = Guid.Parse("8d2a3b8f-1111-4c22-9a33-6f5e4d3c2b1a");
    private static readonly Guid CommandId = Guid.Parse("0f0e0d0c-2222-4b3a-8c7d-112233445566");

    private static readonly WSManSessionSettings Settings = new(
        new Uri("http://ex1.panoply.local/PowerShell/"), "http://schemas.microsoft.com/powershell/Microsoft.Exchange",
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), 153600, TimeSpan.FromSeconds(20));

    [Test]
    public void Create_AnyShell_IsACreateForTheConfigurationCarryingTheOpeningFragments()
    {
        var envelope = Parse(WSManMessages.Create(Settings, ShellId, "Q1JFQVRJT04="));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Header(envelope, A + "Action"), Is.EqualTo("http://schemas.xmlsoap.org/ws/2004/09/transfer/Create"));
            Assert.That(Header(envelope, W + "ResourceURI"), Is.EqualTo("http://schemas.microsoft.com/powershell/Microsoft.Exchange"));
            Assert.That(Header(envelope, A + "To"), Is.EqualTo("http://ex1.panoply.local/PowerShell/"));
            Assert.That(Header(envelope, W + "MaxEnvelopeSize"), Is.EqualTo("153600"));
            Assert.That(Header(envelope, W + "OperationTimeout"), Is.EqualTo("PT20S"));
            var shell = Body(envelope).Element(Rsp + "Shell")!;
            Assert.That(shell.Attribute("ShellId")?.Value, Is.EqualTo(ShellId.ToString().ToUpperInvariant()));
            Assert.That(shell.Element(Rsp + "InputStreams")?.Value, Is.EqualTo("stdin pr"));
            Assert.That(shell.Element(Rsp + "OutputStreams")?.Value, Is.EqualTo("stdout"));
            Assert.That(shell.Element(Pwsh + "creationXml")?.Value, Is.EqualTo("Q1JFQVRJT04="));
            Assert.That(Option(envelope, "protocolversion"), Is.EqualTo("2.3"));
        }
    }

    /// <summary>
    /// WS-Management requires an OptionSet holding an option the service must comply with to say so itself, with
    /// <c>s:mustUnderstand="true"</c>. Exchange's IIS-hosted WinRM enforces it, refusing the Create with
    /// <c>w:InvalidOptions</c> (2150858809) when it is missing; the native client, pypsrp and PSWSMan all send it.
    /// </summary>
    [Test]
    public void Create_ProtocolVersionTheServiceMustComplyWith_MarksTheOptionSetMustUnderstand()
    {
        var optionSet = OptionSet(Parse(WSManMessages.Create(Settings, ShellId, "Q1JFQVRJT04=")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(optionSet.Attribute(S + "mustUnderstand")?.Value, Is.EqualTo("true"));
            Assert.That(optionSet.Element(W + "Option")?.Attribute("MustComply")?.Value, Is.EqualTo("true"));
        }
    }

    [Test]
    public void Command_OnlyOptionalOptions_LeavesTheOptionSetOptional()
    {
        var optionSet = OptionSet(Parse(WSManMessages.Command(Settings, ShellId, CommandId, "UElQRUxJTkU=")));

        Assert.That(optionSet.Attribute(S + "mustUnderstand"), Is.Null,
            "an OptionSet of options the service may ignore must not make the service refuse it for not knowing one");
    }

    [Test]
    public void Command_AnyPipeline_CarriesTheFirstMessageAsItsArgumentWithAnEmptyCommand()
    {
        var envelope = Parse(WSManMessages.Command(Settings, ShellId, CommandId, "UElQRUxJTkU="));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Header(envelope, A + "Action"), Is.EqualTo("http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command"));
            Assert.That(Selector(envelope), Is.EqualTo(ShellId.ToString().ToUpperInvariant()));
            var commandLine = Body(envelope).Element(Rsp + "CommandLine")!;
            Assert.That(commandLine.Attribute("CommandId")?.Value, Is.EqualTo(CommandId.ToString().ToUpperInvariant()));
            Assert.That(commandLine.Element(Rsp + "Command")?.Value, Is.Empty);
            Assert.That(commandLine.Element(Rsp + "Arguments")?.Value, Is.EqualTo("UElQRUxJTkU="));
            Assert.That(Option(envelope, "WINRS_SKIP_CMD_SHELL"), Is.EqualTo("False"));
        }
    }

    [Test]
    public void Send_ToAPipeline_Base64EncodesTheDataOnTheNamedStream()
    {
        var envelope = Parse(WSManMessages.Send(Settings, ShellId, "pr", "hello"u8.ToArray(), CommandId));

        var stream = Body(envelope).Element(Rsp + "Send")!.Element(Rsp + "Stream")!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.Attribute("Name")?.Value, Is.EqualTo("pr"));
            Assert.That(stream.Attribute("CommandId")?.Value, Is.EqualTo(CommandId.ToString().ToUpperInvariant()));
            Assert.That(Convert.FromBase64String(stream.Value), Is.EqualTo("hello"u8.ToArray()));
        }
    }

    [Test]
    public void Receive_ForTheShell_AsksForStdoutWithoutACommandAndKeepsAlive()
    {
        var envelope = Parse(WSManMessages.Receive(Settings, ShellId, commandId: null));

        var desired = Body(envelope).Element(Rsp + "Receive")!.Element(Rsp + "DesiredStream")!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(desired.Value, Is.EqualTo("stdout"));
            Assert.That(desired.Attribute("CommandId"), Is.Null);
            Assert.That(Option(envelope, "WSMAN_CMDSHELL_OPTION_KEEPALIVE"), Is.EqualTo("True"));
        }
    }

    [Test]
    public void Receive_ForAPipeline_NamesTheCommand()
    {
        var envelope = Parse(WSManMessages.Receive(Settings, ShellId, CommandId));

        Assert.That(Body(envelope).Element(Rsp + "Receive")!.Element(Rsp + "DesiredStream")!.Attribute("CommandId")?.Value,
            Is.EqualTo(CommandId.ToString().ToUpperInvariant()));
    }

    [TestCase(WSManSignal.Terminate, "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/terminate")]
    [TestCase(WSManSignal.PowerShellStop, "powershell/signal/crtl_c")]
    public void Signal_Code_IsTheWinRMSignalUri(WSManSignal signal, string expected)
    {
        var envelope = Parse(WSManMessages.Signal(Settings, ShellId, CommandId, signal));

        var element = Body(envelope).Element(Rsp + "Signal")!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(element.Element(Rsp + "Code")?.Value, Is.EqualTo(expected));
            Assert.That(element.Attribute("CommandId")?.Value, Is.EqualTo(CommandId.ToString().ToUpperInvariant()));
        }
    }

    [Test]
    public void Delete_TheShell_IsATransferDeleteSelectingTheShell()
    {
        var envelope = Parse(WSManMessages.Delete(Settings, ShellId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Header(envelope, A + "Action"), Is.EqualTo("http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete"));
            Assert.That(Selector(envelope), Is.EqualTo(ShellId.ToString().ToUpperInvariant()));
        }
    }

    [Test]
    public void EveryMessage_HasItsOwnMessageId()
    {
        var first = Header(Parse(WSManMessages.Delete(Settings, ShellId)), A + "MessageID");
        var second = Header(Parse(WSManMessages.Delete(Settings, ShellId)), A + "MessageID");

        Assert.That(first, Is.Not.EqualTo(second).And.StartsWith("uuid:"));
    }

    [Test]
    public void ParseReceive_StreamsForTheCommand_ReturnsTheirDecodedBytesInOrder()
    {
        var reply = Reply(
            $"<rsp:ReceiveResponse xmlns:rsp=\"{Rsp}\">" +
            $"<rsp:Stream Name=\"stdout\" CommandId=\"{CommandId}\">{Convert.ToBase64String("one"u8)}</rsp:Stream>" +
            $"<rsp:Stream Name=\"stdout\" CommandId=\"{CommandId}\">{Convert.ToBase64String("two"u8)}</rsp:Stream>" +
            $"<rsp:CommandState CommandId=\"{CommandId}\" State=\"http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Running\"/>" +
            "</rsp:ReceiveResponse>");

        var result = WSManMessages.ParseReceive(reply);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.Select(d => Encoding.ASCII.GetString(d)), Is.EqualTo(new[] { "one", "two" }));
            Assert.That(result.CommandDone, Is.False);
        }
    }

    [Test]
    public void ParseReceive_CommandStateDone_ReportsTheCommandDone()
    {
        var reply = Reply(
            $"<rsp:ReceiveResponse xmlns:rsp=\"{Rsp}\"><rsp:CommandState CommandId=\"{CommandId}\" " +
            "State=\"http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Done\"/></rsp:ReceiveResponse>");

        Assert.That(WSManMessages.ParseReceive(reply).CommandDone, Is.True);
    }

    [Test]
    public void ParseReceive_TimedOutFault_ThrowsAFaultTheCallerCanRetry()
    {
        var reply = Fault(2150858793, "w:TimedOut", "The WS-Management service cannot complete the operation within the time specified in OperationTimeout.");

        Assert.That(() => WSManMessages.ParseReceive(reply),
            Throws.TypeOf<WSManFaultException>().With.Property(nameof(WSManFaultException.IsOperationTimeout)).True
                .And.Property(nameof(WSManFaultException.Code)).EqualTo(2150858793u));
    }

    [Test]
    public void ThrowIfFault_AccessDenied_CarriesTheServersMessage()
    {
        var reply = Fault(5, "w:AccessDenied", "Access is denied.");

        Assert.That(() => WSManMessages.ThrowIfFault(reply),
            Throws.TypeOf<WSManFaultException>().With.Message.Contains("Access is denied")
                .And.Property(nameof(WSManFaultException.IsOperationTimeout)).False);
    }

    [Test]
    public void ThrowIfFault_NoFault_ReturnsTheEnvelope()
    {
        var reply = Reply("<rsp:SendResponse xmlns:rsp=\"http://schemas.microsoft.com/wbem/wsman/1/windows/shell\"/>");

        Assert.That(WSManMessages.ThrowIfFault(reply).Root?.Name, Is.EqualTo(S + "Envelope"));
    }

    private static XDocument Parse(byte[] message) => XDocument.Parse(Encoding.UTF8.GetString(message));

    private static string? Header(XDocument envelope, XName name) => envelope.Root!.Element(S + "Header")!.Element(name)?.Value;

    private static XElement OptionSet(XDocument envelope) => envelope.Root!.Element(S + "Header")!.Element(W + "OptionSet")!;

    private static XElement Body(XDocument envelope) => envelope.Root!.Element(S + "Body")!;

    private static string? Selector(XDocument envelope) =>
        envelope.Root!.Element(S + "Header")!.Element(W + "SelectorSet")?.Elements(W + "Selector")
            .SingleOrDefault(s => s.Attribute("Name")?.Value == "ShellId")?.Value;

    private static string? Option(XDocument envelope, string name) =>
        envelope.Root!.Element(S + "Header")!.Element(W + "OptionSet")?.Elements(W + "Option")
            .SingleOrDefault(o => o.Attribute("Name")?.Value == name)?.Value;

    private static byte[] Reply(string body) => Encoding.UTF8.GetBytes(
        $"<s:Envelope xmlns:s=\"{S}\" xmlns:a=\"{A}\"><s:Header><a:Action>reply</a:Action></s:Header><s:Body>{body}</s:Body></s:Envelope>");

    private static byte[] Fault(uint code, string subcode, string text) => Reply(
        $"<s:Fault><s:Code><s:Value>s:Receiver</s:Value><s:Subcode><s:Value xmlns:w=\"{W}\">{subcode}</s:Value></s:Subcode></s:Code>" +
        $"<s:Reason><s:Text xml:lang=\"en-US\">{text}</s:Text></s:Reason><s:Detail>" +
        $"<f:WSManFault xmlns:f=\"http://schemas.microsoft.com/wbem/wsman/1/wsmanfault\" Code=\"{code}\" Machine=\"ex1\">" +
        $"<f:Message>{text}</f:Message></f:WSManFault></s:Detail></s:Fault>");
}
