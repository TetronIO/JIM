# Entra ID Publisher Verification — Not Applicable to JIM

> **GitHub Issue:** #64 (closed)
> **Date:** 2026-02-28
> **Related:** #361 (Microsoft Graph API Connector)

## Question

Could JIM benefit from Microsoft's [Publisher Verification](https://learn.microsoft.com/en-us/entra/identity-platform/publisher-verification-overview) to ease integration with customer Entra ID tenants and reduce risk during consent stages?

## Answer

**No.** Publisher verification does not apply to JIM's deployment model.

### Why Not

Publisher verification is designed for **multi-tenant** applications — apps registered in one Azure AD tenant that request consent from users or administrators in *other* tenants. It provides a blue "verified" badge in consent prompts to increase trust.

JIM is **self-hosted**. When connecting to a customer's Entra ID tenant via the Microsoft Graph API, the deployment model is:

1. The customer creates an app registration **in their own tenant** (single-tenant)
2. The customer grants the required Graph API permissions via admin consent within their own tenant
3. JIM authenticates using the customer's own app registration credentials

This is a single-tenant pattern. There is no cross-tenant consent flow, so publisher verification has nothing to verify.

### When Would It Apply?

Only if JIM offered a cloud-hosted SaaS model where Tetron registered a central multi-tenant app that customers consented to. This is not in scope — JIM is designed to be self-contained and deployable in air-gapped environments.

Since November 2020, tenants with risk-based step-up consent enabled block consent to unverified multi-tenant apps, so publisher verification would become a hard requirement if a SaaS model were ever considered.
