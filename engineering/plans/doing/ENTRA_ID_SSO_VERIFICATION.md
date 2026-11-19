# Microsoft Entra ID SSO Verification

- **Status:** Doing (all phases built; waiting on the first `jim-sso-entra` run from `main`, then the gate is switched on)
- **Created:** 2026-11-19
- **Issue:** [#2367](https://github.com/TetronIO/JIM/issues/2367)

## Overview

JIM's Microsoft Entra ID guide has not been run since Keycloak replaced Entra ID as the development identity provider (#197). This plan verifies Entra ID end to end against a dedicated test tenant, settles what the guide should say, teaches `Register-JIMIdentityProvider` to register JIM with Entra ID, and makes the result a release gate: a commit status of its own, `jim-sso-entra`, posted nightly by the Active Directory lab workflow, beside `jim-ad-lab-adfs` (#2355, #2441).

It follows the AD FS work closely, because that work already solved most of the shape: a scenario that runs on its own (Scenario 031), browserless sign-in checks run from the lab's toolbox container, a registration cmdlet that creates and repairs, and a gate per status.

## The test tenant

`tetronjim.onmicrosoft.com` (tenant ID `34599ee0-829e-4dd4-8da2-4727a0664d51`), on the free tier, isolated, later shared with SCIM testing. Set up on 2026-11-19 through a device-code sign-in approved by its administrator; no standing administrative credential was left behind.

| What | State |
|---|---|
| Security defaults | Off. The free tier has no Conditional Access, so this is the only way to give the test users a sign-in without MFA |
| `admin@` | Global Administrator, per-user MFA enforced (so turning security defaults off removed MFA from nobody who matters) |
| `jim-test-admin@`, `jim-test-user@` | Ordinary users, no roles, passwords that do not expire |
| CI app: "JIM CI: identity provider registration" | Client ID `193745d9-66c8-4937-81d0-2c50cad399d6`. Only `Application.ReadWrite.OwnedBy` (admin consented). Federated credential for `repo:TetronIO/JIM:environment:jim-ad-lab`. No secret or certificate |
| `jim-ad-lab` environment | Secrets `JIM_SSO_ENTRA_ADMIN_PASSWORD`, `JIM_SSO_ENTRA_USER_PASSWORD`; variables `JIM_SSO_ENTRA_TENANT_ID`, `JIM_SSO_ENTRA_CI_CLIENT_ID` |

## Decisions

All five were agreed on 2026-11-19, as recommended below (D2 as revised against Microsoft's guidance).

### D1. Which claim identifies a person: `oid`, matched to Object Identifier (agreed)

**Agreed: `JIM_SSO_CLAIM_TYPE=oid`, `JIM_SSO_MV_ATTRIBUTE=Object Identifier`** (a built-in Metaverse attribute that already exists).

The issue's principle 2 says `sub`, for the same shape across providers. Two facts argue against it for Entra ID:

- Entra ID's `sub` is pairwise: unique to the person *and the application*. Nobody can know a person's `sub` before they have signed in to JIM, which is exactly why Step 7 ("find your admin's subject identifier") is the clumsiest step in the guide. `oid` is the person's object ID in the tenant: shown in the Entra admin centre, returned by Graph, and stable for the account's life. Microsoft's own guidance is to identify a user by `oid` (with `tid`).
- With `oid`, Step 7 disappears: `Register-JIMIdentityProvider`, which already has a Graph token, returns the `oid` of the person who ran it, ready for `JIM_SSO_INITIAL_ADMIN`.
- It is also how AD FS already works (`oid` matched to Object GUID, #2441), and it means an Entra-provisioned Metaverse Object (SCIM from Entra ID, later) can carry the same value, so a person signs in as the object synchronisation made for them.

The alternative, `sub` matched to Subject Identifier, keeps the issue's wording but keeps Step 7 as a sign-in-then-look-it-up step.

### D2. Access tokens: version 2, the client ID as the only audience, and `scp` required (agreed)

**Agreed: the registration sets `requestedAccessTokenVersion` to 2; for Entra ID, JIM accepts only the client ID as an access token's audience; and every bearer token must carry an `scp` claim granting JIM's API scope (`access_as_user`).**

This is Microsoft's guidance, checked on 2026-11-19:

- A resource application (an API, which JIM is) should use version 2 access tokens, and once it does, "ensure the application's audience validation logic is modified to **only** accept its `appId`" ([Security best practices for app registration](https://learn.microsoft.com/entra/identity-platform/security-best-practices-for-app-registration#access-token-version)). A v2 token's `aud` is the client ID; today JIM expects `api://{id}` and refuses it with `401` (the #2353 failure class).
- A delegated token's `scp` must be validated, and its absence checked for, since ID tokens carry none ([Validate the actor](https://learn.microsoft.com/entra/identity-platform/claims-validation#validate-the-actor)). An ID token's `aud` is also the client ID, so without the `scp` requirement it would pass as an access token.

A version 1 configuration stops working, deliberately (JIM is pre-release: no compatibility path); the changelog and the guide say so. Considered and rejected: pinning version 1 and changing nothing in JIM, which goes against Entra ID's grain; accepting both audiences, which Microsoft advises against.

### D3. How the checks sign people in: browserless, like AD FS (agreed)

**Agreed: an Entra ID variant of the AD FS checks' browserless sign-in client**: plain HTTP from the toolbox container, following Entra ID's sign-in page (its embedded configuration, the credential post, the "stay signed in" step) to JIM's redirect URI. No new dependency, and the same harness, result format and checks as Scenario 031.

The cost is that the client depends on Microsoft's sign-in page, which Microsoft can change. The client will name that failure as "Entra ID's sign-in page did not behave as expected" so it is never mistaken for a JIM fault, and `jim-sso-entra` being its own status keeps it from colouring anything else.

The alternative is a real headless browser (Playwright and Chromium in the toolbox), which survives page changes better but is a new third-party dependency (needs approval under the dependency policy) and a heavier container.

### D4. The release gate: generalise first, in its own stack layer (agreed)

**Agreed: replace the four per-status gate scripts with one, `scripts/Test-ReleaseGate.ps1 -Context <status>`, driven by a table of statuses (context, enforcement variable, remedy), with one matrix job in `release.yml` and one loop in `/release`.** It lands as the bottom layer of a stack, with the Entra ID work on top, since that work's gate is just one more row.

The four existing scripts (one each for `jim-ad-lab`, `jim-ad-lab-adfs`, `jim-pre-release` and `jim-deploy-lab`) already share `scripts/ReleaseGate.ps1` and differ only in their constants; a fifth copy, and a fifth job and skill step, is the drift the issue asked to stop.

The alternative is a fifth sibling script for `jim-sso-entra`: smaller now, more to keep in step later.

### D5. Who first creates JIM's registration in the tenant: the administrator, once (agreed)

**Agreed: run `Register-JIMIdentityProvider -Provider EntraId` once, from the devcontainer, as the tenant administrator (device code), which creates the registration and grants tenant-wide consent for its delegated permissions; then add the CI app as an owner.** From then on the nightly run repairs it with only `Application.ReadWrite.OwnedBy`, and resets its client secret each night.

The CI app cannot grant consent: that needs `DelegatedPermissionGrant.ReadWrite.All` (or an admin role), which can grant any delegated permission to any application, far beyond what CI should hold. Without consent, a test user meets a consent prompt they are not allowed to answer. A customer runs the cmdlet as an administrator, so the cmdlet grants consent when it can, and says so plainly when it cannot.

The alternative is to give the CI app the consent permission, so the nightly run also proves creation from nothing. It does prove a little more, at the price of a much stronger standing credential.

## Implementation Phases

### Layer 0 (bottom of the stack): refuse an ID token on the API ✅

Found while planning D2, and proven on the development stack: when the audience JIM requires is the portal's own client ID (Keycloak's `jim-api` today; Entra ID's version 2 tokens next), the portal's ID token carries that audience, and `/api` accepted it as an access token (`GET /api/v1/userinfo` answered 200). A bearer token must now also grant JIM's API scope (`scope` or `scp`) whenever the required audience is the client ID; with no API scope set, no token is accepted (`BearerScopeRequirement`). AD FS and Entra ID's version 1 tokens, whose audience is the API's own identifier, are unaffected. A security fix, so it sits at the bottom of the stack and can land on its own.

### Layer 1: one release gate script (D4) ✅

1. `scripts/ReleaseGates.psd1`: the table of statuses, each with its enforcement variable, the run that posts it, and the remedy text the current scripts hold as constants.
2. `scripts/Test-ReleaseGate.ps1 -Context <status> [-Sha] [-Repository]`: the existing `Invoke-ReleaseGate` core, reading the row. No override, as now.
3. `release.yml`: one gate job, a matrix over the table's rows, each skipping when its enforcement variable is not `true`; `validate` needs that one job.
4. `/release` skill: Step 7/8 and the tag-time checks loop over the table.
5. Remove the four scripts and their tests; port their tests to the new script (red first: the new script's tests fail before it exists).
6. Changelog: none (release tooling). Docs: `engineering/` release process notes.

### Layer 2: Entra ID verification

**Phase 1: access tokens (D2), in JIM.Web.** ✅
- For Entra ID (an authority on `login.microsoftonline.com`), the audience JIM accepts is the client ID only; the other providers keep today's resolution (`ApiAudienceResolver`).
- The `scp` requirement then follows from Layer 0 with no further change: the audience is the client ID, so a token must grant `access_as_user`.
- Unit tests first, in `JIM.Web.Api.Tests`, including an ID token refused as a bearer token.

**Phase 2: `Register-JIMIdentityProvider -Provider EntraId`.** ✅ Proved against the test tenant on 2026-11-19: it created the registration (client ID `80185dc2-6a10-415e-974b-40ceea310430`, for `https://jim.web:8443` and `http://localhost:5200`) with consent, and a second run changed nothing. The CI app was then made its owner (D5), and `jim-test-admin`'s object ID set as the `jim-ad-lab` variable `JIM_SSO_ENTRA_ADMIN_OBJECT_ID`.
- Parameters: `-TenantId`; `-AccessToken` (secure string, a Graph token, for unattended runs such as CI); without it, an interactive device-code sign-in, as used for the tenant setup.
- Creates or repairs one app registration (decided in #2367): Web platform (`/signin-oidc`, `/signout-callback-oidc`) for the portal; Mobile and desktop (loopback `http://localhost`) for `Connect-JIM`; single-page application (`/api/reference/`) for Try It, which Entra ID requires for a browser's PKCE code redemption; App ID URI `api://{client ID}` with the `access_as_user` scope; `requestedAccessTokenVersion` 2; delegated `openid`, `profile`, `email`, `offline_access`; a client secret.
- Grants tenant-wide consent when the caller may (D5), and otherwise lists the consent still needed in its output.
- `-ResetClientSecret` replaces the secret and removes the ones it replaced, so nightly runs do not accumulate secrets.
- Output, as for the other providers, with `ClaimType` `oid`, `MvAttribute` `Object Identifier`, and a new `InitialAdmin` property: the `oid` of the person who ran it (empty when run with a token that is not a person's).
- Pester tests first against a mocked Graph (plan, apply, idempotent re-run, `-WhatIf`), following `KeycloakRegistration.ps1`'s split into state, plan and change.
- Docs: `docs/powershell/identity-providers.md` (generated reference regenerated).

**Phase 3: the checks, Scenario 035** (032 to 034 were taken). ✅

Proved locally on 2026-11-19, before any harness wiring: JIM (this branch) with Entra ID's settings from `Register-JIMIdentityProvider`, and the checks' own script (`Invoke-AdfsSignInCheck.ps1`) run from the devcontainer against `http://localhost:5200`. `jim-test-admin` signed in to the portal, was provisioned as the initial administrator from `oid` matched to Object Identifier, got the Administrator API; Connect-JIM's bearer token was accepted and resolved to the same Metaverse Object; Try It signed in through the single-page application redirect and its token was accepted. `jim-test-user` signed in all three ways, was told they have no JIM identity, and was refused the Administrator API (403).
- `Invoke-Scenario-035-EntraIdSignIn.ps1`, run on its own like Scenario 031 (`Test-ScenarioRunsOnItsOwn`), Active Directory lab only, plus a local mode for the devcontainer.
- Runner Step 4f: gets a Graph token for the CI app from GitHub's OIDC token (a small PowerShell helper; no new Action), runs the cmdlet with `-ResetClientSecret` (the secret goes to the scrub list as AD FS's does), and restarts JIM with Entra ID's authority, `oid`/Object Identifier, and `jim-test-admin`'s `oid` as `JIM_SSO_INITIAL_ADMIN` (held as a `jim-ad-lab` variable, `JIM_SSO_ENTRA_ADMIN_OBJECT_ID`).
- The sign-in client (D3) and the checks, from the toolbox: portal sign-in; initial administrator provisioned; bearer token accepted on `/api`; `Connect-JIM` interactively; the same Metaverse Object from the portal and from `Connect-JIM`; `jim-test-user` signs in, is told who they are, and is refused every Administrator API; Try It signs in through the single-page application redirect and its token is accepted. Scenario 031's rename check is left out: the CI app cannot rename users, and `oid` cannot change with a rename by definition.

**Phase 4: `ad-lab.yml`.** ✅
- A step after the AD FS checks, with `continue-on-error`, running Scenario 35; `id-token: write` added to the job's permissions, for the federated credential.
- Posts `jim-sso-entra` from that step's outcome; adds it to the summary; scrubs the new secrets.
- The gate row from Layer 1, with `JIM_SSO_ENTRA_GATE_ENFORCED`, switched on after the first green run.

**Phase 5: the guide.** ✅
- Rewrite `docs/administration/sso-setup/microsoft-entra-id.md` to the outcome: `Register-JIMIdentityProvider` first, the portal steps as the manual alternative; version 2 tokens; `oid`; no Step 7; Step 5a (Scalar) dropped as a customer step, since Try It is now part of the registration.
- State what was verified, and against what, as the AD FS guide does.
- `test/integration/ad-lab/README.md`: the Entra ID checks and the host's outbound access.
- Changelog: the v2 token fix and the Entra ID registration cmdlet.

## Success Criteria

- `jim-sso-entra` is `success` on a dispatched `ad-lab.yml` run on `main`, and then nightly.
- The scenario passes locally from the devcontainer against the tenant before the PR is opened.
- Every check in the issue's Verify list is covered, apart from the rename, for the reason above.
- The release gate refuses a commit without `jim-sso-entra` once enforced.

## Risks

- **Microsoft's sign-in page changes** (D3): the checks fail with a message naming the page, not JIM.
- **The AD lab host's outbound access** to `login.microsoftonline.com` and `graph.microsoft.com`: confirmed by the user on 2026-11-19.
- **Tenant consent defaults**: if Microsoft changes what users may consent to, the administrator's one-off consent (D5) still covers JIM.
- **Throttling or an Entra ID outage**: visible as `jim-sso-entra` alone, never as an LDAP or AD FS failure.

## Out of scope

- SCIM testing against the tenant (the tenant is shared later; nothing here prepares it).
- Conditional Access or MFA scenarios: the free tier has neither, and JIM only authenticates through Entra ID.
