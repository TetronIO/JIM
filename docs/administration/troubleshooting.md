---
title: Troubleshooting
---

# Troubleshooting

!!! info "Documentation In Progress"
    This page is under active development. Content will be added in a future update.

<!-- TODO: Common issues and resolutions for deployment, authentication, synchronisation, and connectivity problems -->

## Authentication

### `Invalid parameter: redirect_uri` when running `Connect-JIM` interactively

You clicked **Login** (or let `Connect-JIM` open your browser) and the identity provider returned an error page saying `Invalid parameter: redirect_uri` (Keycloak), `AADSTS50011` (Entra ID), or a similar redirect-mismatch message.

**What it means.** The PowerShell module is trying to authenticate as the identity provider client whose ID came from JIM's `/api/v1/auth/config` endpoint, but that client does not have a loopback redirect URI (`http://localhost:8400/callback/` through `http://localhost:8409/callback/`) registered. This typically happens because JIM is advertising the web application's client ID (a confidential client), but the PowerShell module needs a public client configured for PKCE/loopback.

**How to fix.**

1. Confirm your identity provider has a public client registered with at least the redirect URI `http://localhost:8400/callback/`. The per-provider guides cover this: [Entra ID Step 5b](sso-setup/microsoft-entra-id.md#step-5b-configure-powershell-module-authentication-optional-but-recommended), [AD FS Step 7](sso-setup/ad-fs.md#step-7-configure-powershell-module-authentication-optional-but-recommended), [Keycloak Step 6a](sso-setup/keycloak.md#step-6a-configure-powershell-module-authentication-recommended).
2. If the public client is a **separate registration** from the web application (always the case for Keycloak), set `JIM_SSO_PUBLIC_CLIENT_ID` on the JIM server to the public client's client ID and restart JIM.
3. If the public client **shares a registration** with the web application (Entra ID or AD FS with both platforms added to one app), you do not need to set `JIM_SSO_PUBLIC_CLIENT_ID`; just ensure the web app registration has the loopback redirect URI added under its native/mobile platform.

Verify the config the server is advertising:

```bash
curl https://your-jim-url/api/v1/auth/config
```

The `clientId` in the response must be the client that has the loopback redirect URI registered at your identity provider.

### Sign-in loops between JIM and the identity provider

You sign in, the browser goes back and forth between JIM and the identity provider, and you land on a page titled **Sign-in could not complete**. JIM also logs a warning beginning `Sign-in stopped`. (Earlier versions of JIM kept going back and forth indefinitely.)

**What it means.** When sign-in starts, JIM sets two short-lived cookies and checks for them when the identity provider sends the browser back. Outside Development mode these cookies are marked `Secure`, and browsers only accept `Secure` cookies over HTTPS, or over plain HTTP to `localhost`. When the cookies do not come back, JIM restarts the sign-in once, which recovers a one-off loss (cookies cleared mid-sign-in, or the Back button onto the sign-in callback). If the cookies are lost again, restarting cannot help, so JIM stops and shows this page instead.

**How to fix.**

- **JIM is reached over plain HTTP from another machine**, for example `http://jim01:5200`. This is the usual cause, and JIM stops straight away without restarting. Browser access from other machines requires HTTPS: put a TLS-terminating reverse proxy in front of JIM, as described in [TLS and Reverse Proxy](deployment.md#tls-and-reverse-proxy). Plain HTTP only works from a browser on the JIM host itself, at `http://localhost:5200`.
- **TLS terminates at a reverse proxy that JIM does not trust.** The address bar shows `https://`, but JIM only sees the proxy's plain HTTP connection. Set `JIM_TRUSTED_PROXIES` to the proxy's address so JIM reads the `X-Forwarded-Proto` header the proxy sends; see the [Configuration Reference](configuration.md).
- **The browser is blocking cookies for JIM's address**, or a proxy is removing `Set-Cookie` or `Cookie` headers. Allow cookies for the JIM site and make sure the proxy passes them through unchanged.
- **Safari over `http://localhost` outside Development mode.** Unlike other browsers, Safari does not treat `localhost` as secure, so it refuses the cookies. Use HTTPS, or another browser for local testing.

## Known noisy log lines

Some messages in the container logs look alarming but are expected and harmless. They are documented here so operators can confirm at a glance that nothing is actually wrong.

### `jim.scheduler`: `Cannot load library libgssapi_krb5.so.2`

During startup you may see:

```
jim.scheduler  | Cannot load library libgssapi_krb5.so.2
jim.scheduler  | Error: libgssapi_krb5.so.2: cannot open shared object file: No such file or directory
```

**What it means.** The PostgreSQL client library (Npgsql) probes for the GSS/Kerberos authentication library on startup to advertise that capability to the server. The scheduler image deliberately does not include it.

**Why it is safe.** The scheduler authenticates to PostgreSQL using password credentials and does not talk to any LDAP or Kerberos-backed service. The missing library is never used.

**Why we do not install it.** Adding `libgssapi-krb5-2` to the scheduler image would silence the message, but it would also add an unused dependency to the container's attack surface. JIM is designed for high-assurance environments; we keep images minimal and only install libraries that are actually needed at runtime.

**How to confirm the scheduler is healthy despite the message.** Immediately after the probe lines you should see:

```
jim.scheduler  | info: Microsoft.Hosting.Lifetime[0]
jim.scheduler  |       Application started. Press Ctrl+C to shut down.
```

The container should also transition to `healthy` in `docker ps`.
