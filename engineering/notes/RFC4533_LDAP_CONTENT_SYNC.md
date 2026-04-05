# RFC 4533; LDAP Content Synchronisation (syncrepl)

## Status: Research Note (2026-04-01)

Institutional knowledge captured during OpenLDAP import concurrency work. This documents a potential future evolution of JIM's OpenLDAP import strategy from the current Pattern B (connection-per-combo parallelism) to native LDAP Content Sync. No backlog issue; this is a reference note, not a commitment.

## Context

OpenLDAP's RFC 2696 Simple Paged Results implementation has a connection-scoped paging cookie limitation: any new search on the same connection invalidates all outstanding paging cursors. JIM currently works around this by giving each container+objectType combo its own dedicated LdapConnection and running them in parallel (Pattern B, implemented in `LdapConnectorImport.GetFullImportObjectsParallel`).

Pattern B is the industry-standard approach and performs well for typical deployments (2-12 combos). However, RFC 4533 would be the ideal long-term solution because it eliminates paging entirely and provides native change tracking.

## What is RFC 4533?

RFC 4533 defines the LDAP Content Synchronisation Operation (syncrepl). It is OpenLDAP's native replication protocol, designed specifically for keeping a consumer in sync with a provider.

**Two modes:**
- **refreshOnly**: polling mode. Consumer sends a Sync Request with an optional cookie. Provider returns all content (if no cookie) or only changes since the cookie (if cookie provided). Functionally equivalent to full import + delta import without paging.
- **refreshAndPersist**: streaming mode. After the initial refresh phase, the provider keeps the connection open and pushes changes in real-time. This would map to a "live sync" capability.

**Key advantages over current approach:**
- Initial `refreshOnly` with no cookie performs a full content transfer without paging cookies; completely sidesteps the RFC 2696 limitation
- Subsequent calls with a sync cookie return only incremental changes (adds, modifies, deletes); a natural delta import that is more reliable than accesslog parsing
- The server manages the change tracking state, not the client; eliminates the accesslog watermark complexity and the `olcSizeLimit` edge cases we handle today
- No connection pooling needed; a single connection handles the full sync session

## LDAP Protocol Details

**Request control OID:** `1.3.6.1.4.1.4203.1.9.1.1` (Sync Request)
**Response control OIDs:**
- `1.3.6.1.4.1.4203.1.9.1.2` (Sync State); attached to each entry, indicates add/modify/delete/present
- `1.3.6.1.4.1.4203.1.9.1.3` (Sync Done); attached to the final SearchResultDone message, contains the updated sync cookie

**Message flow (refreshOnly):**
```
Client                          Server
  |                               |
  |--- SearchRequest ------------>|  (with Sync Request control, mode=refreshOnly)
  |                               |
  |<-- SearchResultEntry ---------|  (with Sync State control: add/modify)
  |<-- SearchResultEntry ---------|  (with Sync State control: add/modify)
  |    ...                        |
  |<-- SearchResultDone ----------|  (with Sync Done control: new cookie)
  |                               |
```

**Sync cookie:** Opaque blob managed by the server. Contains enough state for the server to determine what has changed since the cookie was issued. Must be stored by JIM between imports (maps naturally to `PersistedConnectorData`).

## Implementation Effort

**The main barrier is the lack of .NET library support.** `System.DirectoryServices.Protocols` does not implement RFC 4533 natively. Implementations exist in:
- **Java**: Ldaptive, Apache Directory LDAP API, LSC Project
- **Python**: python-ldap syncrepl module
- **Go**: Various in-progress implementations

**What JIM would need to build:**
1. Custom `DirectoryControl` subclasses for Sync Request, Sync State, and Sync Done controls
2. BER encoding/decoding for the control values (ASN.1 structures defined in RFC 4533 Section 2)
3. Cookie storage and lifecycle management (straightforward; maps to existing `PersistedConnectorData`)
4. Entry processing that handles the four Sync State modes: add, modify, delete, present

**Estimated complexity:** Medium-high. The BER encoding/decoding is the hardest part; the rest maps cleanly to existing JIM import abstractions. Could reference the Ldaptive Java implementation as a guide.

## Who Uses syncrepl in Production?

- **OpenLDAP's own multi-master replication**: syncrepl is the foundation of OpenLDAP's replication architecture
- **LSC Project** (LDAP Synchronization Connector); uses syncrepl as its preferred sync mechanism for OpenLDAP sources
- **FreeIPA**: uses syncrepl internally for 389 DS replication
- **Keycloak**: does NOT use syncrepl; uses Simple Paged Results with dedicated connections (same pattern as JIM's Pattern B)

## When Would This Be Worth Implementing?

Consider if any of these become true:
- Customers report that accesslog-based delta imports are unreliable at scale (olcSizeLimit edge cases, accesslog database corruption)
- Customers need real-time sync capabilities (refreshAndPersist mode)
- A .NET library adds RFC 4533 support, eliminating the custom BER encoding work
- OpenLDAP becomes a primary target directory (rather than secondary to AD)

## References

- [RFC 4533; LDAP Content Synchronization Operation](https://datatracker.ietf.org/doc/html/rfc4533)
- [RFC 2696; LDAP Simple Paged Results](https://www.rfc-editor.org/rfc/rfc2696.html)
- [Ldaptive SyncRepl implementation (Java)](https://www.ldaptive.org/docs/guide/operations/search.html#sync-repl)
- [OpenLDAP Replication documentation](https://www.openldap.org/doc/admin26/replication.html)
- [LSC Project syncrepl source connector](https://www.lsc-project.org/documentation/latest/about.html)
