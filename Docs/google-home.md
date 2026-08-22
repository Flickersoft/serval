# Cameras in Google Home

*"Hey Google, show the front door on the Kitchen display."* Serval can present its cameras to
Google Home as a cloud-to-cloud integration, so they appear in the Home app and can be put on a
Nest Hub by voice. Televisions are a separate story — Google will not send a camera to one, and
[casting from the app](#cameras-on-a-television) is what does.

It is **off by default, and without prerequisites, you cannot turn it on.** The prerequisites below are real ones,
not recommendations — Google will not talk to a server it cannot reach over HTTPS with a valid
certificate. Read the next section before doing anything else.

## Before you start

You need all three:

- **A public HTTPS URL that reaches this server, with a certificate a browser trusts.** Google's
  servers make the calls, so a VPN does not help and a self-signed certificate is refused. A
  reverse proxy with a real domain, or a tunnel — see [what to expose](#what-to-expose).
- **A Nest Hub display, on the same network as Serval.** That is where Google will send a camera by
  voice, and it plays it live. **Not a television** — Google refuses those, whoever the camera
  vendor is; see [cameras on a television](#cameras-on-a-television) for the way round it. Not
  phones or speakers either. The picture travels straight from Serval to the display, so the two
  have to be able to reach each other: one on a guest VLAN, or at somebody else's house, will not
  connect.
- **Live view switched on**, since that is what actually serves the picture. It is on by default.

A Google account, and about twenty minutes.

## What leaves your network

**No video ever passes through Google.**

Google calls this server to list your cameras, to ask whether they are online, and — when you ask
to see one — to set up the connection. What Google is told is: your camera **names and rooms**,
whether each one is currently working, and when you ask to watch one. No video, no stills, no
clips, no detections, no transcripts.

Where the picture then goes depends on what you are watching:

- **Live — entirely on your LAN.** A Nest Hub, or a television you cast to. It is WebRTC: the
  picture goes straight from Serval to the screen over your own network, never through Google and
  never out through your public address. Only the signalling crosses.
- **A recording cast to a television — over your own public address.** The television fetches it
  from the same HTTPS address you published, so that footage goes out through your reverse proxy
  and back. Still never to Google, but no longer only over your LAN. Each request carries an
  expiring token, described under [what to expose](#what-to-expose).

That second case is the argument for exposing as little of Serval as the thing you want will run on.

## What to expose

**Google needs `/api/google/*` and nothing else.** Everything under that path authenticates itself,
so it is safe to publish while the rest of Serval stays on your LAN. That is the narrowest
arrangement and the one to prefer. It covers the Home app, a Nest display, and casting the **live**
view to a television.

**Casting a recording needs `/api/cameras/*/cast*` as well.** Those two routes — the playlist and
its transcoded segments — are not under `/api/google/`, because they are not Google's; the App asks
for them and the television fetches them directly.

Weigh that one properly, because it is **not** the same credential as the rest of this page. The
Google routes take a ticket good for one camera. These take `?stream_token=`, the ordinary playback
token the App's own video player uses: signed for an *account* rather than a camera, and valid for
ten minutes, so whoever holds one can fetch any camera's recordings for that long. Publishing the
App already carries exactly that, on `/api/cameras/*/vod.m3u8`; the point is only that it is more
than `/api/google/*` alone. Leave these unexposed and live casting still works — a recording just
will not play.

Expose more only if you also want to reach the App and your cameras from outside. That is a
separate decision with its own consequences, and [deployment.md](deployment.md#tls-and-exposure)
covers them: Serval's defaults assume a trusted network, and two of them — the CORS origins and the
OpenAPI page — stop being safe the moment anything is public.

Either way, the port the video itself is served on can stay off the internet entirely. The display
reaches it over your own network, not through your proxy.

## Setting it up

1. **Get a public HTTPS URL to this server**, per the section above, and confirm it reaches Serval
   from outside your network before going on. A certificate error or a timeout here will look like
   a Google problem later.

2. **Generate a client id and a client secret** — two long random values, from
   `openssl rand -base64 32` or anything else you trust. You will need both in step 4, and again
   in step 7.

   **These are values you invent, not values Google gives you.** Both are secrets — the client id
   included, which is unusual for OAuth but is the case here, because it is the only thing standing
   between a stranger's Google account and your cameras. Do not name it after your house, and keep
   both somewhere safe.

3. **Create a project** at the [Google Home Developer Console](https://console.home.google.com) and
   note its **project id** — the identifier, not the display name.

4. **Configure it.** In the `.env` file beside your `docker-compose.yml`, alongside the values
   already there:

   ```
   SERVAL_GOOGLE_ENABLED=true
   SERVAL_GOOGLE_PUBLIC_BASE_URL=https://serval.example.com
   SERVAL_GOOGLE_PROJECT_ID=your-project-id
   SERVAL_GOOGLE_CLIENT_ID=the-value-from-step-2
   SERVAL_GOOGLE_CLIENT_SECRET=the-other-value-from-step-2
   ```

5. **Recreate the container — restarting it is not enough.** A restart reuses the container that
   already exists, and its environment was fixed when it was created, so new values in `.env` are
   simply not read. From the directory holding your `docker-compose.yml`:

   ```
   docker compose up -d server
   ```

   **This replaces the container even though the image has not changed.** Compose compares the
   running container against the service definition — `.env` values included — and recreates it
   when they differ, so there is nothing else to pull or rebuild. `docker restart` and the
   *Restart* button in a management UI both reuse the existing container and carry the old values
   forward; the symptom is a card that keeps reporting a setting you are sure you already fixed.

6. **Check what the App says.** *Settings → Server status*, at the bottom of the page. The
   **Google Home** card appears there only once `SERVAL_GOOGLE_ENABLED=true` has taken effect, so
   its appearing at all is the first confirmation step 5 worked. It shows one of three states:

   | The card says | What it means | What to do |
   |---|---|---|
   | *nothing — no card* | The server is still running with `SERVAL_GOOGLE_ENABLED` off | Step 5 did not take. Recreate the container rather than restarting it |
   | *Not active* | Switched on, but one value is still wrong. The card names which | Fix that value, then step 5 again |
   | **Ready — no account linked** | **Everything on this side is correct.** Serval is waiting for Google | **Carry on to step 7** — this is where you should be |

   *Ready — no account linked* is as far as this side can get on its own: nothing has linked yet,
   so there is nothing more to report. It becomes *Active* after step 9, and that is the only
   difference between the two. The server log says the same thing at startup.

7. **Tell Google where this server is.** Back in the [Google Home Developer
   Console](https://console.home.google.com) — not the Cloud console, which is only step 8 — open
   the project from step 3 and click **Add cloud-to-cloud integration**. The first time through it
   shows a resources page and a checklist; click past them (*Next: Develop*, then *Next: Setup*)
   to reach **Setup and configuration**, which is the page everything below lives on.

   Give the integration a name and choose **Camera** as the device type, then fill in these two
   sections — they are separate parts of the same page, which is the bit that catches people out:

   **Account linking**

   | Field | Value |
   |---|---|
   | Client ID | the client id from step 2 |
   | Client secret | the client secret from step 2 |
   | Authorization URL | `https://serval.example.com/api/google/oauth/authorize` |
   | Token URL | `https://serval.example.com/api/google/oauth/token` |

   **Cloud fulfillment URL**

   | Field | Value |
   |---|---|
   | Cloud fulfillment URL | `https://serval.example.com/api/google/fulfillment` |

   Substitute your own address for `serval.example.com` — it must match
   `SERVAL_GOOGLE_PUBLIC_BASE_URL` exactly. Then **Save**.

   None of the three URLs contains a secret, so they are safe to paste into a support thread or a
   screenshot. The client id and secret are not.

8. ***(Optional)* Add a HomeGraph key** so camera changes reach Google on their own. In the
   [Google Cloud console](https://console.cloud.google.com): enable the **HomeGraph API** for the
   project → *Service accounts* → create one with the role **Service Account OpenID Connect
   Identity Token Creator** → *Add key* → *Create new key* → **JSON**.

   Put the downloaded file in a `secrets` directory beside your `docker-compose.yml`, uncomment
   the `./secrets` volume line in that file, and add to `.env`:

   ```
   SERVAL_GOOGLE_HOMEGRAPH_KEY=/app/secrets/homegraph.json
   ```

   Then `docker compose up -d server` again — same reason as step 5, and this one also adds a
   volume, which a restart cannot do at all. The card's *Camera changes* line is what confirms the
   key loaded.

   **This is the only file Google gives you, and it is a live credential** — treat it the way you
   treat a password, and keep it out of anywhere it might be shared or backed up in the open. See
   [the key from Google](#the-key-from-google) for what it does and does not buy.

9. **Link it.** Google Home app → *Add* → *Works with Google* → find your project, listed as
   *[test] «your project name»*. The webview will flash past without asking you to sign in or to
   approve anything — that is expected. Your cameras should appear.

   Back on *Settings → Server status*, the card should now read **Active** and name the account,
   with *Google last called* showing a time. That is the whole flow done. If it still says *Ready
   — no account linked*, Google never reached this server: the linking step failed rather than the
   cameras being slow to show up, and [when it does not work](#when-it-does-not-work) starts with
   the usual causes.

   Then: *"Hey Google, show the front door on the Kitchen display."*

The integration stays in test mode indefinitely, which is all a personal deployment needs. Google's
certification process is for publishing an integration to other people.

## The key from Google

The HomeGraph service-account JSON is the one credential that comes **from** Google, and the one to
keep safe: anyone holding it can act as your integration. It is optional, and it buys exactly one
thing — letting Serval tell Google to re-read your camera list.

Without it everything works — linking, the camera list, streaming — but Google only learns about a
camera you added or renamed when you re-link, or say *"Hey Google, sync my devices"*. That is the
whole difference.

## Cameras on a television

**Optional, and everything above works without it.** Skip this section and your cameras still
appear in the Home app and still go on a Nest display by voice.

**Google will not put a camera on a television.** Not Serval's cameras and not anyone's — asking the
Assistant to show a camera on a TV is refused before this server is ever called, and the same
refusal happens for other vendors' certified integrations. Google routes camera streams to Nest
displays and to the Home app, and that is the whole of it. Nothing you can configure here changes
that.

What does work is casting **from the Serval app**, which skips the Assistant and talks to the Cast
device directly. Open a camera in a browser and press *Cast*. That needs two things:

- **A Cast application of your own**, registered against the receiver this server already hosts.
  Nothing is hosted by the Serval project and no application id ships with it — the page comes from
  your own server, and you register the application against your own address.
- **The app open over HTTPS**, on Chrome or Edge — desktop or Android. Google's sender SDK is absent
  on Safari, on Firefox and on iOS, and it will not start over plain `http://`, so the button is
  simply not shown there.

What plays depends on what is on screen when you press it:

- **The live view** goes over **WebRTC**, sub-second, straight from go2rtc to the television over
  your LAN. Nothing is transcoded and nothing crosses your public address but the signalling.
- **A recording**, if you have scrubbed back, plays from where you are and keeps going. Recordings
  are the camera's main stream — 4K, or a portrait doorbell — and no Cast device can decode that, so
  they are re-encoded to 1080p H.264 as they play. Only what you actually watch is encoded.
- **The timeline still steers it.** Clicking the scrubber moves the television as well as the
  screen in front of you. What was sent is the whole of the timeline you are looking at, up to six
  hours of it, so a click anywhere on the bar is a jump in what is already playing rather than a
  fresh cast — and *Live* switches the television back to the live camera.

It costs a **one-time $5 Google Cast developer fee** and about ten minutes:

1. Sign up at the [Google Cast SDK Developer Console](https://cast.google.com/publish) and pay the
   one-time fee.
2. **Add new application → Custom Receiver.** Name it whatever you like. For the receiver URL, use
   your own address with this path:

   ```
   https://serval.example.com/api/google/camerastream/receiver
   ```

   Your server already answers there — the page is served whether or not this is set up, precisely
   so you have a working URL to register.
3. Copy the **Application ID** it gives you.
4. **Publish it**, or — while testing — register the **serial number** of your device under *Cast
   Receiver Devices* and tick *send your serial number to Google* in that device's settings. An
   unpublished receiver loads only on registered devices.
5. **Reboot the television.** Whether you published or registered a serial, the device caches what
   applications it can run, and until it restarts your receiver is invisible to it — the Cast button
   will not even appear, because the sender only discovers devices that can run the application it
   was given. Allow about fifteen minutes after a change before rebooting.
6. Put the application id in `.env` and recreate the container:

   ```
   SERVAL_GOOGLE_CAST_RECEIVER_APP_ID=1G2F89213HG
   ```

   ```
   docker compose up -d server
   ```

*Settings → Server status → Google Home* then reads **On a television: live, through this server's
own Cast receiver**, and a *Cast* button appears on the camera screen once a device is found.

Two things worth knowing about recordings on a television:

- **They are capped at 1080p**, by `SERVAL_GOOGLE_CAST_MAX_HEIGHT`, and by the screen itself. The
  receiver asks the television what it can decode and a 4K set answers honestly even when its panel
  is 1080p, so it takes the smaller of that answer and the screen's own size — then this server
  takes the smaller of *that* and the setting. At 2160p a segment is 9.4 MB and takes 1.25s to
  encode, so keeping up needs about 27 Mbit/s sustained through your public address, and it does
  not. At 1080p it is 2.5 MB and about 6 Mbit/s. Raise the setting only where the path is known to
  carry it, and only for a screen with the pixels to use it.
- **Live is unaffected either way.** It is WebRTC, never transcoded, and never leaves your LAN.

## Configuration

All of these go in `.env`, and **none of them can be changed from the App** — *Settings → Server
status* reports what they add up to, but does not edit them. Leave any of the five required
ones unset and the integration stays off.

| | |
|---|---|
| `SERVAL_GOOGLE_ENABLED` | `true` to turn it on. Off by default |
| `SERVAL_GOOGLE_PUBLIC_BASE_URL` | Your public address, e.g. `https://serval.example.com`. Must be `https` |
| `SERVAL_GOOGLE_PROJECT_ID` | The project id from the Google Home Developer Console |
| `SERVAL_GOOGLE_CLIENT_ID` | You generate it. A secret |
| `SERVAL_GOOGLE_CLIENT_SECRET` | You generate it. A secret |
| `SERVAL_GOOGLE_HOMEGRAPH_KEY` | Optional — path to the key file inside the container |
| `SERVAL_GOOGLE_CAST_RECEIVER_APP_ID` | Optional — your Cast application id, for [cameras on a television](#cameras-on-a-television) |
| `SERVAL_GOOGLE_CAST_MAX_HEIGHT` | Optional, default `1080`. The tallest a cast **recording** is transcoded to. Live is never transcoded |
| `SERVAL_GOOGLE_VERIFICATION_PIN` | Optional — set it to get a per-camera switch in the Home app. Asked for when switching a camera **off**, never on. Unset, no switch is offered. A secret |

## When it does not work

In roughly the order things actually go wrong:

- **A value you changed does not seem to have taken.** The container was restarted rather than
  recreated. A container's environment is fixed when it is created, so `docker restart` — and the
  *Restart* button in most management UIs — carries the old values forward. `docker compose up -d
  server` is what replaces it. This catches nearly everyone once.
- **The Assistant says "showing the front door" and the display just spins.** Almost always
  `SERVAL_WEBRTC_CANDIDATES` in `.env`. Left unset, Serval advertises an address the display cannot
  reach; set it to the LAN address of the machine Serval runs on, e.g.
  `SERVAL_WEBRTC_CANDIDATES=192.168.1.20:8666`. Live view in a browser needs the same setting, so
  if that already works on your network, look further down this list.
- **Still spinning, and live view works.** The display is not on the same network as Serval — a
  guest VLAN, an SSID that isolates clients, or a display somewhere else entirely.
- **Linking fails in the Google Home app.** The client id or the project id does not match between
  your `.env` and the *Account linking* section from step 7. The server log names which one.
- **Cameras do not appear after linking.** Only cameras that are **enabled** and have a **live**
  stream are offered. A file (test) camera never is.
- **A camera you renamed still shows its old name.** No HomeGraph key, or it did not load. Check
  *Settings → Server status → Google Home*, or say *"Hey Google, sync my devices"*.
- **The picture is poor, or takes a long time to appear.** Google wants 480p–1080p, H.264 or VP8.
  Point the camera's **live** stream at its sub stream: a 4K or HEVC stream has to be transcoded
  for every request, which is slow and expensive.
- **Nothing works at all, and Google reports the server as unavailable.** Something required is
  still missing or wrong. *Settings → Server status → Google Home* names the one thing it is.
- **The Cast button never appears.** The sender only finds devices that can run *your* Cast
  application, so a television that has not picked it up yet is invisible rather than listed. Reboot
  the television. Also check you are on `https://` — Google's sender SDK does not start over plain
  HTTP, and Chrome or Edge, since it does not exist on Safari, Firefox or iOS.
- **It casts, and the screen stays on the receiver's own splash.** The load arrived before the
  receiver had finished starting. The sender retries once by itself after four seconds; if it is
  still stuck, press Cast again — the second attempt reuses the receiver already running.
- **A recording plays, then does not.** Check that `/api/cameras/*/cast*` is reachable from outside
  — a proxy publishing only `/api/google/*` serves the live view perfectly and cannot fetch a single
  recorded segment.
- **The picture blinks and carries on.** That is the receiver recovering: a batch it could not
  decode, retried where it stopped, then a couple of seconds later, then past the batch. Three
  attempts, and it says so in the log. Nothing to do unless it becomes frequent.
- **Something on the television went wrong and there is nothing to see.** The receiver logs what it
  loaded, whether it got WebRTC or fell back, where the playhead was and any player error, to the
  device's own console. Reach it by attaching a debugger to the device, which an unpublished Cast
  application allows.

Worth knowing: the Assistant says "showing the front door" **before the display has connected to
anything**, so a dead camera and a network problem sound identical from the other side of the room.
The server log tells them apart.

## What it does not do

- **Put a camera on a television by voice.** Google refuses, before this server is called at all,
  and does the same to other vendors' certified integrations. Cast from the Serval app instead —
  see [cameras on a television](#cameras-on-a-television).
- **Show cameras on a speaker.** There is nothing to show them on.
- **Reach a display outside your network.** There is no relay; the display and Serval have to be
  able to talk to each other directly.
- **Link more than one Google account.** One Serval, one account.
- **Send motion or doorbell alerts to Google.** Alerts stay in Serval's own notifications — see
  [alerts.md](alerts.md).
- **Carry talk-back.** The two-way audio in [live-view.md](live-view.md#two-way-talk-back) works
  in the App only.
