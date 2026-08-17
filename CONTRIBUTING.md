# Contributing to Serval

Serval is a self-hosted security-camera system with on-device AI, owned by Flickersoft LLC.
Contributions are welcome — bug reports, documentation, and code.

Read [the README](README.md) first for what the system is and how the pieces fit. The
[What's missing](README.md#whats-missing) section is the honest list of what does not work yet,
and it is the best place to find something worth doing.

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you write code

**Open an issue first for anything substantial.** Much of Serval is shaped by constraints that
are not visible from the code — what a camera will actually give you over ONVIF, what the
detection gates cost per frame, why the settings catalogue lives on the Server. A short
conversation before a large change saves rework.

Small fixes — a typo, a broken link, an obvious bug — go straight to a pull request.

## The Contributor License Agreement

Serval requires every contributor to sign a Contributor License Agreement before their first
pull request can be merged. The agreement is [CLA.md](CLA.md).

The mechanics are automatic: open a pull request, and a bot comments with a sign-off phrase. Post
that phrase as a comment and the check goes green. It is one action, once — signing covers all
your future contributions unless the agreement changes version.

**What it means, plainly.** You keep ownership of your work. You grant Flickersoft LLC a broad
license to it, *including the right to sublicense* — which means Flickersoft LLC may distribute
your contribution under license terms other than the AGPL, including commercial terms. If that is
not acceptable to you, please do not submit code; issues and bug reports need no agreement, and
they are genuinely useful.

If you are contributing as part of your job, or your employment agreement assigns your
intellectual property to your employer, say so on the issue before you start. That needs a
Corporate CLA, which is a different document.

## Licensing of the project itself

Serval is distributed under the **GNU Affero General Public License, version 3 or later**
([LICENSE](LICENSE)), together with:

- the additional permissions in [LICENSE-EXCEPTIONS.md](LICENSE-EXCEPTIONS.md), which allow
  distribution through application stores, and
- the trademark terms in [TRADEMARK.md](TRADEMARK.md), which govern the Serval name and brand.

Contributed code is distributed under those terms. New source files do not need a license header;
the CLA governs what you have granted.

## The repository

| Project | Path | Stack | Role |
|---|---|---|---|
| **Shared** | [`Shared/`](Shared/) | .NET 10 | The AI-detection library — object detection, scene description, audio — and the telemetry contract, referenced by both the Server and the CameraModule. |
| **CameraModule** | [`CameraModule/`](CameraModule/) | .NET 10 | On-device AI — speech, transcription, emotion, audio events, speaker labels/diarization, and optional vision — streamed to the Server. |
| **Server** | [`Server/`](Server/) | .NET 10 / ASP.NET | NVR + telemetry hub: records cameras to HLS, serves live/VOD/clips + the snapshot-wall dashboard, WebRTC with PTZ and talk-back, runs server-side AI, and ingests + serves AI telemetry. |
| **App** | [`App/serval_app/`](App/serval_app/) | Flutter (Dart) | UI for camera feeds and all AI output, plus the camera registry. |

```
Serval/
├── Serval.slnx          # root solution (Shared + CameraModule + Server + tests)
├── Docs/                # the long-form documentation
├── Shared/              # AI-detection library + telemetry contract, used by both hosts
│   ├── Serval.Ai.Core/           # the seams + the pure gate logic, no native deps
│   ├── Serval.Ai/                # sherpa-onnx + LLamaSharp implementations
│   ├── Serval.Contracts/         # the telemetry documents, declared once
│   └── Serval.Ai.Tests/
├── CameraModule/        # .NET 10 edge-AI worker + its tests
├── Server/              # .NET 10 ASP.NET API (+ the developer docker-compose)
├── App/serval_app/      # Flutter client
├── deploy/              # the deployment compose files
└── scripts/             # model fetch/export helpers
```

## Building and testing

Prerequisites are .NET 10 and, for the App, the Flutter SDK (Dart `^3.12`).

```bash
dotnet build Serval.slnx
dotnet test Serval.slnx

cd App/serval_app && flutter test
```

To run from source: the Server needs MongoDB reachable, `ffmpeg` on `PATH`, and a signing key +
first-admin password (it refuses placeholder values):

```bash
export Serval__Auth__SigningKey="$(openssl rand -base64 32)"
export Serval__Auth__BootstrapAdminUsername=admin
export Serval__Auth__BootstrapAdminPassword='pick-something'   # first boot only
cd Server/Serval.Server && dotnet run

# CameraModule — fetch models first (~3.5 GB, or ~1.2 GB with SKIP_VISION=1; not in git)
cd CameraModule/Serval.CameraModule && ./scripts/fetch-models.sh && dotnet run

# App against a local Server
cd App/serval_app && flutter run -d chrome
```

`Server/docker-compose.yml` is the developer container stack — it builds the image from the
working tree, where [deploy/docker-compose.yml](deploy/docker-compose.yml) pulls the published
one.

**Do not point tests at a live deployment.** Run your own Server instance.

### Checks a pull request has to pass

```bash
dotnet build Serval.slnx          # no warnings, not just no errors

cd App/serval_app
dart format .                     # reports no files changed
flutter analyze                   # no issues, info included
```

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs these on every pull request, along with
both test suites, so a branch that skips them finds out anyway — just slower. Two differences from
what you run locally, worth knowing before a red check is a mystery:

- **The goldens gate, and they run as their own step.** A failure there uploads the
  expected/actual/diff images as a `golden-failures` artifact on the run. Look at it before assuming
  a mistake — if the design did change, `flutter test --update-goldens` and commit the new captures
  in the same branch that changed the design.
- **CI resolves with `--enforce-lockfile`**, so `pubspec.lock` is committed alongside any
  `pubspec.yaml` edit. Otherwise the failure is a resolution error rather than a code one.

**The build must be warning-free.** Warnings are the analyzers doing the job they were added for,
and a build carrying three that everyone has learned to scroll past is a build where the fourth —
the one that matters — arrives invisible. Fix it or, if it is genuinely wrong here, suppress it
narrowly with a comment saying why.

**`dart format` is the entire formatting rule.** There is no house style to learn and nothing to
settle in review: run it and commit what it writes. Format your own edits rather than the file
around them — a branch that reformats untouched code is the trenchcoat problem below.

**`flutter analyze` must be silent, including info.** Info-level lints are the ones with no
argument against them. `curly_braces_in_flow_control_structures` costs two characters and closes
the hole where a second statement indented under an unbraced `if` reads as guarded and runs
unconditionally. Left alone they accumulate until the real warning is one line in forty.

## What a good pull request looks like

- **One change per pull request.** A branch that fixes a bug and reformats two files is two
  reviews wearing a trenchcoat.
- **Tests for behaviour that can be tested without hardware.** Much of Serval cannot — anything
  touching a real camera, a real stream or a real model. The parts that can are the gate logic,
  the contracts, the settings catalogue and the widget layer, and those have tests already.
- **Match the surrounding code**, including the comment style below.
- **Say what you tested.** Especially for camera-dependent work — name the camera and what you
  saw, since a reviewer probably cannot reproduce it.

## Comment style

Comments here are for the person who has to change the code. Sort what you are about to write
into one of three kinds:

- **A constraint** — what breaks if someone changes this. Keep it, at whatever length it needs.
  `A null percent is not a zero` earns a paragraph, because a null becoming 0 turns an
  unreported GPU into an idle one.
- **Design rationale** — why this shape and not the obvious alternative. Belongs in
  [Docs/](Docs/). Check there first: if it is already written up, link to it instead of
  restating it.
- **History** — what the code used to do, or which iteration of the design produced it. Leave it
  out. Git has it, and a comment describing code that is not there misleads.

Then cut the connective prose. A point made once is made.

[`App/serval_app/lib/widgets/icon_rail.dart`](App/serval_app/lib/widgets/icon_rail.dart) is the
worked example: a lead sentence saying what the thing is, then only what is not visible from the
code below it.

## Reporting bugs

Include the Serval version or commit, which component (Server, CameraModule, App), the camera
make and model where relevant, and the logs around the failure. For anything involving a stream,
the output of the failing `ffmpeg` command is usually the whole answer.

The [bug report form](.github/ISSUE_TEMPLATE/bug_report.yml) asks for exactly that list and will
not submit without the parts that cannot be guessed, so opening an issue from
[the chooser](https://github.com/Flickersoft/serval/issues/new/choose) is the shortest route to a
report someone can act on. Proposals have
[their own form](.github/ISSUE_TEMPLATE/feature_request.yml).

## Security

Do not open a public issue for a security problem. Serval handles video and audio from inside
people's homes, and a vulnerability disclosed publicly is a vulnerability being exploited. Report
it privately through GitHub's security advisory form on this repository.
