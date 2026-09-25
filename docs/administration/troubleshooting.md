---
title: Troubleshooting
---

# Troubleshooting

!!! info "Documentation In Progress"
    This page is under active development. Content will be added in a future update.

<!-- TODO: Common issues and resolutions for deployment, authentication, synchronisation, and connectivity problems -->

## Start-up

### A service logs `The database is not reachable yet`

At start-up, `jim.worker`, `jim.web` and `jim.scheduler` each wait for PostgreSQL before doing anything else, and log every attempt with the time spent so far and the reason:

```
The database is not reachable yet (attempt 5, 18s elapsed): jim.database:5432: Name or service not known. Retrying in 15s.
```

**What it means.** The service cannot reach PostgreSQL at the address named in the line (`JIM_DB_HOSTNAME`). A few of these lines are normal while the bundled PostgreSQL container starts, or while an external database server restarts: the service carries on as soon as PostgreSQL answers, logging `Connected to the database after 8 attempts (63s).`, and nothing is lost while it waits.

**How to fix, if it keeps waiting.**

1. Check that PostgreSQL is running: for the bundled database, `jim.database` should be listed as running in `docker compose ps` (remember the `--profile with-db` flag); for an external server, ask whoever runs it.
2. Check that `JIM_DB_HOSTNAME` in `.env` names that server, with `:port` appended if it does not listen on 5432 (see [Configuration](configuration.md)). `Name or service not known` means the name does not resolve; `Connection refused` means nothing is listening at that address and port.
3. For an external server, check that a firewall allows the JIM host to reach it.

A service waits for five minutes. Then it logs one final line, `The database was not reachable within 300s (…); stopping so that the service is restarted`, and stops with exit code 1; Docker's restart policy starts it again, which begins a fresh five-minute wait.

### A service stops with `password authentication failed`

Waiting cannot fix rejected credentials, so the service does not wait for them: it stops straight away, with exit code 1 and PostgreSQL's own error, for example `28P01: password authentication failed for user "jim"`. Check `JIM_DB_USERNAME` and `JIM_DB_PASSWORD` in `.env` against the database server, then start JIM again.

## HTTPS

### The browser shows an empty response at `http://<server>:5200`

The browser reports that the page sent no data, or that the connection was reset.

**What it means.** JIM serves HTTPS on that port, and does not answer plain HTTP there.

**How to fix.** Open `https://<server>:5200`.

### `docker compose up` fails with `bind source path does not exist: .../tls/tls.crt`

Docker Compose first warns that a `secret file ... does not exist`, then stops without starting `jim.web`.

**What it means.** JIM's certificate or key is missing from the `tls` folder next to the compose files. Nothing has changed in your installation; `jim.web` has simply not been started.

**How to fix.** Put the certificate at `tls/tls.crt` and its key at `tls/tls.key`, or let `setup.sh` create them; see [The certificate](deployment.md#the-certificate). Then run your `docker compose ... up -d` command again.

### `jim.web` keeps restarting with `Access to the path '/run/secrets/jim-tls/tls.key' is denied`

The `jim.web` log shows `System.UnauthorizedAccessException: Access to the path '/run/secrets/jim-tls/tls.key' is denied`, and the container restarts again and again.

**What it means.** JIM runs as UID `1654` with every capability dropped, and Docker mounts the key with the owner and mode it has on the host, so JIM cannot read a key that belongs to anyone else. The other services, and your data, are unaffected.

**How to fix.** In the folder holding the compose files, as root:

```bash
chown 1654:1654 tls/tls.key && chmod 400 tls/tls.key
docker compose -f docker-compose.yml -f docker-compose.production.yml restart jim.web
```

### Signing in through nginx fails with `502 Bad Gateway`

Sign-in reaches your identity provider, but the return to JIM (`/signin-oidc`) shows nginx's `502 Bad Gateway` page, and nginx's error log records `upstream sent too big header while reading response header from upstream`.

**What it means.** JIM's response to a sign-in sets cookies larger than nginx's default buffer for a response's headers, so nginx gives up on it. Nothing is wrong with JIM or your identity provider.

**How to fix.** Add these to the nginx `location` that proxies JIM, as the [nginx example](deployment.md#nginx-example) does, then reload nginx:

```nginx
proxy_buffer_size 16k;
proxy_buffers 8 16k;
```

## Authentication

### Sign-in loops between JIM and the identity provider

You open JIM, sign in at your identity provider, and are sent back to the identity provider again and again; the portal never loads. Each time round, the `jim.web` log records a warning like this:

```
'.AspNetCore.Correlation.<random characters>' cookie not found.
```

and the [security audit log](security-audit-events.md) records a failed sign-in with the reason `OIDC correlation failed`.

**What it means.** Your browser is reaching JIM over plain HTTP at an address other than `localhost`. JIM's sign-in cookies are HTTPS-only in a production deployment, so the browser discards them; without them, JIM cannot match the identity provider's response to the sign-in it started, so it starts a new one. A standard installation serves HTTPS only, so this happens when JIM's plain HTTP port has been published to other machines, typically by a compose override of your own, or by starting JIM without `docker-compose.production.yml`. Safari can do the same even at `http://localhost`. [TLS and Reverse Proxy](deployment.md#tls-and-reverse-proxy) explains the requirement.

**How to fix.**

1. Start JIM with `docker-compose.production.yml`, which serves HTTPS, and remove any override that publishes port `8080` beyond the host's loopback interface.
2. Register JIM's `https://` sign-in and sign-out callback URLs at your identity provider, and remove any `http://` ones you added (see the [SSO Setup Guide](sso-setup.md)).
3. Open JIM at its `https://` address.

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
