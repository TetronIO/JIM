# Slim base images (chiseled / alpine / distroless): assessment for JIM

## Status: Assessed 2026-05-13. No migration recommended for `JIM.Web` or `JIM.Worker`. Optional pilot proposed for `JIM.Scheduler`. No action taken; recorded so we do not redo this analysis.

## Why this came up

After the May 2026 round of Dependabot `dotnet/{aspnet,runtime,sdk}:10.0-noble` digest bumps (#726-#731, plus the apt-package retunes in #732/#733 unrelated to this), 9 of the 55 GitHub code-scanning alerts that Trivy raised against the old digests still appear on the new digests. The surviving alerts are:

| CVE | Affected package | Images |
|---|---|---|
| `CVE-2026-2219` (medium) | `dpkg-deb` | aspnet, runtime, sdk |
| `CVE-2026-4878` (medium) | `libcap` (TOCTOU race in `cap_set_file`) | aspnet, runtime, sdk |
| `CVE-2026-5958` (medium) | `sed` (`-i` + `--follow-symlinks` race) | aspnet, runtime, sdk |

All three live in the Ubuntu 24.04 (Noble) base layer of Microsoft's `:10.0-noble` images, not in .NET itself. The other 46 alerts were genuinely cleared by the digest bumps; Trivy stopped reporting them once main moved past commit `dfc51d9f`.

The question this note answers: does it make sense to swap our production base images for a slimmer Microsoft variant (chiseled or alpine) to eliminate that residual OS attack surface?

## What "slim base images" actually means

Microsoft publishes three relevant variants of each .NET image alongside the full `:10.0-noble` tag we use today:

| Tag | What it is | What it ships |
|---|---|---|
| `:10.0-noble` (current) | Full Ubuntu 24.04 LTS base. ~200 MB. | glibc, apt, bash, coreutils, curl, ping, package manager, everything. |
| `:10.0-alpine` | Alpine Linux base. ~50 MB. | musl libc, busybox shell, `apk` package manager, small base. |
| `:10.0-noble-chiseled` | Microsoft's "distroless" answer. Built from Ubuntu Noble but stripped to bare minimum. ~30-40 MB. | glibc, .NET runtime, a small set of shared libraries. **No shell. No package manager. No curl / ping / ls.** Non-root by default. |

A fourth option, Google's `gcr.io/distroless/*`, is conceptually identical to chiseled but is not Microsoft-maintained and is less common in the .NET ecosystem. We can dismiss it.

The point of any of these variants is that the surface Trivy scans shrinks: fewer apt packages installed means fewer CVE bindings, and many OS-level CVEs simply do not apply because the affected utilities are not in the image.

## What each JIM image actually requires

This is where the picture diverges sharply between the three services.

### `JIM.Scheduler` ([src/JIM.Scheduler/Dockerfile](../../src/JIM.Scheduler/Dockerfile))

- **Zero apt packages installed.** The Dockerfile only creates `/data/keys` and `/var/log/jim`, then switches to the built-in `app` user.
- Talks to PostgreSQL via the managed Npgsql driver; that's it. No LDAP, no Kerberos, no SMB, no OS-level dependencies.
- Could move to `:10.0-noble-chiseled` with effectively a one-line `FROM` change. The chiseled image already ships the `app` user.
- Effort: ~30 minutes including a smoke test in the integration stack.

### `JIM.Web` ([src/JIM.Web/Dockerfile](../../src/JIM.Web/Dockerfile))

- Installs `libldap-common`, `libldap2` (both version-pinned), `iputils-ping`, `curl`.
- Symlinks `libldap-2.5.so.0` → `libldap.so.2` to work around [dotnet/runtime#123676](https://github.com/dotnet/runtime/issues/123676) (managed LDAP code in .NET 10 loads `libldap-2.5.so.0` by name; Noble ships `libldap.so.2`).
- LDAP support is load-bearing: file-browser / connector-configuration flows for AD/LDAP deployments depend on it.
- iputils-ping and curl are diagnostic-only (unpinned per the standing policy) and could be dropped on a slim variant.

### `JIM.Worker` ([src/JIM.Worker/Dockerfile](../../src/JIM.Worker/Dockerfile))

- Installs `libldap-common`, `libldap2`, `cifs-utils`, `libgssapi-krb5-2`; all four version-pinned. Same `libldap-2.5.so.0` symlink workaround as Web.
- All four packages are load-bearing for sync operations: AD/LDAP attribute sync, SMB/CIFS file-share connectors, Kerberos authentication for both.

So the spread is: one image (`JIM.Scheduler`) has nothing to lose by going chiseled, and two images (`JIM.Web`, `JIM.Worker`) carry the full LDAP / Kerberos / SMB toolchain that JIM exists to drive.

## Migration cost for the load-bearing images

If we wanted to chiseled-ify `JIM.Web` and `JIM.Worker` anyway, the two viable approaches are:

1. **Multi-stage `.so` extraction.** Use Noble as a "package builder" stage that `apt-get install`s libldap / cifs-utils / krb5, then `COPY --from=builder /usr/lib/.../libldap.so.2 …` into a chiseled final stage. Each base-image bump from Microsoft potentially shifts the dynamic-linker search path or symbol versions, so this extraction layer needs revalidation every time. The libldap SONAME workaround on line 41 of the current Worker Dockerfile is a foretaste of that maintenance: we already paper over one .NET ↔ libldap version mismatch; multi-stage extraction adds two more failure modes (missing transitive dependency, ABI drift between builder and runtime).

2. **Switch to Alpine.** `:10.0-alpine` exists, and Alpine packages `openldap-libs`, `cifs-utils`, and `krb5`. The trade-off is:
   - **musl libc, not glibc.** .NET works fine on musl, but globalisation, networking edge cases, and some native interop have historically surfaced musl-only quirks. For a product whose value proposition is "reliably synchronises identity data with hostile real-world directories", an unbounded class of musl-only behavioural differences is risk we have not budgeted for.
   - **Different Kerberos implementation conventions.** Alpine packages MIT Kerberos like Ubuntu, but ccache defaults, default ticket-encryption types, and SMB option parsing differ subtly. We would need to regression-test every connector (AD, generic LDAP, file shares) against the new base.
   - **Pinning ergonomics.** Alpine's `apk` does not offer the `=2.6.10+dfsg-0ubuntu0.24.04.1` style version pinning we standardised on; we'd need to rewrite the pinning convention and the `scan-base-images` CI job that enforces it.

Neither path is cheap; both put new variability into the path JIM customers care about most.

## JIM-specific negative trade-offs

The general arguments for chiseled / distroless (smaller images, fewer CVEs, faster pulls) are real. The arguments against, for JIM specifically:

1. **LDAP is the load-bearing path.** The existing libldap symlink hack documents that we have already eaten one .NET / LDAP library compatibility issue. Switching libc (Alpine) or losing apt reproducibility (chiseled `.so` extraction) compounds that risk in exactly the layer we can least afford to be flaky. JIM is silently broken to a customer the day AD sync starts producing wrong attribute values; we should not introduce knowable variability there for an OS CVE shave.

2. **CIFS / Kerberos behavioural differences.** Alpine packages `cifs-utils` and `krb5`, but credential-cache defaults, SMB option parsing, and Kerberos ticket-encryption defaults are not byte-for-byte the same as Ubuntu. Every connector that uses those packages becomes a regression-test target permanently, not just at migration time.

3. **The pinning policy fights us.** `engineering/CLAUDE.md` and the root `CLAUDE.md` enshrine `=2.6.10+dfsg-0ubuntu0.24.04.1`-style apt pinning as a hard rule. Alpine's `apk` does not offer the same pinning surface. The `scan-base-images` CI job and the `.trivyignore` workflow would both need rewriting, and supply-chain auditors expecting Debian-style pinned packages would need a different explanation.

4. **Customer-side debuggability shrinks.** JIM ships to healthcare, finance, and government deployments. When something breaks on a customer site, `docker exec -it jim.web sh` is an actual triage tool. Chiseled removes it entirely; Alpine gives `ash` (busybox) with different flag semantics from coreutils. Surprise factor during incident response is non-trivial.

5. **The reward is asymmetric.** The 9 surviving alerts are medium-severity, in `dpkg-deb` / `libcap` / `sed`. None are reachable from the application path: an attacker would need code execution as the `app` user already to exploit them locally. Microsoft refreshes its `:10.0-noble` base image whenever Ubuntu ships security updates to those packages; the alerts will clear in a future digest bump without us doing anything. We would be trading **permanent operational complexity** for **transient CVE relief**.

## Recommendation

**Do not migrate `JIM.Web` or `JIM.Worker`.** The trade-off math is bad: significant added complexity in the load-bearing LDAP / CIFS / Kerberos path, in exchange for three medium CVEs that are not reachable and that will roll off the next Microsoft image refresh anyway.

**Optional pilot: chiseled `JIM.Scheduler`.** Zero apt dependencies, smallest blast radius, easy to revert. A one-Dockerfile change that lets us:
- Get a release cycle of hands-on experience with shipping a shell-less image to customers (do field engineers complain? does the integration test runner have problems? do logs still cover what we need?)
- Drop ~1/3 of the surviving OS CVE alerts on each future bump cycle.
- Generate evidence for whether `JIM.Web` / `JIM.Worker` migration is worth revisiting.

If the Scheduler pilot is uneventful for one release, we have new data. If it produces operational friction, we have learnt cheaply.

**Do nothing path** is also defensible: the 9 surviving CVEs are low blast-radius, Microsoft's image-refresh cadence will clear them, and we have higher-leverage work to do.

## Revisit triggers

This note should be re-evaluated if:

- Microsoft deprecates `:10.0-noble` in favour of a chiseled-default tag.
- A new high-severity CVE lands in an Ubuntu base package that is *also* reachable from the JIM application path; the calculus changes once severity or reachability rises.
- A customer raises a hardening requirement that explicitly mandates distroless / chiseled images.
- The libldap workaround on line 41 of `JIM.Worker/Dockerfile` finally becomes maintainable via a clean upstream fix; that removes one of the brittleness arguments against the chiseled extraction path.

## References

- Microsoft chiseled images: <https://devblogs.microsoft.com/dotnet/announcing-dotnet-chiseled-containers/>
- .NET LDAP SONAME issue: <https://github.com/dotnet/runtime/issues/123676>
- Source Dockerfiles: [src/JIM.Web/Dockerfile](../../src/JIM.Web/Dockerfile), [src/JIM.Worker/Dockerfile](../../src/JIM.Worker/Dockerfile), [src/JIM.Scheduler/Dockerfile](../../src/JIM.Scheduler/Dockerfile)
- Trivy alert state at time of writing: 55 open, 46 stale (cleared by #726-#731 bumps), 9 still present on `dfc51d9f`
