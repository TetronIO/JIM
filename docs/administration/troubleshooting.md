---
title: Troubleshooting
description: Fix a JIM installation that will not start, serve HTTPS or let you sign in, by the message it shows.
type: troubleshooting
audience: operator
validated: v0.16.0
---

# Troubleshooting

This page covers installing, starting and signing in to JIM. For a synchronisation that goes wrong (a run that ends with a warning or an error, or an object that will not join, import or export), see [Troubleshooting Synchronisation](troubleshooting-synchronisation.md).

```jim-diagram
archetype: decision
title: A service that waits for the database, diagnosed
desc: "When a service logs The database is not reachable yet, first see whether it connects in the end: if it logs Connected to the database, the wait was normal at start-up and nothing was lost. If it keeps waiting, check that PostgreSQL is running; if it is not, start it, or for an external server ask whoever runs it. If it is running, read the reason the line gives. Name or service not known means the name does not resolve: check JIM_DB_HOSTNAME. Connection refused means nothing is listening at that address and port: check the host name and its port. No route to host means nothing answered or something refused the connection on the way: check the firewall or, on Podman, JIM's network."
start: {label: Database not reachable yet}
questions:
- {id: connects, label: 'Connects in the end?', sub: '`Connected to the database` logged'}
- {id: running, label: 'Is PostgreSQL running?'}
- {id: reason, label: 'What reason does the line give?'}
outcomes:
- {id: normal, label: Normal at start-up, sub: nothing lost while it waited, good: true}
- {id: start, label: Start PostgreSQL, sub: 'external: ask whoever runs it'}
- {id: name, label: Name does not resolve, sub: 'check `JIM_DB_HOSTNAME`'}
- {id: port, label: Nothing listening there, sub: 'check the host and `:port`'}
- {id: route, label: Nothing answered, sub: 'firewall, or Podman''s network'}
answers:
- {from: connects, to: normal, label: 'yes'}
- {from: connects, to: running, label: 'no, it keeps waiting'}
- {from: running, to: start, label: 'no'}
- {from: running, to: reason, label: 'yes'}
- {from: reason, to: name, label: '`Name or service not known`'}
- {from: reason, to: port, label: '`Connection refused`'}
- {from: reason, to: route, label: '`No route to host`'}
```

## :material-rocket-launch-outline: Start-up

### A service logs `The database is not reachable yet`

At start-up, `jim.worker`, `jim.web` and `jim.scheduler` each wait for PostgreSQL before doing anything else, and log every attempt with the time spent so far and the reason:

```text
The database is not reachable yet (attempt 5, 18s elapsed): jim.database:5432: Name or service not known. Retrying in 15s.
```

Where the name resolves, the line also gives the address it led to on that attempt, such as `jim-database:5432 (10.89.1.14): No route to host`.

**What it means.** The service cannot reach PostgreSQL at the server named in the line (`JIM_DB_HOSTNAME`). A few of these lines are normal while the bundled PostgreSQL container starts, or while an external database server restarts: the service carries on as soon as PostgreSQL answers, logging `Connected to the database after 8 attempts (63s).`, and nothing is lost while it waits.

**How to fix, if it keeps waiting.**

1. Check that PostgreSQL is running: for the bundled database, `jim.database` should be listed as running in `docker compose ps` (remember the `--profile with-db` flag), or on Podman `jim-database-postgres` in `sudo podman ps` (see [Operating JIM](podman.md#operating-jim)); for an external server, ask whoever runs it.
2. Check that `JIM_DB_HOSTNAME` in `.env` (on Podman, `jim-config.yaml`) names that server, with `:port` appended if it does not listen on 5432 (see [Configuration](configuration.md)). `Name or service not known` means the name does not resolve; `Connection refused` means nothing is listening at that address and port. For the bundled database, compare the address in the line with the database's own (`sudo podman inspect jim-database-postgres --format '{{.NetworkSettings.Networks.jim.IPAddress}}'` on Podman): a different address means the name still points at a database that has since moved.
3. `No route to host` means nothing answered for the address, or something on the way refused the connection. For an external server, check that a firewall allows the JIM host to reach it. For the bundled database on rootful Podman, check that the database is still plugged into JIM's network: `sudo bridge link` lists one interface per pod on the network's bridge (`sudo podman network inspect jim` names the bridge, such as `podman1`), so a JIM and its database show two. With one missing, and `nmcli device` showing NetworkManager connecting an interface named `veth…`, NetworkManager unplugged the database: tell it to leave Podman's interfaces alone (see [Firewall, SELinux, AppArmor and NetworkManager](podman.md#firewall-selinux-and-apparmor)), then restart JIM and its database, as step 4 shows.
4. On a rootful Podman host running firewalld, a firewalld reload also removes the rules Podman adds for JIM's network, unless `netavark-firewalld-reload.service` is enabled to put them back; JIM's services then log `No route to host` or, where they can no longer resolve names on their network, `Resource temporarily unavailable`. Restart JIM and its database to bring them back: `sudo systemctl stop jim.service jim-database.service && sudo systemctl start jim-database.service jim.service`.

A service waits for five minutes. Then it logs one final line, `The database was not reachable within 300s (…); stopping so that the service is restarted`, and stops with exit code 1; Docker's restart policy, or Podman's, starts it again, which begins a fresh five-minute wait. On Podman the restarted service keeps its pod's network, so a fault in that network outlasts the restart: restart JIM and its database, as step 4 shows.

### The bundled database restarts with `could not map anonymous shared memory`

The bundled PostgreSQL asks for more memory than the host can give, so it stops as it starts, and JIM never becomes ready. Its log (`docker compose ... --profile with-db logs jim.database`, or on Podman `podman logs jim-database-postgres`) says:

```text
FATAL:  could not map anonymous shared memory: Cannot allocate memory
```

Size the database's memory for the host, as [Bundled PostgreSQL Memory](configuration.md#bundled-postgresql-memory) describes, and restart it. Releases up to v0.16.0 do not read those settings, and asked for 8 GB of `shared_buffers`, which a host with less than about 10 GB of memory cannot give: upgrade, or until then override the database's `command` and `shm_size` in a compose file of your own.

### A service stops with `password authentication failed`

Waiting cannot fix rejected credentials, so the service does not wait for them: it stops straight away, with exit code 1 and PostgreSQL's own error, for example `28P01: password authentication failed for user "jim"`. Check `JIM_DB_USERNAME` and `JIM_DB_PASSWORD` in `.env` against the database server, then start JIM again.

### The worker stops with `permission denied for schema public`

On its first start against your own PostgreSQL server, JIM never becomes ready. On Docker, the installer stops with `dependency failed to start: container jim.worker is unhealthy`. The worker's log (`docker logs jim.worker`, or on Podman `sudo podman logs jim-worker`) shows PostgreSQL's error each time the worker starts:

```text
42501: permission denied for schema public
```

**What it means.** JIM's user cannot create JIM's tables in its database. Since PostgreSQL 15, only a database's owner can create tables in its `public` schema, and `GRANT ALL PRIVILEGES ON DATABASE`, which is all that earlier versions of [Before You Install](deployment.md#before-you-install) gave JIM's user, does not allow it. JIM stops before creating anything, so there is nothing to clean up.

**How to fix.** Connected to the server as a superuser such as `postgres`, make JIM's user the database's owner, then start JIM again:

```sql
ALTER DATABASE jim OWNER TO jim;
```

## :material-package-variant-closed: Podman

Problems particular to running JIM on Podman, such as a rootless JIM that will not start, or Ubuntu's AppArmor profiles stopping JIM's containers, are on [Troubleshooting Podman](troubleshooting-podman.md).

## :material-certificate-outline: HTTPS

### The browser cannot connect at `http://<server>`

The browser reports that the site cannot be reached or refused the connection, or, with a port in the address, that the page sent no data.

**What it means.** JIM serves HTTPS only. Nothing answers plain HTTP on port 80, and JIM's own port does not answer plain HTTP either. Nothing is wrong with JIM.

**How to fix.** Open JIM at its `https://` address, such as `https://jim.example.com`. After a first visit over HTTPS, browsers switch to `https://` for JIM's name on their own.

### `docker compose up` fails with `bind source path does not exist: .../tls/tls.crt`

Docker Compose first warns that a `secret file ... does not exist`, then stops without starting `jim.web`.

**What it means.** JIM's certificate or key is missing from the installation's `tls` folder. Nothing has changed in your installation; `jim.web` has not been started.

**How to fix.** Run the installer's certificate step, which creates a certificate or installs your organisation's: `sudo /opt/jim/setup.sh --certificate` (from an installation made by hand, run the `setup.sh` you downloaded or the one in the release bundle, with `JIM_INSTALL_DIR=/opt/jim`). To place the files yourself instead, see [The certificate](tls-and-reverse-proxy.md#the-certificate). Then run your `docker compose ... up -d` command again.

### `jim.web` keeps restarting with `Access to the path '/run/jim-tls/tls.key' is denied`

The `jim.web` log shows `System.UnauthorizedAccessException: Access to the path '/run/jim-tls/tls.key' is denied`, and the container restarts again and again.

**What it means.** JIM runs as UID `1654` with every capability dropped, and Docker mounts the key with the owner and mode it has on the host, so JIM cannot read a key that belongs to anyone else. The other services, and your data, are unaffected.

**How to fix.** As root, in the installation folder:

```bash
cd /opt/jim
chown 1654:1654 tls/tls.key && chmod 400 tls/tls.key
docker compose -f docker-compose.yml -f docker-compose.production.yml restart jim.web
```

The installer's certificate step (`sudo /opt/jim/setup.sh --certificate`) sets the owner itself.

### Signing in through nginx fails with `502 Bad Gateway`

Sign-in reaches your identity provider, but the return to JIM (`/signin-oidc`) shows nginx's `502 Bad Gateway` page, and nginx's error log records `upstream sent too big header while reading response header from upstream`.

**What it means.** JIM's response to a sign-in sets cookies larger than nginx's default buffer for a response's headers, so nginx gives up on it. Nothing is wrong with JIM or your identity provider.

**How to fix.** Add these to the nginx `location` that proxies JIM, as the [nginx example](tls-and-reverse-proxy.md#nginx-example) does, then reload nginx:

```nginx
proxy_buffer_size 16k;
proxy_buffers 8 16k;
```

## :material-shield-key-outline: Authentication

### Sign-in loops between JIM and the identity provider

You open JIM, sign in at your identity provider, and land on a page titled **Sign-in could not complete** instead of the portal. The `jim.web` log records a warning beginning `Sign-in stopped` that names the cause, next to one like this:

```text
'.AspNetCore.Correlation.<random characters>' cookie not found.
```

and the [security audit log](security-audit-events.md) records a failed sign-in with the reason `OIDC correlation failed`.

**What it means.** When sign-in starts, JIM sets short-lived cookies and checks for them when the identity provider sends the browser back; without them, JIM cannot match the identity provider's response to the sign-in it started. JIM's sign-in cookies are HTTPS-only in a production deployment, so a browser reaching JIM over plain HTTP at an address other than `localhost` discards them. A standard installation serves HTTPS only, so this happens when JIM's plain HTTP port has been published to other machines, typically by a compose override of your own or by starting JIM without `docker-compose.production.yml`. Safari can do the same even at `http://localhost`. JIM restarts a sign-in once when its cookies go missing, which recovers a one-off loss such as cookies cleared mid-sign-in or the Back button onto the sign-in callback. When the connection means the cookies can never survive, or the restart fails the same way, JIM stops on this page instead of starting sign-in over and over. Nothing is lost when it stops: try again once the cause is fixed. [TLS and Reverse Proxy](tls-and-reverse-proxy.md) explains the HTTPS requirement.

**How to fix.**

1. Serve JIM over HTTPS: start it with `docker-compose.production.yml`, which does, and remove any override that publishes port `8080` beyond the host's loopback interface. If a reverse proxy in front of JIM forwards plain HTTP to it, [set `JIM_TRUSTED_PROXIES`](tls-and-reverse-proxy.md#trusting-the-reverse-proxy) so JIM knows its requests arrived over HTTPS. Without it, JIM sees only the proxy's plain HTTP connection even though your address bar shows `https://`.
2. Register JIM's `https://` sign-in and sign-out callback URLs at your identity provider, and remove any `http://` ones you added for a plain-HTTP address (see the [SSO Setup Guide](sso-setup.md)).
3. Open JIM at its `https://` address.

If JIM is already served over HTTPS and trusts its proxy but you still see the page, the browser is blocking cookies for JIM's address, or something between the browser and JIM is removing the `Set-Cookie` or `Cookie` headers. Allow cookies for the JIM site, and make sure any proxy or security appliance passes them through unchanged.

### A setting stopped sign-in

You sign in at your identity provider and land on a page titled **A setting stopped sign-in**, which names a setting such as `JIM_SSO_CLIENT_ID`, the value JIM expected and the value it received. Or the PowerShell module, or another client of JIM's API, is refused with `401 Unauthorized` although your identity provider signed it in. Either way, the `jim.web` log records a warning beginning `Sign-in problem` with the same details.

**What it means.** A value JIM is configured with does not match what your identity provider sends. JIM recognises these:

| Setting | What did not match |
|---------|--------------------|
| `JIM_SSO_CLIENT_ID` | The ID token is for another client, or the identity provider does not let the portal's client sign people in with an authorisation code (`unauthorized_client`). |
| `JIM_SSO_SECRET` | The identity provider refused the portal's client ID or secret (`invalid_client`). |
| `JIM_SSO_API_SCOPE` | The identity provider refused the scopes JIM asked for (`invalid_scope`, or `invalid_resource` from AD FS), or an access token is not for JIM's API: its audience is not the one JIM expects. |
| `JIM_SSO_VALID_ISSUERS` | The token came from an issuer JIM does not accept. |
| `JIM_SSO_CLAIM_TYPE` | The sign-in carried no claim of the type JIM identifies people by. JIM lists the claim types it did carry, never their values. |

JIM reports an access token only once its signature shows your identity provider issued it, so nobody else can make JIM report a problem. JIM still refuses the token: an API caller sees only `401`, and the details go to the log and to administrators.

**How to fix.** Change the setting JIM names, or your identity provider's configuration, so the two values match. Restart JIM after changing a setting. To list every problem JIM has met since it started, connect as an administrator, or with the infrastructure API key when nobody can sign in, and run `Get-JIMSignInDiagnostic`:

```powershell
Connect-JIM -Url https://jim.example.com -ApiKey $env:JIM_INFRASTRUCTURE_API_KEY
(Get-JIMSignInDiagnostic).Problems | Format-Table Source, Setting, Expected, Received, Count
```

`Get-JIMSignInDiagnostic` also shows the redirect URIs JIM sends your identity provider when reached at the address you connected to. When sign-in stops on the identity provider's own error page about a redirect URI, compare them with the ones registered for the portal's client: JIM never sees that error, because the identity provider does not send the browser back.

### `Invalid parameter: redirect_uri` when running `Connect-JIM` interactively

You clicked **Login** (or let `Connect-JIM` open your browser) and the identity provider returned an error page saying `Invalid parameter: redirect_uri` (Keycloak), `AADSTS50011` (Entra ID), or a similar redirect-mismatch message.

**What it means.** The PowerShell module is trying to authenticate as the identity provider client whose ID came from JIM's `/api/v1/auth/config` endpoint, but that client does not have a loopback redirect URI (`http://localhost:8400/callback/` through `http://localhost:8409/callback/`) registered. This typically happens because JIM is advertising the web application's client ID (a confidential client), but the PowerShell module needs a public client configured for PKCE/loopback.

**How to fix.**

1. Confirm your identity provider has a public client registered with at least the redirect URI `http://localhost:8400/callback/`. The per-provider guides cover this: [Entra ID Step 5b](sso-setup/microsoft-entra-id.md#step-5b-configure-powershell-module-authentication-optional-but-recommended), [AD FS Step 1](sso-setup/ad-fs.md#step-1-register-jim-with-ad-fs), [Keycloak Step 1](sso-setup/keycloak.md#step-1-register-jim-with-keycloak).
2. If the public client is a **separate registration** from the web application (always the case for Keycloak and AD FS), set `JIM_SSO_PUBLIC_CLIENT_ID` on the JIM server to the public client's client ID and restart JIM.
3. If the public client **shares a registration** with the web application (Entra ID with both platforms added to one app), you do not need to set `JIM_SSO_PUBLIC_CLIENT_ID`; make sure the web app registration has the loopback redirect URI added under its native/mobile platform.

Verify the config the server is advertising:

```bash
curl https://your-jim-url/api/v1/auth/config
```

The `clientId` in the response must be the client that has the loopback redirect URI registered at your identity provider.

## :material-bell-off-outline: Known noisy log lines

Some messages in the container logs look alarming but are expected and harmless. They are documented here so operators can confirm at a glance that nothing is actually wrong.

### `jim.scheduler`: `Cannot load library libgssapi_krb5.so.2`

During startup you may see:

```text
jim.scheduler  | Cannot load library libgssapi_krb5.so.2
jim.scheduler  | Error: libgssapi_krb5.so.2: cannot open shared object file: No such file or directory
```

**What it means.** The PostgreSQL client library (Npgsql) probes for the GSS/Kerberos authentication library on startup to advertise that capability to the server. The scheduler image deliberately does not include it.

**Why it is safe.** The scheduler authenticates to PostgreSQL using password credentials and does not talk to any LDAP or Kerberos-backed service. The missing library is never used.

**Why we do not install it.** Adding `libgssapi-krb5-2` to the scheduler image would silence the message, but it would also add an unused dependency to the container's attack surface. JIM is designed for high-assurance environments; we keep images minimal and only install libraries that are actually needed at runtime.

**How to confirm the scheduler is healthy despite the message.** Immediately after the probe lines you should see:

```text
jim.scheduler  | info: Microsoft.Hosting.Lifetime[0]
jim.scheduler  |       Application started. Press Ctrl+C to shut down.
```

The container should also transition to `healthy` in `docker ps`.

## :material-lifebuoy: Still stuck?

Collect these before asking for help:

- **The JIM version,** shown at the foot of every portal page and by `Connect-JIM`, or the release you installed.
- **How JIM is installed:** Docker or Podman, with the bundled database or your own, and behind a reverse proxy or not.
- **The services' logs from the time of the problem:** `docker logs jim.web`, and likewise `jim.worker`, `jim.scheduler` and `jim.database`; on Podman, `sudo podman logs jim-web` and the rest (see [Troubleshooting Podman](troubleshooting-podman.md#still-stuck)).
- **For a sign-in problem,** the `jim.web` warning beginning `Sign-in stopped` or `Sign-in problem`, what `Get-JIMSignInDiagnostic` shows, and the failed sign-in in the [security audit log](security-audit-events.md).

Then ask in the [Q&A](https://github.com/TetronIO/JIM/discussions/categories/q-a) category of GitHub Discussions, or open a [GitHub Issue](https://github.com/TetronIO/JIM/issues) for a bug. Leave secrets out of anything you share: `.env` and `jim-config.yaml` hold them.
