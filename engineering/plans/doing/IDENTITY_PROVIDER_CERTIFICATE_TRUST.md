# Identity Provider Certificate Trust - Implementation Plan

- **Status:** Doing (Phases 1-2 complete)
- **Created:** 2026-10-18
- **Issue:** [#2363](https://github.com/TetronIO/JIM/issues/2363)
- **Related Issues:** #2362 (parent: easiest identity provider setup and maintenance), #2355 (AD FS lab), #1914 (certificate chain trust for Connectors), #2361 (AD FS troubleshooting)

## Overview

JIM cannot sign anyone in through an identity provider whose TLS certificate chains to an internal certificate authority (the usual case for AD FS and on-premises Keycloak). Worse, JIM.Web does not start at all: the OpenID Connect discovery document is fetched before the host is built, and a TLS failure there is fatal after five retries.

This plan makes the JIM certificate store vouch for the identity provider as well as for Connectors, makes start-up survive an untrusted identity provider, and gives administrators a first-start and maintenance experience that explains the problem where it appears and fixes it without a rebuild or restart.

The agreed UX is the walkthrough published for the issue (first start; maintenance; guard rails). Decisions taken with the product owner on 2026-10-18:

| # | Decision | Outcome |
|---|----------|---------|
| D1 | Trust a certificate straight from the identity provider (JIM captures the chain) | Yes. Administrator role only (a signed-in Administrator, or an API key holding the Administrator role, which includes the infrastructure API key). Never available to the User role or anonymously. |
| D2 | Per-certificate **Trusted for** (Sign-in, Connectors, or both) | Yes. Existing certificates become Connectors only. |
| D3 | Background check of the identity provider's chain, and a warning banner | Yes, in this PR. |
| D4 | Lock-out guard on the certificate sign-in depends on | Warn with typed confirmation; do not block. |

Rejected (recorded on the walkthrough): a "Trust" button on the unauthenticated page; trust on first use; a mounted CA folder (`update-ca-certificates` at start).

## Business Value

- An AD FS or internal-CA Keycloak deployment works out of the box once one certificate is trusted, with no custom image.
- The problem is explained in three places an administrator will look (container log, the portal's first page, the docs), each naming the certificate to trust.
- Certificate renewals onto a new CA are caught before sign-in breaks, and a tidy-up of the certificate store cannot lock everyone out silently.
- One place to manage every certificate JIM trusts, scriptable through REST and PowerShell.

## Technical Architecture

### Current state

- `JIM.Web/Program.cs` fetches `/.well-known/openid-configuration` with a bare `HttpClient` before `builder.Build()`. Failure ends the process (`Log.Fatal`, exit 1). The result only feeds the runtime-generated OpenAPI document's OAuth endpoints (development only; production serves a static document built with placeholders).
- The OpenID Connect and JWT bearer handlers use their default back-channel, so they trust only the container's operating-system store.
- `JimCertificateStoreTrust.VouchesFor` (`JIM.Connectors`, internal) is the chain rule the SQL, SCIM and LDAP Connectors share.
- `TrustedCertificate` has no notion of purpose; `ICertificateProvider.GetTrustedCertificatesAsync()` returns every enabled certificate to Connectors.
- When the identity provider's metadata cannot be fetched during a sign-in challenge, the exception is unhandled and the user lands on the generic `/Error` page.

### Proposed solution

```
                      ┌────────────────────────────── JIM.Web ─────────────────────────────────┐
 browser ──► portal ──┤ IdentityProviderUnavailableMiddleware ──► /sign-in-unavailable (anon)  │
                      │      ▲ catches metadata/TLS failure on challenge                         │
                      │ OIDC + JWT bearer handlers ──► BackchannelHttpHandler ───────────────────┼──► identity provider
                      │      (one shared IdentityProviderBackchannel)  │                          │
                      │                                                ▼                          │
                      │                       IdentityProviderCertificateValidator (sync)         │
                      │                         OS trust  OR  CertificateStoreTrust.VouchesFor    │
                      │                         over a pre-loaded "Sign-in" certificate snapshot  │
                      │ IdentityProviderTrustMonitor (hosted, 15 min + on demand) ──► status      │
                      │ GET /api/v1/auth/identity-provider-trust (anonymous, read-only)           │
                      │ POST /api/v1/certificates/identity-provider (Administrator)               │
                      └───────────────────────────────────────────────────────────────────────────┘
                                         │ JimApplication
                      ┌──────────────── JIM.Application ───────────────────┐
                      │ IdentityProviderTrustServer: probe authority,       │
                      │   evaluate chain, report status, trust a presented  │
                      │   certificate (audited Activity)                    │
                      │ CertificateServer: TrustedFor; purpose filters      │
                      └─────────────────────────────────────────────────────┘
```

**Trust rule (shared).** Move `JimCertificateStoreTrust` to `JIM.Utilities` as a public `CertificateStoreTrust` (Connectors, Application and Web all reference `JIM.Utilities`). Rule unchanged: the chain must build under `AllowUnknownCertificateAuthority` with no downloads and no revocation check, and some element must be in the given set. Add one entry point for a TLS callback:

`CertificateStoreTrust.Evaluate(X509Certificate2 serverCertificate, X509Chain? chain, SslPolicyErrors errors, IReadOnlyCollection<X509Certificate2> store)` returning `TrustOutcome { TrustedByOperatingSystem | TrustedByJimStore(thumbprint) | NameMismatch | NotPresented | Untrusted }`.

- `SslPolicyErrors.None` is accepted as the operating system decided it.
- `RemoteCertificateNameMismatch` or `RemoteCertificateNotAvailable` always fails, whatever the store holds. The store only answers chain errors; it can never switch a check off.
- The server-sent intermediates come from `chain.ChainPolicy.ExtraStore`.

**Purpose (D2).** `TrustedCertificate.TrustedFor` is a `[Flags] enum CertificateTrustPurpose { None = 0, Connectors = 1, SignIn = 2 }`. New migration adds the column with default `1`, so every existing certificate stays Connectors only. Validation: at least one purpose. `ICertificateProvider.GetTrustedCertificatesAsync()` returns Connectors-purpose certificates only (unchanged behaviour for every existing row); a new `CertificateServer.GetSignInTrustedCertificatesAsync()` returns SignIn-purpose ones. The configuration snapshot and change history include the field.

**Back-channel (sign-in trust).** One `IdentityProviderBackchannel` singleton in JIM.Web builds the `HttpMessageHandler` given to both `OpenIdConnectOptions.BackchannelHttpHandler` and `JwtBearerOptions.BackchannelHttpHandler`:

- An outer `DelegatingHandler` refreshes the SignIn-certificate snapshot asynchronously before each request (cheap query; back-channel requests are rare: metadata refresh, signing keys, one token redemption per sign-in). A short (5 s) micro-cache bounds repeated loads. A database failure keeps the previous snapshot (empty before the first successful load), so the operating system's trust still applies.
- The inner `SocketsHttpHandler` validation callback reads the snapshot synchronously and calls `CertificateStoreTrust.Evaluate`. It also records the presented chain for diagnostics.
- No cache invalidation plumbing is needed: every back-channel request sees the store as it is within 5 s, which is what makes "no restart" and the auto-refreshing page work.
- No custom configuration manager either. IdentityModel's `ConfigurationManager` (8.19.2) retries on every call while it holds no configuration, so a certificate trusted after a refusal counts on the very next sign-in. `IdentityProviderConfigurationRecoveryTests` pins that, so an upgrade that starts backing off there fails the build.

**Start-up.** Remove the fatal pre-build discovery fetch. The runtime OpenAPI document's OAuth endpoints are resolved lazily inside the document transformer through the OpenID Connect handler's own `ConfigurationManager` (same back-channel, same trust), falling back to the authority-based placeholders when unreachable. The monitor's first probe after the database is ready logs the actionable warning shown in the walkthrough (step A1). `ValidateSsoConfiguration` (environment variables present) stays fail-fast.

**Probe and status (D3).** `IdentityProviderTrustServer` (JIM.Application, `JimApplication.IdentityProviderTrust`):

- `ProbeAsync()` connects to the authority's discovery endpoint (from `ServiceSettings.SSOAuthority`) through an injectable `IIdentityProviderCertificateProbe` seam, captures the presented chain, and evaluates it against the SignIn snapshot. Returns `IdentityProviderTrustStatus` (JIM.Models): authority, host, `State` (`Trusted`, `Untrusted`, `NameMismatch`, `Unreachable`, `NotHttps`, `NotChecked`), `TrustedBy` (`OperatingSystem` or `JimCertificateStore` plus the trusting certificate's id/thumbprint/name/expiry), the presented chain (subject, issuer, thumbprint, not-after, sent-by-server, in-JIM-store), `CheckedUtc`, and `TrustingCertificateExpiresSoon` (30 days, matching `TrustedCertificate.IsExpiringSoon`).
- `TrustPresentedCertificateAsync(string thumbprint, string? name, string? changeReason, initiator)` (MetaverseObject and ApiKey overloads, as everywhere else in `CertificateServer`): probes again, requires the thumbprint to be one the identity provider presented in this probe, then adds it with `TrustedFor = SignIn` through the existing audited add path. If the certificate is already in the store for Connectors only, it widens `TrustedFor` instead (audited update). Thumbprint is mandatory on the server; confirmation is the client's job (PowerShell `ShouldProcess`, the portal dialog).
- JIM.Web's `IdentityProviderTrustMonitor` (hosted service, singleton) holds the latest status: probes once the database is ready, every 15 minutes, after any certificate change it observes through the back-channel snapshot changing, and on demand with a 10-second floor so the anonymous status endpoint cannot be used to amplify outbound requests.

**Surfaces.**

| Capability | Portal | REST | PowerShell |
|---|---|---|---|
| See trust status and presented chain | `/sign-in-unavailable` page; banner; Certificates page "In use by sign-in" | `GET /api/v1/auth/identity-provider-trust` (anonymous, read-only) | `Get-JIMIdentityProviderTrust` |
| Trust a presented certificate (D1, Administrator only) | Banner's **Review and trust** dialog | `POST /api/v1/certificates/identity-provider` | `Add-JIMCertificate -FromIdentityProvider [-Thumbprint] [-Name]` |
| Choose purposes | Upload/Edit dialogs: **Trusted for** | `trustedFor` on upload, file, update requests and detail/header DTOs | `-TrustedFor SignIn, Connectors` on `Add-JIMCertificate` and `Set-JIMCertificate`; shown by `Get-JIMCertificate` |
| Lock-out warning (D4) | Typed-name confirmation on delete, disable or removing Sign-in from the in-use certificate | `inUseBySignIn` on DTOs | `Remove-JIMCertificate` / `Set-JIMCertificate` confirmation text names the consequence (`ConfirmImpact = High` already) |

`Add-JIMCertificate -FromIdentityProvider` without `-Thumbprint` calls the status endpoint, shows the chain and the certificate it will trust, and asks for confirmation (`ShouldProcess`), then posts with the shown thumbprint. With `-Thumbprint` it posts directly and the server checks the match.

**Sign-in unavailable page.** `IdentityProviderUnavailableMiddleware` wraps the portal pipeline (not `/api`): an exception whose chain shows the OpenID Connect configuration could not be retrieved (TLS authentication failure, connection failure, or `IDX20803`) triggers an on-demand probe and redirects to the anonymous Razor Page `/sign-in-unavailable` (pattern: `/sign-in-failed`). The page renders the status, the chain, and the prefilled commands; a small static script polls the status endpoint every 10 seconds and enables **Sign in** once the state is `Trusted`. The page never changes trust.

**Banner (D3).** `IdentityProviderTrustBanner` beside `ServiceHealthBanner` inside the Administrator `AuthorizeView` in `MainLayout`: warning when the state is `Untrusted`/`NameMismatch` ("New sign-ins will fail ..."), with an `AlertButton` opening `TrustIdentityProviderCertificateDialog` (Untrusted only; a name mismatch is not something trust can fix, so it links to the docs instead); info when the trusting certificate expires within 30 days, linking to the Certificates page. Content derived by a pure function (pattern: `ServiceHealthDisplay.Banner`) so it is unit-tested without rendering.

### Security notes

- The anonymous status endpoint discloses only what any TLS client of the identity provider already sees, plus the authority the sign-in redirect already reveals.
- Every write path is `[Authorize(Roles = "Administrator")]` (controller) or an Administrator-gated page; the API key path resolves to the key's roles, so a User-role key gets 403.
- Trusting is pinned to a thumbprint the identity provider presented in the same request, so neither an arbitrary certificate nor a stale chain can be trusted through this path.
- SignIn and Connectors trust are independent: a certificate added for a Connector never vouches for the server that issues tokens.
- Validation is additive only; hostname, validity period and signatures are always enforced.

## Implementation Phases

Each phase is test-first (red, green, refactor) and builds and tests green before the next.

1. **Shared trust rule and purpose.** ✅
   - Move `JimCertificateStoreTrust` to `JIM.Utilities/Security/CertificateStoreTrust.cs`; update `CertificateChainAssembler` and `SqlConnector`. Add `Evaluate` with unit tests (OS-trusted; vouched by root, intermediate, self-signed leaf; name mismatch refused despite store; expired refused; empty store; certificate not in store).
   - `CertificateTrustPurpose`, `TrustedCertificate.TrustedFor`, migration (default Connectors), repository `UpdateAsync` copies it, `RequiresPostgres` NoTracking round-trip test, snapshot field, `GetTrustedCertificatesAsync` filters Connectors, `GetSignInTrustedCertificatesAsync`. Add/update overloads accept `trustedFor` (default Connectors).
2. **Back-channel and start-up.** ✅
   - `IdentityProviderBackchannel` (snapshot handler + validator), wired into OpenID Connect and JWT bearer options. Tests against an in-process TLS server presenting a certificate from a private CA generated in the test (pattern: the Connector TLS tests): refused with an empty store, accepted once the CA is in the SignIn snapshot, refused when the CA is Connectors only, refused on name mismatch.
   - Remove the fatal discovery fetch; lazy OAuth endpoints in the runtime OpenAPI transformer.
3. **Probe, status and trusting.**
   - Models, `IdentityProviderTrustServer` with probe seam, `TrustPresentedCertificateAsync` (add, widen, thumbprint not presented, mismatch, Activity attribution for user and API key).
   - `IdentityProviderTrustMonitor` hosted service with on-demand floor; start-up warning log.
   - `GET /api/v1/auth/identity-provider-trust` (anonymous) and `POST /api/v1/certificates/identity-provider` (Administrator); DTO changes (`trustedFor`, `inUseBySignIn`). Controller tests, including attribute checks for anonymous/Administrator.
4. **Portal.**
   - `/sign-in-unavailable` page, middleware (tests: portal request redirected; `/api` untouched; unrelated exceptions untouched), polling script.
   - Certificates page: **Trusted for** column and dialog fields, "In use by sign-in" status, typed confirmation for delete/disable/removing Sign-in on the in-use certificate.
   - Banner and trust dialog; banner content tests.
5. **PowerShell.** `Get-JIMIdentityProviderTrust`; `Add-JIMCertificate -FromIdentityProvider`; `-TrustedFor` on Add/Set; confirmation wording; Pester tests; reference docs regenerated.
6. **Docs, changelog, runtime verification.**
   - New how-to `docs/administration/sso-setup/trusting-your-identity-provider.md`; linked from the troubleshooting section of every identity provider guide (Entra ID gains one, for TLS-inspecting proxies) and from `sso-setup.md`; `certificates.md` gains **Trusted for**; nav entry.
   - Changelog: ✨ trust an internal-CA identity provider; ✨ Trusted for; 🐛 JIM.Web no longer fails to start when the identity provider is unreachable or untrusted.
   - Runtime: run the development Keycloak over HTTPS with a certificate from a throwaway private CA (pattern: `test/integration/docker/keycloak-scim/start-keycloak-scim.sh`), point JIM at it, and walk the first-start journey end to end (log, page, `Add-JIMCertificate -FromIdentityProvider` with the infrastructure API key, page turns green, sign-in works, no restart), then the banner, lock-out confirmation and a User-role API key being refused.

## Success Criteria

- An identity provider whose certificate chains to an internal CA works once that CA, an intermediate, or the provider's own certificate is trusted for Sign-in, with no image rebuild and no restart.
- JIM.Web starts and serves the sign-in unavailable page and the REST API (API keys) while the identity provider is untrusted or unreachable.
- Trusting a certificate for Sign-in never changes what Connectors trust, and vice versa; no setting disables validation.
- Trusting from the identity provider is refused for anyone without the Administrator role.
- Every add, widen, update and delete records an Activity attributed to the person or API key.
- One documented recovery path, linked from every identity provider guide.

## Dependencies

None new. Uses `System.Net.Security`, `System.Security.Cryptography.X509Certificates`, and ASP.NET Core's OpenID Connect and JWT bearer back-channel options.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| A synchronous TLS callback cannot query the database | Snapshot pre-loaded by an async `DelegatingHandler` before each request; callback only reads memory |
| Anonymous status endpoint used to drive outbound traffic | Probe floor of 10 seconds, shared across callers; status served from memory between probes |
| Removing the start-up fetch hides a misconfigured authority | Monitor's first probe logs it at Warning with the docs link; the page and banner show it |
| Existing certificates silently start vouching for sign-in | Migration defaults to Connectors only; Sign-in is opt-in per certificate |
| Server does not send the root, so only the issuing CA can be trusted from the identity provider | Supported by the rule (any chain element); docs and page recommend uploading the root because it outlasts renewals |
| Multiple JIM.Web instances hold different statuses | Each instance probes independently; trust itself lives in the database, so every instance converges within one back-channel request |
