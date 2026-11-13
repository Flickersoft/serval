# AI assistants (MCP)

*"What happened at the front door this afternoon?" "Did anyone mention the package?"* Serval can let
an AI assistant answer questions like these from your own cameras. It serves a
[Model Context Protocol](https://modelcontextprotocol.io) endpoint at `/mcp`, so any assistant that
speaks MCP can connect: Claude, Gemini, ChatGPT, editors such as VS Code and Cursor, and others.
Nothing in Serval is specific to one of them.

It is **off by default**, and it is **read-only**. An assistant can view camera stills and query
telemetry, alerts, and saved clips; it cannot modify settings, move PTZ cameras, create or delete
records, or mark alerts as read.

## Before you start

- **For a cloud assistant (claude.ai, ChatGPT), you need a public HTTPS URL that reaches this server,
  with a certificate a browser trusts.** The assistant's servers make the calls, so a VPN does not
  help. Use a reverse proxy with a real domain, or a tunnel; see [what to expose](#what-to-expose).
- **An assistant running on the server's own machine needs none of that.** The default address,
  `http://localhost:8080`, works for a command-line assistant such as Claude Code or Gemini CLI
  started on the same host.
- **Everyone who links an assistant needs a Serval account.** Linking happens in the App, while
  signed in.

## What leaves your network

**Unlike Google Home, this sends your footage's contents to a third party.** When an assistant calls a
tool, the answer goes to whichever company runs that assistant. That includes scene descriptions,
transcripts of what was said, detections, sound labels, alert titles, clip summaries, and **still
images** from the cameras. That company's privacy terms then apply.

What never leaves:
- video;
- anything at all until somebody signed in to Serval has approved that assistant;
- anything after they disconnect it.

## What to expose

**The assistant needs these:**

| Path | What calls it |
|---|---|
| `/mcp` | The assistant, for every tool call. |
| `/.well-known/oauth-protected-resource`, `/.well-known/oauth-protected-resource/mcp`, `/.well-known/oauth-authorization-server` | The assistant, once, to find out how to sign in. |
| `/api/mcp/oauth/*` | The assistant's servers (register, token) and your browser (authorize). |

**Approving an assistant also needs the App at the same address.** The authorize step sends your
browser to the App's `/connect` page, where you sign in and choose Allow or Deny. So the browser you
link from must reach the App's pages, `/api/auth/*`, and `/api/mcp/oauth/requests/*` at the public
address.

In practice that means publishing the App. The alternative, a split-horizon DNS name that resolves
to the LAN address at home, works only when you link from home. Either way, publishing the App
carries the consequences [deployment.md](deployment.md#tls-and-exposure) describes: set
`Serval__Cors__AllowedOrigins` and `Serval__OpenApi__Enabled=false`.

Every MCP route authenticates itself, and all of them answer 404 while the feature is off. The
anonymous ones are rate-limited per address. The routes an assistant calls allow any browser origin
without credentials, so tightening `Serval__Cors__AllowedOrigins` for the App does not shut out
browser-based MCP clients.

## Setting it up

1. **Publish the address** per the section above, and check it from outside your network.
2. **Settings → AI assistants**, as an Admin:
   - **Let AI assistants connect**: on.
   - **Address assistants reach this server on**: your public origin, e.g.
     `https://serval.example.com`. Give it no path, and use https (plain http is accepted only for
     `localhost`).
3. **Settings → Account** now shows an *AI assistants* section with the **connector address**,
   `https://serval.example.com/mcp`. That is the one value every assistant asks for.

Neither setting needs a restart. Changing the address ends every existing link, because each token is
bound to the address it was issued for.

## Connecting an assistant

Every MCP client uses the same OAuth 2.1 flow once configured with the connector address:

1. It finds Serval's sign-in endpoints from the address.
2. It identifies itself to Serval.
3. It opens your browser at Serval's `/connect` page.
4. You sign in if you need to, check who is asking, and choose **Allow**.
5. The browser goes back to the assistant, and it is connected.

The consent page shows the assistant's name, but a name is only the app's own claim. It also shows:
- **Verified as `domain`**, for an assistant that proved which domain it belongs to (a client ID
  metadata document hosted there).
- **Not verified**, in the warning colour, for one that registered itself, which anyone can do under
  any name. Only allow one of these if you started connecting it yourself, just now.
- **Returns to**, which is where the answer is being sent.

Examples:

- **claude.ai or Claude Desktop**: *Settings → Connectors → Add custom connector*, and paste the
  connector address.
- **Claude Code**: `claude mcp add --transport http serval https://serval.example.com/mcp`, then
  `/mcp` in a session to sign in.
- **Gemini CLI**: `gemini mcp add --transport http serval https://serval.example.com/mcp`, or in
  `settings.json`:
  ```json
  { "mcpServers": { "serval": { "httpUrl": "https://serval.example.com/mcp" } } }
  ```
  Then `/mcp auth serval` to sign in.

Any other client: look for "remote MCP server", "custom connector" or "Streamable HTTP", and give it
the connector address. It needs no client ID, secret or API key.

## What an assistant can do

| Tool | What it answers |
|---|---|
| `list_cameras` | Every camera's id, name and location, plus the server's current time and time zone. |
| `get_activity` | A timeline of what the cameras noticed in a window of up to 7 days, across cameras: scene descriptions, detections, speech, finished conversations, and sounds. |
| `search_activity` | The same records, filtered to those containing a word or phrase. Case-insensitive and literal, not semantic. Up to 7 days per call. |
| `list_alerts` | The alert queue, newest first. Reading it does not mark anything read. |
| `list_clips` | Saved clips with their summaries, searchable by name and transcript. |
| `get_snapshot` | A still from a camera, live or at a past instant where there is footage. |
| `get_alert_image`, `get_clip_image` | The frame behind an alert or a saved clip. |

Results carry links into the App, so the assistant can offer "watch it in Serval" instead of only a
description or a still:

| Result | Link opens |
|---|---|
| A camera in `list_cameras`, a live `get_snapshot` | The camera, live: `/camera/<id>` |
| An activity item, a past `get_snapshot` | That camera's recording 5 seconds before the moment, the same lead-in the App's own feed uses: `/camera/<id>?at=<UTC time>` |
| An alert, `get_alert_image` | The alert's card: `/alerts/<id>` |
| A clip, `get_clip_image` | The clip: `/clips/<id>` |

They are ordinary App addresses on the public address. Nothing in them is a credential: opening one
needs a signed-in account, and a signed-out browser is sent to sign in and then on to the link. The
image tools put the link in the caption, because some clients pass the caption to the model but drop
the picture.

Past frames come from the recording, or from the short preview buffer on a camera that is not
recording. Frames read from footage are scaled to `Serval:Ingest:SnapshotMaxMegapixels`, and at
most two are decoded at once. An alert's frame comes from the recording while it is still kept, and
falls back to the alert's small card thumbnail after that. A saved clip's frame comes from the clip
itself, which is never deleted.

## Linked assistants

*Settings → Account → AI assistants* lists every assistant **you** have linked, whether each one is
verified, and when it was last used. **Disconnect** ends its access at once: its tokens are deleted,
not left to expire.

Links are per person. Each acts with the account that approved it, and nobody else sees it in their
list. An Admin can switch the whole feature off, which stops every link. The list stays, so links
can still be disconnected before the feature is switched back on. A link also ends:
- when its account is deleted;
- when "sign out everywhere" is used, from the password form or the Admin's per-user action, or a
  configuration restore changes the account's password;
- when the public address changes, since every token is bound to the old one;
- when it goes unused for **Forget an unused assistant after** days (30 by default).

## Configuration

All four are ordinary settings under *Settings → AI assistants*, and can also be set in the
environment.

| Key | Default | |
|---|---|---|
| `Serval__Mcp__Enabled` | `false` | The switch. |
| `Serval__Mcp__PublicBaseUrl` | `http://localhost:8080` | The OAuth issuer, and the origin every token is bound to. Must be https, or http on a loopback host; no path. |
| `Serval__Mcp__AccessTokenMinutes` | `60` | How long an access token lasts before the assistant silently refreshes. |
| `Serval__Mcp__RefreshTokenDays` | `30` | How long a link survives unused. Each refresh restarts the clock. |

## When it does not work

- **The assistant says it cannot reach the server.** Check `https://<address>/.well-known/oauth-authorization-server`
  from outside your network. A 404 means the feature is off or the address setting is not a valid
  https origin; the *AI assistants* section on the Account screen names which.
- **The browser shows "Serval could not start connecting this assistant".** Serval refused the
  request before showing anything. Either the assistant's identity could not be checked (its
  metadata document was unreachable, or not a valid one), or it asked to return somewhere it never
  registered. The Server log has a line naming which.
- **"This request has ended" on the consent page.** Requests last ten minutes and can be answered
  once. Start connecting again from the assistant.
- **It connected, then stopped working after an address change.** Tokens are bound to the address
  they were issued for. Connect again.
- **It connected, then was disconnected on its own.** A refresh token was presented twice. Serval
  revokes the link when that happens, since it means two parties hold the credential. Connect again,
  and look for a second copy of the assistant's configuration somewhere.

## How linking works

Serval is its own OAuth 2.1 authorization server for `/mcp`, following the MCP authorization
specification:

- **Discovery.** `/mcp` answers 401 with `WWW-Authenticate: Bearer resource_metadata=…` (RFC 9728),
  which leads to the authorization server metadata (RFC 8414).
- **Client identity** comes in one of two ways:
  - A **client ID metadata document**: an https URL as `client_id`, fetched by the Server with
    redirects, private addresses and anything over 16 KB refused.
  - **Dynamic client registration** (RFC 7591), for clients that still use it. Registrations nobody
    links lapse after a day.

  Clients are public only: there are no client secrets, and PKCE (S256) is required.
- **Authorization.** The authorize endpoint checks the client, an exact redirect URI match (loopback
  redirects may differ in port), PKCE and the `resource` (RFC 8707). Until the client and redirect
  check out it never redirects. It then parks the request and sends the browser to the App's
  `/connect`. The answer returns to the client with `iss` (RFC 9207).
- **Tokens** are opaque, stored only as SHA-256 hashes, and bound to `{PublicBaseUrl}/mcp`.
  - Refresh tokens rotate on every use, and a reused one revokes the link.
  - An assistant's token works only on `/mcp`, and an App session's token does not work there, so
    linking an assistant never hands it the App's write routes.
