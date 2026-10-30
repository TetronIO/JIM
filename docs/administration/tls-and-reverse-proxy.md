---
title: TLS and Reverse Proxy
description: Give JIM its HTTPS certificate, renew or replace it, and run JIM behind a reverse proxy or load balancer.
type: reference
audience: operator
validated: v0.17.0
---

# TLS and Reverse Proxy

JIM serves HTTPS itself, on both runtimes. In production, `jim.web` listens for HTTPS on container port `8443`, which the installation publishes on the standard HTTPS port, 443, so a fresh install can be signed into from any machine at `https://jim.example.com` with no reverse proxy.

```jim-diagram
archetype: comparison
title: Where TLS ends in each supported setup
desc: "Three setups are supported. With no proxy, the default, browsers connect over HTTPS straight to JIM, which terminates TLS itself; its certificate names every name users reach it at. With a re-encrypting proxy, the recommended way to use one, the proxy terminates the browser's HTTPS and opens its own HTTPS connection to JIM, checking JIM's certificate against a CA it trusts, so traffic is encrypted end to end; JIM's certificate carries both the name users type and the name the proxy connects to. With plain HTTP to JIM, a proxy on the JIM host terminates TLS and forwards plain HTTP to JIM on the host's loopback interface: that hop is unencrypted but never leaves the host, and JIM needs no certificate. With no proxy, JIM_TRUSTED_PROXIES is not needed. With either proxy, set it to the address the proxy connects from, which for a proxy on the JIM host is the gateway address of JIM's container network; with plain HTTP, sign-in fails without it. JIM accepts TLS 1.2 and 1.3."
columns:
  - { label: No proxy, sub: the default }
  - { label: Re-encrypting proxy, sub: recommended }
  - { label: Plain HTTP to JIM, sub: proxy on the JIM host }
rows:
  - label: Browser to the server
    cells:
      - { text: "HTTPS to JIM", sub: JIM terminates TLS }
      - { text: "HTTPS to the proxy", sub: the proxy terminates TLS }
      - { text: "HTTPS to the proxy", sub: the proxy terminates TLS }
  - label: Proxy to JIM
    cells:
      - { text: no second hop }
      - { text: "HTTPS", sub: "checked against a CA the proxy trusts" }
      - { text: plain HTTP, sub: "loopback only, never leaves the host" }
  - label: "JIM's certificate"
    cells:
      - { text: every name users reach JIM at }
      - { text: both names, sub: "the one users type, and the proxy's" }
      - { text: not needed }
  - label: Trusted proxies
    cells:
      - { text: not needed }
      - { text: "the address the proxy connects from" }
      - { text: "the network gateway's address", sub: sign-in fails without it }
footer: "Trusted proxies is the setting `JIM_TRUSTED_PROXIES`; JIM accepts TLS 1.2 and 1.3"
```

!!! warning "Browsers on other machines must use HTTPS"
    JIM's sign-in cookies are HTTPS-only in a production deployment, and browsers discard HTTPS-only cookies sent over plain HTTP to any address other than `localhost`. Over plain HTTP from another machine, sign-in never completes: JIM stops it on a **Sign-in could not complete** page rather than send the browser back to your identity provider again (see [Troubleshooting](troubleshooting.md#sign-in-loops-between-jim-and-the-identity-provider)).

## :material-certificate-outline: The Certificate

JIM reads its certificate and private key from two PEM files in the `tls` folder of the installation (`/opt/jim/tls` when the installer ran as root):

| File          | Contents                                                                                              |
|---------------|-------------------------------------------------------------------------------------------------------|
| `tls/tls.crt` | JIM's certificate, followed by any intermediate CA certificates, so that clients receive the whole chain |
| `tls/tls.key` | Its private key, **unencrypted**, belonging to UID `1654` (the user JIM runs as) with mode `400`      |

The certificate must name, as subject alternative names, every DNS name and IP address users and tools reach JIM at, including the name a reverse proxy uses to connect to it. JIM accepts TLS 1.2 and 1.3.

Docker mounts both files into `jim.web` read-only, keeping their owner and mode, and JIM runs with every capability dropped: if the key does not belong to UID `1654`, `jim.web` cannot read it and fails to start. Until both files exist, `docker compose up` stops with an error naming the missing file.

Podman reads the pair from the Podman secret `jim-tls` instead, which the installer stores from these files, so on Podman the key stays readable by root only and needs no change of owner. Installing by hand, store it as [Installing on Podman by Hand](podman-by-hand.md) describes.

The installer asks which of these two ways to provide them, and puts them in place:

- **Your organisation's certificate** (recommended for production)<br /> Issued by your internal certificate authority (CA), such as Active Directory Certificate Services or Identity Management, which your browsers and servers already trust. Nothing needs distributing.
- **A certificate the installer creates**<br /> `setup.sh` creates a certificate authority for this JIM server, and a server certificate from it, for the names and addresses you give it. Browsers show a warning until the CA is trusted; see [Distributing the certificate authority](#distributing-the-certificate-authority).

When installing by hand, put your organisation's certificate in place as root:

```bash
mkdir -p /opt/jim/tls && chmod 700 /opt/jim/tls
cp /path/to/jim.crt /opt/jim/tls/tls.crt   # the certificate, followed by any intermediate CA certificates
cp /path/to/jim.key /opt/jim/tls/tls.key   # its unencrypted private key
chown 1654:1654 /opt/jim/tls/tls.key && chmod 400 /opt/jim/tls/tls.key
```

If your key is encrypted, decrypt it first with `openssl pkey -in encrypted.key -out /opt/jim/tls/tls.key`.

## :material-file-certificate-outline: A Certificate the Installer Creates

`setup.sh` writes these files to `tls/`:

| File      | What it is                                                         | Keep it          |
|-----------|--------------------------------------------------------------------|------------------|
| `ca.crt`  | The certificate authority's certificate, valid for ten years       | Distribute it    |
| `ca.key`  | The certificate authority's private key; it signs JIM's certificates | Secret, readable by root only |
| `tls.crt` | JIM's certificate, valid for one year                              |                  |
| `tls.key` | JIM's private key                                                  | Secret           |
| `names`   | The names and addresses the certificate covers, for renewal        |                  |

The certificate authority is limited, by name constraints, to exactly the names and addresses you gave. A browser that trusts it accepts a certificate it signs for JIM, and rejects one for any other site, so even a stolen `ca.key` cannot be used to impersonate other servers to the machines that trust it.

When installing by hand, create these files with the installer's certificate step alone, from a downloaded `setup.sh` or the one in the release bundle:

```bash
sudo JIM_INSTALL_DIR=/opt/jim ./setup.sh --certificate
```

### Distributing the Certificate Authority

Add `ca.crt` to the trusted root certificate authorities of every machine whose browser or tools use JIM, and of any reverse proxy that connects to JIM over HTTPS:

- **Windows**<br /> Group Policy (Computer Configuration > Policies > Windows Settings > Security Settings > Public Key Policies > Trusted Root Certification Authorities), or `Import-Certificate -FilePath ca.crt -CertStoreLocation Cert:\LocalMachine\Root` on one machine.
- **Red Hat Enterprise Linux and derivatives**<br /> Copy it to `/etc/pki/ca-trust/source/anchors/` and run `update-ca-trust`.
- **Debian and Ubuntu**<br /> Copy it to `/usr/local/share/ca-certificates/jim-ca.crt` and run `update-ca-certificates`.
- **macOS**<br /> `sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain ca.crt`.

Depending on its configuration, Firefox may use its own list rather than the operating system's. If it still warns, import the CA under **Settings > Privacy & Security > Certificates > View Certificates > Authorities**.

The PowerShell module relies on the operating system's list, so machines that run `Connect-JIM` need the CA too.

## :material-update: Renewing the Certificate

JIM sends browsers an HSTS header, which tells them to refuse plain HTTP and certificate errors for JIM's name. Once a browser trusts JIM's certificate, an **expired** certificate therefore blocks access outright, with no option to continue. Renew before the expiry date, which the installer prints at install; to read it later:

```bash
openssl x509 -in /opt/jim/tls/tls.crt -noout -enddate
```

- **A certificate the installer created**<br /> Run the installation's copy of the installer. It issues a new certificate from the same certificate authority, for the same names, and restarts `jim.web` (on Podman, the whole JIM pod, so do it outside a synchronisation run); browsers that trust the CA carry on without a warning.

    ```bash
    sudo /opt/jim/setup.sh --renew-certificate
    ```

- **Your organisation's certificate**<br /> Install the renewed one as in [Replacing the certificate](#replacing-the-certificate).

## :material-swap-horizontal: Replacing the Certificate

To install a renewed certificate from your CA, move to your organisation's certificate, or change the names a created certificate covers, run the installer's certificate step:

```bash
sudo /opt/jim/setup.sh --certificate
```

It asks the same questions as at install, checks the certificate and key before installing them, and restarts `jim.web` (on Podman, the whole JIM pod). A created certificate for new names needs a new certificate authority, which you distribute again; the installer says so when it creates one.

To do the same by hand on Docker, as root:

```bash
cd /opt/jim
cp /path/to/new.crt tls/tls.crt
cp /path/to/new.key tls/tls.key.new && chmod 400 tls/tls.key.new && chown 1654:1654 tls/tls.key.new
mv -f tls/tls.key.new tls/tls.key
docker compose -f docker-compose.yml -f docker-compose.production.yml restart jim.web
```

On Podman, put the new files in `tls/` the same way (no `chown` needed), store them as the `jim-tls` secret again as in [Installing on Podman by Hand](podman-by-hand.md), and restart `jim.service`.

When you move away from a certificate the installer created, delete `tls/ca.key`, and remove the JIM certificate authority from the machines that trusted it.

## :material-server-network-outline: Behind a Reverse Proxy or Load Balancer

JIM does not need a reverse proxy, but you may already route web applications through one, or want JIM on port 443 behind a load balancer. Two configurations are supported:

- **Re-encrypting to JIM** (recommended)<br /> The proxy terminates the browser's HTTPS connection and opens its own HTTPS connection to JIM, checking JIM's certificate. Traffic is encrypted end to end, which regulated environments commonly require, and nothing about JIM changes. The proxy must trust the CA that issued JIM's certificate, and JIM's certificate must carry both the name users type and the name the proxy connects to: nginx checks JIM's certificate against the host name in its `proxy_pass` line, while Apache httpd, which passes the name users typed on to JIM, checks it against that.
- **Plain HTTP on the JIM host's loopback interface**<br /> A proxy on the JIM host itself terminates TLS and forwards plain HTTP to JIM, which listens on the host's loopback interface only. See [Plain HTTP for a proxy on the same host](#plain-http-for-a-proxy-on-the-same-host).

Either way, set `JIM_TRUSTED_PROXIES` so that JIM records each client's real address; see [Trusting the Reverse Proxy](#trusting-the-reverse-proxy).

!!! important "Pass WebSocket connections through"
    The JIM portal keeps a live connection to the server over WebSockets. Your reverse proxy **must** pass WebSocket connections through (the `Upgrade` and `Connection` headers in the examples below). Without them the portal falls back to a slower connection method and responds sluggishly, and the browser's developer console reports `Failed to connect via WebSockets, using the Long Polling fallback transport`.

### nginx Example

Users reach `jim.example.com`; the proxy connects to JIM at `jim-app.example.com`, and trusts the CA in `/etc/nginx/jim-ca.crt` (JIM's `tls/ca.crt`, or your organisation's CA certificate).

```nginx
server {
    listen 443 ssl http2;
    server_name jim.example.com;

    ssl_certificate     /etc/nginx/ssl/jim.example.com.crt;
    ssl_certificate_key /etc/nginx/ssl/jim.example.com.key;
    ssl_protocols       TLSv1.2 TLSv1.3;

    location / {
        # Re-encrypt to JIM, checking its certificate
        proxy_pass https://jim-app.example.com;
        proxy_ssl_verify on;
        proxy_ssl_trusted_certificate /etc/nginx/jim-ca.crt;
        proxy_ssl_server_name on;

        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # JIM's sign-in response sets cookies larger than nginx's default buffer;
        # without these, signing in fails with 502 Bad Gateway
        proxy_buffer_size 16k;
        proxy_buffers 8 16k;

        # WebSocket support (required for the JIM portal)
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
```

### Apache httpd Example

Apache httpd is the default web server on Red Hat Enterprise Linux. This example needs `mod_ssl`, `mod_proxy`, `mod_proxy_http`, `mod_proxy_wstunnel`, `mod_headers` and `mod_rewrite`. Because `ProxyPreserveHost` passes on the name users typed, Apache checks JIM's certificate against `jim.example.com`, not `jim-app.example.com`: if JIM's certificate lacks it, every request fails with `500 Internal Server Error` and Apache logs `AH02411: SSL Proxy: Peer certificate does not match for hostname`.

```apache
<VirtualHost *:443>
    ServerName jim.example.com

    SSLEngine on
    SSLCertificateFile    /etc/pki/tls/certs/jim.example.com.crt
    SSLCertificateKeyFile /etc/pki/tls/private/jim.example.com.key
    SSLProtocol           -all +TLSv1.2 +TLSv1.3

    # Re-encrypt to JIM, checking its certificate
    SSLProxyEngine on
    SSLProxyVerify require
    SSLProxyCheckPeerName on
    SSLProxyCACertificateFile /etc/pki/tls/certs/jim-ca.crt

    ProxyPreserveHost On
    RequestHeader set X-Forwarded-Proto "https"

    # WebSocket support (required for the JIM portal)
    RewriteEngine on
    RewriteCond %{HTTP:Upgrade} =websocket [NC]
    RewriteRule ^/(.*) wss://jim-app.example.com/$1 [P,L]

    ProxyPass        / https://jim-app.example.com/
    ProxyPassReverse / https://jim-app.example.com/
</VirtualHost>
```

On a host with SELinux enforcing, as Red Hat Enterprise Linux is by default, httpd may not open network connections until you allow it; without this, every request fails with `503 Service Unavailable` and the audit log records a `name_connect` denial:

```bash
setsebool -P httpd_can_network_connect 1
```

## :material-shield-check-outline: Trusting the Reverse Proxy

Set `JIM_TRUSTED_PROXIES` (in `.env` on Docker, `jim-config.yaml` on Podman) to the address the proxy connects to JIM from, so that JIM reads each client's real IP address, and the original scheme, from the proxy's `X-Forwarded-For` and `X-Forwarded-Proto` headers. Until you do:

- The security audit log and API rate limiting see the proxy's address instead of each client's, so every client shares one rate limit.
- **With plain HTTP to JIM only:** sign-in fails, because JIM sends your identity provider an `http://` callback address that does not match the `https://` one you registered, and JIM refuses every REST API request that carries a password, and every request to its SCIM endpoints, because it cannot confirm the connection is encrypted.

The address to trust depends on where the proxy runs:

- **Proxy on another machine**<br /> That machine's IP address.
- **Proxy on the JIM host**<br /> The proxy takes port 443, so JIM moves to a port on the host's loopback interface: `JIM_WEB_PORT=127.0.0.1:8443` for a proxy that re-encrypts to `https://localhost:8443` (see [Port Mapping](#port-mapping)), or the plain HTTP setup [below](#plain-http-for-a-proxy-on-the-same-host). Either way, JIM sees the connection arrive from the gateway address of the `jim-network` Docker network, not from `127.0.0.1`. Find it with:

    ```bash
    docker network inspect jim-network --format '{{range .IPAM.Config}}{{.Gateway}}{{end}}'
    ```

    Docker assigns this address when it creates the network, so check it again after anything that removes and recreates the network, such as `docker compose down`. On Podman, a rootful JIM sees the gateway of the `jim` network (`podman network inspect jim`); a rootless JIM sees every client at one internal address, so do not run a rootless JIM behind a proxy that needs `JIM_TRUSTED_PROXIES` (see [Rootful or Rootless](podman.md#rootful-or-rootless)).

For example:

```bash
JIM_TRUSTED_PROXIES=10.0.0.5
```

Then run your `docker compose ... up -d` command again so `jim.web` restarts with the setting. On startup `jim.web` logs `Trusting forwarded headers from 1 known proxy address(es) and 0 known network(s)`, which confirms it read the value. List only your proxy: JIM believes whatever a trusted address tells it about the client's address and scheme. See the [Configuration Reference](configuration.md#reverse-proxy) for the setting's full format.

## :material-lock-open-outline: Plain HTTP for a Proxy on the Same Host

If a reverse proxy on the JIM host terminates TLS, JIM can serve it plain HTTP on the host's loopback interface instead, with no certificate of its own. Create `docker-compose.local-proxy.yml` next to the other compose files:

```yaml
# JIM serves plain HTTP on this host's loopback interface only, for a reverse proxy on this host.
services:
  jim.web:
    environment:
      - ASPNETCORE_URLS=http://+:8080
      # Empty, so that JIM does not look for a certificate
      - ASPNETCORE_Kestrel__Certificates__Default__Path=
      - ASPNETCORE_Kestrel__Certificates__Default__KeyPath=
    ports: !override
      - "127.0.0.1:8080:8080"
    secrets: !reset []

secrets: !reset {}
```

Add `-f docker-compose.local-proxy.yml` after the other two files in every `docker compose` command for this deployment, point the proxy at `http://localhost:8080`, and set [`JIM_TRUSTED_PROXIES`](#trusting-the-reverse-proxy) to the `jim-network` gateway address. The `tls` folder is not needed. Traffic between the proxy and JIM is unencrypted, but it never leaves the host: other machines cannot reach port `8080`.

## :material-lan-connect: Port Mapping

`jim.web` listens for HTTPS on port `8443` inside its container. The installation publishes it on the standard HTTPS port, 443, on every interface, so JIM's address needs no port. The installer asks for the port. To change it afterwards on Podman, edit `PublishPort=` in `jim.kube` (for example `PublishPort=8443:8443`), then reload systemd and restart `jim.service`; on Docker, set `JIM_WEB_PORT` in `.env` and run your `docker compose ... up -d` command again:

```bash
JIM_WEB_PORT=8443
```

JIM's address then carries the port (`https://jim.example.com:8443`), and so must the redirect URIs registered at your identity provider.

To accept connections on one interface only, such as the loopback interface behind a reverse proxy on the same host, prefix its address:

```bash
JIM_WEB_PORT=127.0.0.1:8443
```

The base `docker-compose.yml` publishes no ports, so a deployment that leaves out `docker-compose.production.yml` is not reachable from the host.

!!! note "Use `JIM_WEB_PORT` rather than a `ports` override"
    Docker Compose combines port mappings from every file, so a `ports` entry in an override of your own adds a second mapping instead of replacing the default one, unless it is marked `!override` as in [Plain HTTP for a proxy on the same host](#plain-http-for-a-proxy-on-the-same-host).

!!! note "Rootless Docker or Podman and ports below 1024"
    A rootless container engine cannot publish a port below the kernel's unprivileged-port threshold (1024 by default), which includes 443. For rootless Podman, the installer lowers the threshold to the port you choose; for rootless Docker, it gives the command that does. Alternatively choose a port of 1024 or above.
