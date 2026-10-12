---
title: LDAP Connector Certificates
description: "Connect the LDAP Connector over LDAPS, how it validates the directory's certificate, and the network it needs."
type: object
audience: administrator
---

# LDAP Connector Certificates

The LDAP Connector should reach the directory over LDAPS, so credentials and identity data are encrypted in transit. This page covers using LDAPS, how JIM validates the directory's certificate, and the network the connection needs.

## Use LDAPS

LDAP traffic is unencrypted by default. In production environments, **always enable LDAPS** (SSL/TLS) to protect credentials and identity data in transit. Set the port to 636 and enable the "Use Secure Connection (LDAPS)?" setting.

## Certificate validation

When LDAPS is enabled, JIM validates the certificate the directory server presents. Three things are checked, and any one of them failing stops the connection before the service account's credentials are sent:

- **Chain**<br /> The certificate must chain to an issuer JIM trusts: either one in the operating system's trust store, or one added in **Admin > Certificates**.
- **Validity period**<br /> An expired or not-yet-valid certificate is rejected, including when its issuer is one you added yourself.
- **Name**<br /> The certificate must have been issued for the value in the Host setting. A certificate for `dc01.corp.local` is not accepted when JIM connects to `10.0.0.5`, or to `dc01`.

There is no per Connected System option to relax any of this.

### Trusting an internal certificate authority, or a self-signed certificate

**The quickest route is the certificate JIM already shows you.** When an LDAPS connection is refused because JIM does not trust the issuer, the certificate card shows the certificate chain JIM found, from the root down to the directory server's own certificate, and where each came from: sent by the server, or downloaded from the address the certificate below it names. Its **Choose what to trust** action lets you trust any certificate in that chain. JIM reads the chain from the directory server again, checks your choice is still in it, and adds it to the Trusted Certificates store. There is nothing to obtain, export or upload.

You are asked to confirm first, because this is a security decision: compare the thumbprint against the one the directory's administrator gives you. Every choice makes the connection work; they differ in how long the decision lasts. JIM recommends the root, because it survives the renewal of every certificate beneath it, including the directory server's own. Trusting the server's own certificate has to be repeated each time it is renewed. Where JIM had to download an intermediate authority to reach the one you chose, it stores that alongside, because the LDAP connection does not download certificates itself. A self-signed certificate has no separate authority, so there is only one thing to trust.

Reading the certificate again at the moment you confirm is what makes a change detectable. If the server is presenting something other than what you were shown, JIM trusts nothing and shows you both thumbprints; expected after a renewal, worth investigating otherwise.

**Fetch certificate** on the Connected System's settings does the same reading before anything has failed, so setting a new system up does not mean saving, failing and coming back. Fetching stores nothing.

You can still upload a certificate by hand via **Admin > Certificates**, which is the route to take when the directory is not reachable from JIM at the time you are configuring it. Both kinds work:

- **An internal certificate authority**<br /> Upload the root CA, plus any intermediates the directory server does not send. Every directory server whose certificate it issued is then trusted. Uploading an intermediate on its own also works, and covers what that intermediate issues.
- **The directory server's own self-signed certificate**<br /> Upload the server certificate itself. Only that certificate is then trusted, which is the tighter option where a directory has no certificate authority behind it.

Certificates added this way are trusted **in addition to** the operating system's trust store, so adding one never stops a publicly-issued or already-trusted certificate from working.

To do the same from a script:

```powershell
$reading = Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42
$reading.certificate.chain | Format-Table subject, source, downloadedFrom

Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
    -Thumbprint $reading.certificate.rootThumbprint `
    -ChangeReason 'Trusting the corporate root CA.'
```

### When the certificate name does not match the host you connect to

This is the common obstacle: a certificate issued for a fully-qualified name, in an environment where JIM cannot resolve that name, so the Host setting holds an IP address instead. Uploading the certificate does not help, because the name still does not match.

Rather than weakening validation, give JIM's containers a way to resolve the name. In Docker Compose, add a host entry to the `jim.web` and `jim.worker` services:

```yaml
services:
  jim.worker:
    extra_hosts:
      - "dc01.corp.local:10.0.0.5"
```

Set the Host setting to `dc01.corp.local`. The name now resolves inside the container, the certificate matches, and the connection is fully validated. This works when DNS is unavailable or unreliable, because the mapping is static and needs no name server. The alternative is to have the certificate reissued with the name (or IP address) you actually connect to.

!!! warning "Disabling validation entirely"
    OpenLDAP's own `LDAPTLS_REQCERT=never` environment variable is honoured by the LDAP client library JIM's containers use, and switches certificate validation off. It applies to the whole container, so it affects **every** LDAPS Connected System that container serves, and it cannot be scoped to one directory. JIM's development and integration test stacks do not set it: they trust each throw-away directory's own CA through the JIM certificate store instead, so LDAPS validation is exercised for real rather than bypassed. Never set this variable in production: it exposes the service account's credentials to anyone able to intercept the connection.

## Network Considerations

- Ensure firewall rules allow traffic from the JIM container to the directory server on the configured port (389 or 636).
- If JIM is running in a container, the directory server must be reachable from the container network. When using Docker Compose, this may require configuring the network mode or adding the directory server to the container's DNS resolution.
- For Active Directory environments, JIM connects to a single domain controller. Consider using a domain controller in the same network segment as JIM to minimise latency.
