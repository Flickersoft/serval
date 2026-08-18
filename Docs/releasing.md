# Cutting a release

Serval versions as one thing: Server, CameraModule, Shared and the App ship from a single commit
and carry a single number. A release is a git tag and nothing else — push
`v<major>.<minor>.<patch>` and [server-image.yml](../.github/workflows/server-image.yml) builds the
image and moves the registry tags deployments follow.

[deployment.md](deployment.md#versions-and-image-tags) is the other half of this page: which tag to
pin, what each one moves with, and how to read the version back off a running Server. This one is
the seven steps from a merged pull request to a published release.

## 1. Land the work

Normal pull requests into `main`. Each merge publishes `edge` and its `sha-` tag and consumes no
version number, so there is no pressure to batch changes into a release.

## 2. Pick the number

While the major is `0`:

- **patch** — `v0.2.1` — fixes, nothing new to configure.
- **minor** — `v0.3.0` — a new capability, or anything that changes settings, schema, or behaviour
  that somebody has already set up.

Strangers run this now. A settings or schema break takes the minor bump and says so in the notes.

## 3. Pick the release candidate and freeze it

The commit that ships is chosen *before* testing starts, not after it. Testing takes as long as it
takes, `main` keeps moving while it does, and the commit finally tagged is normally several commits
behind the tip by then. That is the ordinary case rather than a compromise — the alternative is
tagging something nobody has used.

```bash
git checkout main && git pull
sha=$(git rev-parse HEAD)          # today's tip, or any earlier commit on main
git branch rc/v0.2.1 $sha
```

A shell variable does not last as long as a release candidate does, and `HEAD` will mean something
else tomorrow. The local branch is a marker and nothing more: never pushed, deleted after the tag.

Then confirm the checks on that exact commit, asking about the contexts branch protection requires
rather than the workflow that contains them:

```bash
gh api repos/Flickersoft/serval/commits/$sha/check-runs \
  --jq '.check_runs[] | "\(.name)\t\(.conclusion)"'
```

`dotnet` and `flutter` are the two that gate. `build` alongside them is this image workflow, having
already run on the push to `main`. `gh run list --repo Flickersoft/serval --commit "$sha"` is the
same answer expressed as runs.

Scoped to the commit rather than to `--branch main`: two workflows fire per push, so a branch
listing truncated to a few rows is already showing runs from the commit *before* this one, and it
carries no sha to notice that with.

## 4. Use the candidate before you tag it

The tag build has no gate of its own — it builds an image and pushes it. And CI is not a test of the
product: it compiles, runs the unit suites and checks the goldens, on a runner with no ffmpeg, no
Mongo, no models and no camera. Recording, playback, live view, detection, the App itself — nothing
in that list has been exercised at all by the time CI goes green.

The build to put in front of a person already exists. The push that merged the candidate published
`sha-<short>`, and that is the same image its tag will publish, differing only in the version
stamped into it. So there is nothing to build and nothing to wait for:

```bash
cd deploy
cp .env.example .env               # first run only; fill in the two secrets
echo "SERVAL_IMAGE=ghcr.io/flickersoft/serval-server:sha-${sha:0:7}" >> .env
docker compose up -d               # http://localhost:8080/
```

On a stack you can throw away, not on a deployment you depend on. The whole reason to run a release
candidate is that it might be wrong.

Then use it — the parts this release changed, and the walk-through worth repeating regardless:
cameras record and the timeline scrubs, live view connects, an alert arrives and its clip plays,
settings save and survive a restart. This is the step that decides whether the release ships, so it
is the one to leave time for.

**Work landing on `main` in the meantime changes nothing here.** The candidate's image was pushed
when its commit merged and stays in the registry regardless of what arrives after it, so a test run
that spans days is testing exactly what it started with.

**If the testing finds something, the candidate is spent.** The fix goes onto `main` as an ordinary
pull request and a new candidate is picked from the result — there is no release branch, and nothing
is cherry-picked back onto the old commit. Move the marker and start step 4 again:

```bash
git branch -f rc/v0.2.1 $sha
```

## 5. Tag the candidate and push the tag

```bash
git tag v0.2.1 rc/v0.2.1
git push origin v0.2.1
git branch -d rc/v0.2.1
```

The tag names the commit that was tested, not the tip. Whatever landed after it stays on `main` and
goes out in the next release — from the registry side that looks like `edge` sitting ahead of
`latest`, which is exactly right. `--generate-notes` in step 7 reads the history reachable from the
tag, so those later commits stay out of the notes too.

The `create` trigger fires, and the `if:` guard lets it through because the ref starts with
`refs/tags/v`. One tag per push: GitHub fires no event at all when more than three tags arrive
together, and the failure is silence rather than a red run.

## 6. Wait for the build, then verify the tags landed

```bash
gh run watch --repo Flickersoft/serval

curl -s -H "Authorization: Bearer $(curl -s 'https://ghcr.io/token?scope=repository:flickersoft/serval-server:pull&service=ghcr.io' | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')" \
  https://ghcr.io/v2/flickersoft/serval-server/tags/list
```

Expect `0.2.1`, `0.2` and `latest` all moved onto the new build. The listing proves the first two
exist; `latest` is in it either way, so compare digests if you want certainty about that one:

```bash
docker buildx imagetools inspect --format '{{.Manifest.Digest}}' ghcr.io/flickersoft/serval-server:latest
```

## 7. Cut the release

```bash
gh release create v0.2.1 --repo Flickersoft/serval --generate-notes --verify-tag
```

`--generate-notes` lists the pull requests merged since the previous release, which is real content
now that everything lands through one. `--verify-tag` fails on a typo instead of silently creating a
release at a tag that does not exist.

## Two rules that keep this from going wrong

**Never move or delete a published tag.** The tag drives the image build, so re-pointing it rebuilds
over a version people may already be running. Tagged the wrong commit? Cut the next patch.

**Never tag a commit whose CI has not passed, or that nobody has run.** The tag build only builds
the image — it does not re-run `dotnet` and `flutter`, which gate pull requests rather than tags, and
nothing anywhere gates on a person having used the thing. A commit that fails either test publishes
and drags `latest` onto itself. The CI half is worth a guard eventually — adding the test jobs as a
`needs:` on the tag path, rather than relying on remembering. The other half cannot be automated,
which is why it is step 4 rather than a footnote.

## Numbers that are not the release number

| Where | What it is |
| --- | --- |
| `version: 1.0.0+1` in [App/serval_app/pubspec.yaml](../App/serval_app/pubspec.yaml) | Flutter's project version, and nothing reads it — no build passes `--build-name`. The App is handed the real number as `--dart-define=SERVAL_VERSION` by the Dockerfile and reads it in [source_offer.dart](../App/serval_app/lib/models/source_offer.dart). |
| `0.0.0` and the `dev` suffix in [Directory.Build.props](../Directory.Build.props) | What a build made outside the workflow reports — not a fallback version so much as a refusal to invent one. |
| `schema_version` in the telemetry documents | The ingest contract, versioned on its own clock ([telemetry.md](telemetry.md)). |
