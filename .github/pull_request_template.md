## What this changes, and why

<!-- One change per pull request. A branch that fixes a bug and reformats two files is two reviews
     wearing a trenchcoat. -->

Closes #

## What you tested

<!-- Especially for camera-dependent work: name the camera and say what you saw. A reviewer
     probably cannot reproduce it. -->

## Checks

<!-- These are the same checks .github/workflows/ci.yml runs. Ticking them locally is faster than
     finding out from a red check. -->

- [ ] `dotnet build Serval.slnx` — no warnings, not just no errors
- [ ] `dotnet test Serval.slnx`
- [ ] `dart format .` in `App/serval_app` reports no files changed
- [ ] `flutter analyze` is silent, info included
- [ ] `flutter test` passes — and if the design changed, `flutter test --update-goldens` and the
      new captures are committed in this branch
- [ ] `pubspec.lock` is committed alongside any `pubspec.yaml` edit (CI resolves with
      `--enforce-lockfile`)
- [ ] I have signed the [CLA](https://github.com/Flickersoft/Serval/blob/main/CLA.md), or will when
      the bot asks
