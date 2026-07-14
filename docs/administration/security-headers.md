---
title: Security Headers
---

# Security Headers

Every response JIM.Web sends, whether from the Blazor admin portal, a static asset, or the REST API, carries a
set of defence-in-depth security headers. This is an application-layer control (OWASP Top 10 A02: Security
Misconfiguration) that reduces the impact of cross-site scripting, clickjacking, and MIME-sniffing attacks, and
requires no configuration.

## Headers sent

| Header | Value | Purpose |
|--------|-------|---------|
| `Content-Security-Policy` | See [below](#content-security-policy) | Restricts which origins scripts, styles, images, fonts and connections may load from |
| `X-Content-Type-Options` | `nosniff` | Stops browsers guessing a response's content type, preventing MIME-sniffing attacks |
| `X-Frame-Options` | `DENY` | Stops JIM being embedded in an `<iframe>` on another site (clickjacking protection) |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | Limits how much of JIM's URL is leaked in the `Referer` header of outbound links |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` | Disables browser APIs JIM never uses |

If something earlier in the pipeline (a controller action, for example) has already set one of these headers,
JIM does not overwrite it; the more specific value wins.

## Content Security Policy

JIM's Content Security Policy (CSP) is currently in **stage one**: a restrictive, same-origin-only policy
compatible with Blazor Server and MudBlazor as they stand today.

```
default-src 'self';
script-src 'self' 'unsafe-inline';
style-src 'self' 'unsafe-inline';
img-src 'self' data:;
font-src 'self';
connect-src 'self' wss: ws:;
frame-ancestors 'none';
base-uri 'self';
form-action 'self'
```

Everything (scripts, styles, images, fonts, WebSocket connections) is restricted to JIM's own origin. JIM is
designed for [air-gapped deployment](deployment.md#air-gapped-deployment), so nothing in the default policy
depends on a third-party host, CDN, or external font provider.

`'unsafe-inline'` is present for `script-src` and `style-src` because Blazor Server's reconnection handling and
MudBlazor's component styling both rely on inline `<script>`/`style="..."` usage. This is a deliberate,
documented stage-one compromise; a future stage-two release will move to a nonce-based policy and remove it.

`connect-src` allows both `ws:` and `wss:` so the Blazor Server SignalR circuit (the WebSocket connection that
keeps the UI live) works identically whether JIM is reached over plain HTTP (local development) or HTTPS
(a real deployment).

## Reverse proxies and corporate networks

If you terminate TLS at a reverse proxy in front of JIM (see [TLS and Reverse Proxy](deployment.md#tls-and-reverse-proxy)),
JIM's headers pass through unchanged unless your proxy is explicitly configured to strip or rewrite them. A
corporate web proxy or browser security extension that inspects or blocks unrecognised `Content-Security-Policy`
directives may need an allowance for JIM's origin; the policy above is the complete list of directives JIM sends,
useful if you need to document it for a network security review.

## See also

- [Rate Limiting](../api/rate-limiting.md): the other REST API hardening control from the same security assessment
- [Deployment](deployment.md): TLS termination and reverse proxy configuration
