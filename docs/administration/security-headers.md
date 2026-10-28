---
title: Security Headers
description: The security headers JIM sends on every response, its Content Security Policy, and what to check behind a reverse proxy.
type: reference
audience: security-assessor
---

# Security Headers

Every response JIM's web application sends, whether from the portal, a static asset, or the REST API, carries a
set of defence-in-depth security headers. This is an application-layer control (OWASP Top 10 A02: Security
Misconfiguration) that reduces the impact of cross-site scripting, clickjacking, and MIME-sniffing attacks, and
requires no configuration.

```jim-diagram
archetype: flow
title: How JIM's security headers reach the browser
desc: "Every response from JIM's web application, whether the portal, a static asset or the REST API, gets five security headers: the Content Security Policy and four more. Where something more specific sets one of them, its value wins. A reverse proxy in front of JIM, if there is one, passes the headers on unchanged unless it is configured to strip or rewrite them. The browser then applies them."
in: {label: JIM response, note: 'portal, asset or API'}
steps:
- {id: headers, label: Security headers, sub: 'CSP and four more', note: 'a more specific value wins'}
- {id: proxy, label: Reverse proxy, sub: if there is one, note: 'passes them on unless set to strip'}
out: {label: Browser, note: applies them}
```

## :material-shield-lock-outline: Headers sent

| Header | Value | Purpose |
|--------|-------|---------|
| `Content-Security-Policy` | See [below](#content-security-policy) | Restricts which origins scripts, styles, images, fonts and connections may load from |
| `X-Content-Type-Options` | `nosniff` | Stops browsers guessing a response's content type, preventing MIME-sniffing attacks |
| `X-Frame-Options` | `DENY` | Stops JIM being embedded in an `<iframe>` on another site (clickjacking protection) |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | Limits how much of JIM's URL is leaked in the `Referer` header of outbound links |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` | Disables browser APIs JIM never uses |

JIM sets these defaults as a request enters its pipeline. A handler that runs after them (a controller action, for
example) and sets one of these headers replaces JIM's default, so the more specific value wins.

## :material-lock-check-outline: Content Security Policy

JIM's Content Security Policy (CSP) is a restrictive, same-origin-only policy:

```text
default-src 'self';
script-src 'self' 'unsafe-inline';
style-src 'self' 'unsafe-inline';
img-src 'self' data:;
font-src 'self';
connect-src 'self';
frame-ancestors 'none';
base-uri 'self';
form-action 'self'
```

Everything (scripts, styles, images, fonts, WebSocket connections) is restricted to JIM's own origin. JIM is
designed for [air-gapped deployment](deployment.md#air-gapped-deployment), so nothing in the default policy
depends on a third-party host, CDN, or external font provider.

`'unsafe-inline'` is present for `script-src` and `style-src` because the portal's reconnection handling and its
component styling rely on inline `<script>` and `style="..."` usage.

`connect-src 'self'` covers the WebSocket connection that keeps the portal live, over plain HTTP (`ws:`) and HTTPS
(`wss:`) alike, and allows no WebSocket connection to any other host.

The API reference at `/api/reference` is the one exception. Its **Try It** signs you in from the browser and asks your
identity provider's token endpoint for the token itself, so on that page alone `connect-src` also allows the token
endpoint's origin, for example `connect-src 'self' https://adfs.example.com`. JIM takes the endpoint from your identity
provider's own configuration, as the API reference does, and adds only its origin. When JIM cannot read that
configuration, the page has the policy above, and **Try It** works only with an API key.

## :material-lan: Reverse proxies and corporate networks

If you put a reverse proxy in front of JIM (see [TLS and Reverse Proxy](tls-and-reverse-proxy.md)),
JIM's headers pass through unchanged unless your proxy is explicitly configured to strip or rewrite them. A
corporate web proxy or browser security extension that inspects or blocks unrecognised `Content-Security-Policy`
directives may need an allowance for JIM's origin; the policy above, with the API reference's one addition, is the
complete list of directives JIM sends,
useful if you need to document it for a network security review.

## :material-book-open-variant-outline: See also

- [Rate Limiting](../api/rate-limiting.md): the other REST API hardening control from the same security assessment
- [Deployment](deployment.md): HTTPS certificates and reverse proxy configuration
