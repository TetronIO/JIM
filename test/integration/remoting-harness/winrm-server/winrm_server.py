# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
"""A stand-in WinRM server for testing Tetron.PowerShellRemoting without Windows (#2021, spike Phase 1a).

It does what a Windows WinRM endpoint does for PowerShell remoting, closely enough to exercise a client end to end:

- Kerberos authentication with the "Kerberos" HTTP scheme, connection-based, from the service keytab
  (KRB5_KTNAME), through pyspnego: an implementation independent of the client under test.
- WinRM message encryption (HTTP-Kerberos-session-encrypted, MS-WSMV 2.2.9.1). Unencrypted requests are refused once
  authenticated, as a WinRM listener with AllowUnencrypted=false refuses them.
- The WS-Management shell operations PowerShell remoting uses (MS-PSRP 3.1.5.3): Create, Command, Send, Receive,
  Signal and Delete.
- The PowerShell Remoting Protocol itself is not reimplemented: each shell's PSRP traffic is passed to its own
  `pwsh -s` process, PowerShell's out-of-process server.

What it is not: Windows. It shares any misreading of MS-WSMV that its author also made in the client, which is why
the Active Directory lab (Phase 1b) is the conformance test, not this.
"""

import base64
import http.server
import logging
import os
import queue
import re
import socketserver
import ssl
import struct
import subprocess
import sys
import threading
import uuid
import xml.etree.ElementTree as ET

import spnego

LOG = logging.getLogger("winrm-server")

NS = {
    "s": "http://www.w3.org/2003/05/soap-envelope",
    "a": "http://schemas.xmlsoap.org/ws/2004/08/addressing",
    "w": "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd",
    "p": "http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd",
    "rsp": "http://schemas.microsoft.com/wbem/wsman/1/windows/shell",
    "x": "http://schemas.xmlsoap.org/ws/2004/09/transfer",
}

ACTION_CREATE = "http://schemas.xmlsoap.org/ws/2004/09/transfer/Create"
ACTION_DELETE = "http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete"
ACTION_COMMAND = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command"
ACTION_SEND = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Send"
ACTION_RECEIVE = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive"
ACTION_SIGNAL = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Signal"

COMMAND_STATE_DONE = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Done"
COMMAND_STATE_RUNNING = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Running"

PSRP_PIPELINE_STATE = 0x00041006
EMPTY_GUID = "00000000-0000-0000-0000-000000000000"

BOUNDARY = b"Encrypted Boundary"
KERBEROS_PROTOCOL = "application/HTTP-Kerberos-session-encrypted"

SERVICE_HOST = os.environ.get("WINRM_SERVICE_HOST", "winrm-harness.harness.test")
RECEIVE_TIMEOUT_SECONDS = float(os.environ.get("WINRM_RECEIVE_TIMEOUT_SECONDS", "10"))


# --- WinRM message encryption --------------------------------------------------------------------------------------

def encrypt_message(context, plaintext: bytes):
    """Encodes a SOAP reply as a WinRM Kerberos-encrypted body (MS-WSMV 2.2.9.1)."""
    wrapped = context.wrap_winrm(plaintext)
    body = (
        b"--" + BOUNDARY + b"\r\n"
        b"\tContent-Type: " + KERBEROS_PROTOCOL.encode() + b"\r\n"
        b"\tOriginalContent: type=application/soap+xml;charset=UTF-8;Length="
        + str(len(plaintext) + wrapped.padding_length).encode() + b"\r\n"
        b"--" + BOUNDARY + b"\r\n"
        b"\tContent-Type: application/octet-stream\r\n"
        + struct.pack("<I", len(wrapped.header)) + wrapped.header + wrapped.data
        + b"--" + BOUNDARY + b"--\r\n"
    )
    content_type = f'multipart/encrypted;protocol="{KERBEROS_PROTOCOL}";boundary="{BOUNDARY.decode()}"'
    return body, content_type


def decrypt_message(context, body: bytes, content_type: str) -> bytes:
    """Decodes a WinRM Kerberos-encrypted request body; refuses anything else."""
    if "multipart/encrypted" not in content_type.lower() or KERBEROS_PROTOCOL.lower() not in content_type.lower():
        raise ValueError(f"request is not Kerberos-encrypted: {content_type}")

    marker = b"--" + BOUNDARY + b"\r\n\tContent-Type: application/octet-stream\r\n"
    start = body.index(marker)
    length_match = re.search(rb";Length=(\d+)\r\n", body[:start])
    if not length_match:
        raise ValueError("request does not declare its original length")
    expected = int(length_match.group(1))

    payload_start = start + len(marker)
    payload_end = body.rindex(b"--" + BOUNDARY + b"--")
    payload = body[payload_start:payload_end]
    header_length = struct.unpack("<I", payload[:4])[0]
    header = payload[4:4 + header_length]
    data = payload[4 + header_length:]
    plaintext = context.unwrap_winrm(header, data)
    if len(plaintext) != expected:
        raise ValueError(f"decrypted {len(plaintext)} bytes, declared {expected}")
    return plaintext


# --- PowerShell out-of-process bridge ------------------------------------------------------------------------------

def pipeline_state_in(base64_fragments: str) -> bool:
    """True if the PSRP fragments carry a PIPELINE_STATE message, the pipeline's last word."""
    data = base64.b64decode(base64_fragments)
    offset = 0
    while offset + 21 <= len(data):
        _object_id, _fragment_id, flags, blob_length = struct.unpack(">QQBI", data[offset:offset + 21])
        blob = data[offset + 21:offset + 21 + blob_length]
        if flags & 0x1 and len(blob) >= 8 and struct.unpack("<I", blob[4:8])[0] == PSRP_PIPELINE_STATE:
            return True
        offset += 21 + blob_length
    return False


class PowerShellProcess:
    """One `pwsh -s` per shell, speaking PowerShell's out-of-process transport on stdin and stdout."""

    PACKET = re.compile(r"<(?P<kind>Data|DataAck|CommandAck|CloseAck|SignalAck)(?P<attrs>[^>]*?)(?:/>|>(?P<body>[^<]*)</Data>)")

    def __init__(self):
        self.process = subprocess.Popen(["pwsh", "-NoLogo", "-NoProfile", "-s"], stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, bufsize=1)
        self.output = {}  # PSGuid (lower case) -> queue of (stream, base64 fragments)
        self.acks = {}  # (kind, PSGuid) -> threading.Event
        self.lock = threading.Lock()
        threading.Thread(target=self._read, daemon=True).start()

    def queue_for(self, guid: str) -> "queue.Queue":
        with self.lock:
            return self.output.setdefault(guid.lower(), queue.Queue())

    def _event(self, kind: str, guid: str) -> threading.Event:
        with self.lock:
            return self.acks.setdefault((kind, guid.lower()), threading.Event())

    def _read(self):
        for line in self.process.stdout:
            for match in self.PACKET.finditer(line):
                attrs = dict(re.findall(r"(\w+)='([^']*)'", match.group("attrs")))
                guid = attrs.get("PSGuid", EMPTY_GUID).lower()
                if match.group("kind") == "Data":
                    self.queue_for(guid).put((attrs.get("Stream", "Default"), (match.group("body") or "").strip()))
                else:
                    self._event(match.group("kind"), guid).set()

    def _write(self, packet: str):
        self.process.stdin.write(packet + "\n")
        self.process.stdin.flush()

    def send_data(self, guid: str, base64_fragments: str, stream: str = "Default"):
        self._write(f"<Data Stream='{stream}' PSGuid='{guid}'>{base64_fragments}</Data>")
        self._event("DataAck", guid).wait(30)
        self._event("DataAck", guid).clear()

    def start_command(self, guid: str):
        self._write(f"<Command PSGuid='{guid}' />")
        if not self._event("CommandAck", guid).wait(30):
            raise TimeoutError("pwsh did not acknowledge the command")

    def signal(self, guid: str):
        self._write(f"<Signal PSGuid='{guid}' />")
        self._event("SignalAck", guid).wait(30)

    def close(self, guid: str):
        self._write(f"<Close PSGuid='{guid}' />")
        self._event("CloseAck", guid).wait(30)

    def kill(self):
        try:
            self.process.kill()
        except OSError:
            pass


class Shell:
    def __init__(self, shell_id: str, resource_uri: str):
        self.shell_id = shell_id
        self.resource_uri = resource_uri
        self.pwsh = PowerShellProcess()
        self.done = set()  # pipelines whose PIPELINE_STATE has been delivered


SHELLS = {}
SHELLS_LOCK = threading.Lock()


# --- WS-Management -------------------------------------------------------------------------------------------------

def soap(action: str, relates_to: str, body: str, header_extra: str = "") -> bytes:
    return (
        f'<s:Envelope xmlns:s="{NS["s"]}" xmlns:a="{NS["a"]}" xmlns:w="{NS["w"]}" xmlns:rsp="{NS["rsp"]}" '
        f'xmlns:x="{NS["x"]}" xmlns:p="{NS["p"]}">'
        f"<s:Header><a:Action>{action}</a:Action><a:MessageID>uuid:{uuid.uuid4()}</a:MessageID>"
        f"<a:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:To>"
        f"<a:RelatesTo>{relates_to}</a:RelatesTo>{header_extra}</s:Header>"
        f"<s:Body>{body}</s:Body></s:Envelope>"
    ).encode("utf-8")


def fault(relates_to: str, code: int, subcode: str, text: str) -> bytes:
    body = (
        f'<s:Fault><s:Code><s:Value>s:Receiver</s:Value><s:Subcode><s:Value>{subcode}</s:Value></s:Subcode></s:Code>'
        f'<s:Reason><s:Text xml:lang="en-US">{text}</s:Text></s:Reason>'
        f'<s:Detail><f:WSManFault xmlns:f="http://schemas.microsoft.com/wbem/wsman/1/wsmanfault" Code="{code}" '
        f'Machine="{SERVICE_HOST}"><f:Message>{text}</f:Message></f:WSManFault></s:Detail></s:Fault>'
    )
    return soap("http://schemas.dmtf.org/wbem/wsman/1/wsman/fault", relates_to, body)


def shell_from(envelope) -> "Shell":
    shell_id = envelope.findtext(".//w:SelectorSet/w:Selector[@Name='ShellId']", namespaces=NS)
    with SHELLS_LOCK:
        shell = SHELLS.get((shell_id or "").upper())
    if shell is None:
        raise LookupError(f"no shell {shell_id}")
    return shell


def handle(envelope_bytes: bytes) -> bytes:
    envelope = ET.fromstring(envelope_bytes)
    action = envelope.findtext("s:Header/a:Action", namespaces=NS)
    message_id = envelope.findtext("s:Header/a:MessageID", namespaces=NS) or ""
    LOG.info("%s", action.rsplit("/", 1)[-1])

    if action == ACTION_CREATE:
        resource_uri = envelope.findtext("s:Header/w:ResourceURI", namespaces=NS)
        shell_element = envelope.find("s:Body/rsp:Shell", NS)
        shell_id = (shell_element.get("ShellId") or str(uuid.uuid4())).upper()
        creation = shell_element.findtext("{http://schemas.microsoft.com/powershell}creationXml") or ""
        shell = Shell(shell_id, resource_uri)
        with SHELLS_LOCK:
            SHELLS[shell_id] = shell
        if creation:
            shell.pwsh.send_data(EMPTY_GUID, creation.strip())
        body = (
            f"<x:ResourceCreated><a:Address>http://{SERVICE_HOST}/wsman</a:Address><a:ReferenceParameters>"
            f"<w:ResourceURI>{resource_uri}</w:ResourceURI><w:SelectorSet><w:Selector Name=\"ShellId\">{shell_id}"
            f"</w:Selector></w:SelectorSet></a:ReferenceParameters></x:ResourceCreated>"
            f"<rsp:Shell><rsp:ShellId>{shell_id}</rsp:ShellId><rsp:ResourceUri>{resource_uri}</rsp:ResourceUri>"
            f"<rsp:InputStreams>stdin pr</rsp:InputStreams><rsp:OutputStreams>stdout</rsp:OutputStreams></rsp:Shell>"
        )
        return soap("http://schemas.xmlsoap.org/ws/2004/09/transfer/CreateResponse", message_id, body)

    if action not in (ACTION_COMMAND, ACTION_SEND, ACTION_RECEIVE, ACTION_SIGNAL, ACTION_DELETE):
        return fault(message_id, 2150858817, "w:ActionNotSupported", f"Unsupported action {action}")

    try:
        shell = shell_from(envelope)
    except LookupError as error:
        return fault(message_id, 2150858843, "w:InvalidSelectors", str(error))

    if action == ACTION_COMMAND:
        command_line = envelope.find("s:Body/rsp:CommandLine", NS)
        command_id = (command_line.get("CommandId") or str(uuid.uuid4())).upper()
        arguments = command_line.findtext("rsp:Arguments", namespaces=NS) or ""
        shell.pwsh.start_command(command_id)
        if arguments:
            shell.pwsh.send_data(command_id, arguments.strip())
        body = f"<rsp:CommandResponse><rsp:CommandId>{command_id}</rsp:CommandId></rsp:CommandResponse>"
        return soap("http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandResponse", message_id, body)

    if action == ACTION_SEND:
        for stream in envelope.findall("s:Body/rsp:Send/rsp:Stream", NS):
            guid = (stream.get("CommandId") or EMPTY_GUID).upper()
            ps_stream = "PromptResponse" if stream.get("Name") == "pr" else "Default"
            shell.pwsh.send_data(guid, (stream.text or "").strip(), ps_stream)
        return soap("http://schemas.microsoft.com/wbem/wsman/1/windows/shell/SendResponse", message_id,
                    "<rsp:SendResponse/>")

    if action == ACTION_RECEIVE:
        desired = envelope.find("s:Body/rsp:Receive/rsp:DesiredStream", NS)
        command_id = desired.get("CommandId") if desired is not None else None
        guid = (command_id or EMPTY_GUID).upper()
        source = shell.pwsh.queue_for(guid)
        try:
            items = [source.get(timeout=RECEIVE_TIMEOUT_SECONDS)]
        except queue.Empty:
            return fault(message_id, 2150858793, "w:TimedOut",
                         "The WS-Management service cannot complete the operation within the time specified in OperationTimeout.")
        while not source.empty():
            items.append(source.get_nowait())

        command_attr = f' CommandId="{command_id.upper()}"' if command_id else ""
        streams = ""
        for ps_stream, fragments in items:
            name = "pr" if ps_stream == "PromptResponse" else "stdout"
            streams += f'<rsp:Stream Name="{name}"{command_attr}>{fragments}</rsp:Stream>'
            if command_id and pipeline_state_in(fragments):
                shell.done.add(guid)
        state = ""
        if command_id:
            state_uri = COMMAND_STATE_DONE if guid in shell.done else COMMAND_STATE_RUNNING
            state = f'<rsp:CommandState CommandId="{command_id.upper()}" State="{state_uri}"/>'
        body = f"<rsp:ReceiveResponse>{streams}{state}</rsp:ReceiveResponse>"
        return soap("http://schemas.microsoft.com/wbem/wsman/1/windows/shell/ReceiveResponse", message_id, body)

    if action == ACTION_SIGNAL:
        signal = envelope.find("s:Body/rsp:Signal", NS)
        command_id = (signal.get("CommandId") or EMPTY_GUID).upper()
        code = signal.findtext("rsp:Code", namespaces=NS) or ""
        if code.endswith("/terminate"):
            shell.pwsh.close(command_id)
        else:
            shell.pwsh.signal(command_id)
        return soap("http://schemas.microsoft.com/wbem/wsman/1/windows/shell/SignalResponse", message_id,
                    "<rsp:SignalResponse/>")

    if action == ACTION_DELETE:
        shell.pwsh.close(EMPTY_GUID)
        shell.pwsh.kill()
        with SHELLS_LOCK:
            SHELLS.pop(shell.shell_id, None)
        return soap("http://schemas.xmlsoap.org/ws/2004/09/transfer/DeleteResponse", message_id, "")

    return fault(message_id, 2150858817, "w:ActionNotSupported", f"Unsupported action {action}")


# --- HTTP ---------------------------------------------------------------------------------------------------------

class WinRMHandler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def setup(self):
        super().setup()
        # Kerberos authentication is per connection, as Windows' HTTP.sys does it.
        self.context = spnego.server(hostname=SERVICE_HOST, service="HTTP", protocol="kerberos")

    def log_message(self, format, *args):
        LOG.debug(format, *args)

    def _reply(self, status: int, body: bytes = b"", content_type: str = "", headers=None):
        self.send_response(status)
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        if content_type:
            self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def do_POST(self):
        if self.path.rstrip("/").lower() not in ("/wsman", "/powershell"):
            self._reply(404)
            return

        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        headers = {}
        authorization = self.headers.get("Authorization", "")
        if authorization.startswith("Kerberos "):
            reply_token = self.context.step(base64.b64decode(authorization.split(" ", 1)[1]))
            if reply_token:
                headers["WWW-Authenticate"] = "Kerberos " + base64.b64encode(reply_token).decode()
            if not self.context.complete:
                self._reply(401, headers=headers)
                return
        elif not self.context.complete:
            self._reply(401, headers={"WWW-Authenticate": "Kerberos"})
            return

        if not body:
            self._reply(200, headers=headers)
            return

        try:
            plaintext = decrypt_message(self.context, body, self.headers.get("Content-Type", ""))
        except (ValueError, spnego.exceptions.SpnegoError) as error:
            LOG.warning("refused request: %s", error)
            self._reply(400, headers=headers)
            return

        reply = handle(plaintext)

        encrypted, content_type = encrypt_message(self.context, reply)
        self._reply(200, encrypted, content_type, headers)


class ThreadingHTTPServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True


def main():
    logging.basicConfig(level=os.environ.get("WINRM_LOG_LEVEL", "INFO"), stream=sys.stdout,
                        format="%(asctime)s %(levelname)s %(message)s")
    servers = [ThreadingHTTPServer(("0.0.0.0", 5985), WinRMHandler)]
    certificate = os.environ.get("WINRM_TLS_CERTIFICATE")
    if certificate:
        tls = ThreadingHTTPServer(("0.0.0.0", 5986), WinRMHandler)
        context = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.load_cert_chain(certificate, os.environ["WINRM_TLS_KEY"])
        tls.socket = context.wrap_socket(tls.socket, server_side=True)
        servers.append(tls)
    for server in servers[1:]:
        threading.Thread(target=server.serve_forever, daemon=True).start()
    LOG.info("Stand-in WinRM server for HTTP/%s listening on 5985%s", SERVICE_HOST, " and 5986" if certificate else "")
    servers[0].serve_forever()


if __name__ == "__main__":
    main()
