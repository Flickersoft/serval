# Security Policy

How to report a vulnerability in Serval, and what happens after you do.

## Reporting

**Do not open a public issue.** Serval handles video and audio from inside people's homes, and a
vulnerability disclosed publicly is a vulnerability being exploited.

Report it through [GitHub's private security advisory form](https://github.com/Flickersoft/serval/security/advisories/new)
on this repository. That form is private between you and the maintainers until an advisory is
published, and it is the only channel — there is no security mailing list.

## What to include

- The commit or image tag you are running (`sha-<commit>`; the running Server names its own commit
  under the source link in the app).
- Which component — Server, CameraModule, or App. They have different trust boundaries.
- Whether you reached it from the LAN, from a reverse proxy, or from an authenticated session, and
  at what role. Serval is built for a trusted LAN and serves plain HTTP by design, so "unauthenticated
  on the same subnet" is a different finding from "unauthenticated across the proxy".
- The request, the logs around it, and a reproduction if you have one.

## Scope

The documented deployment is the one that matters: the `deploy/` compose stack behind a LAN, or
behind a TLS reverse proxy per [Docs/deployment.md](../Docs/deployment.md#tls-and-exposure).

Serval put directly on the internet is out of scope — out of the box it allows any browser origin
and publishes its API documentation without a login, which the README and the deployment docs both
say plainly. Findings that amount to "this is exposed when deployed against that advice" are already
known and tracked in a private backlog; they become public issues as they are fixed.

Third-party components in the stack — MongoDB, go2rtc, ffmpeg — belong to their own projects.
Report a flaw in how Serval *uses* one here; report a flaw *in* one upstream.

## Response

Expect an acknowledgement within a few days. Serval is maintained by a small team, so a fix timeline
comes with the acknowledgement rather than before it. You will be credited in the advisory unless
you ask not to be.
