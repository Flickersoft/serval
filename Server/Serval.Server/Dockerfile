# syntax=docker/dockerfile:1
#
# Build context is the REPO ROOT, not this directory: Serval.Server.csproj project-references
# ../../Shared/*, which a context rooted here could not reach. Build with:
#
#   docker build -f Server/Serval.Server/Dockerfile -t serval-server .
#
# ---- app (Flutter web build) ----
#
# Pin to the exact Flutter revision .github/workflows/ci.yml tests with, so the container builds
# with the SDK the App was actually developed and tested against rather than whatever `stable`
# resolves to on build day. The two are a pair and have to be bumped together — a new SDK also
# means regenerating test/goldens/, so that is three files or none.
#
# Not App/serval_app/.metadata, which is where this used to point: Flutter writes that file on
# create and migrate but *not* on upgrade, so it still records the revision the project was created
# with and drifted silently the first time the SDK moved.
ARG FLUTTER_REVISION=6b182d2c7585eba26d4edce0f97630effd256c33

FROM debian:bookworm-slim AS app
RUN apt-get update \
    && apt-get install -y --no-install-recommends git curl ca-certificates xz-utils unzip \
    && rm -rf /var/lib/apt/lists/*

# The Flutter SDK's own repo, from the Flutter/Google GitHub org — not a third-party image.
ARG FLUTTER_REVISION
RUN git clone --filter=blob:none https://github.com/flutter/flutter.git /opt/flutter \
    && git -C /opt/flutter checkout ${FLUTTER_REVISION}
ENV PATH="/opt/flutter/bin:/opt/flutter/bin/cache/dart-sdk/bin:${PATH}"
RUN flutter precache --web

WORKDIR /src/App/serval_app
# Restore first with only the pubspec present, same cache-friendly pattern as the .NET stage below.
COPY App/serval_app/pubspec.yaml App/serval_app/pubspec.lock ./
RUN flutter pub get
COPY App/serval_app/ .

# The commit this image is built from. The App shows it as a *Source* link — AGPL section 13 owes
# a network user the source of the version they are using, and the web App is compiled here from
# the same commit as the Server it ships beside, so a value stamped in at build time names the
# running version exactly. Empty is fine and is what a local `flutter run` gets: the link then
# points at the repository rather than at a commit that might not be the one running.
ARG SOURCE_REVISION=""

# The release number that commit carries, as major.minor.patch — derived from the nearest version
# tag by the image workflow and passed in. It cannot be worked out in here: .git/ is excluded from
# the build context (see .dockerignore), so there are no tags to describe against. Empty is fine
# and is what a local build gets; the App then names the commit alone.
ARG SERVAL_VERSION=""

# No --dart-define=SERVAL_BASE_URL: the App defaults to the page's own origin on web (see
# serval_config.dart), so the same build works regardless of the hostname/IP it's served from.
#
# --pwa-strategy=none because notifications need a service worker of our own, at web/sw.js, and
# there is only one registration slot at scope "/". Flutter's generated worker is now a stub whose
# only act is to unregister itself, and leaving it on would mean our registration racing something
# that calls unregister() on whatever is in that slot — a race whose losing side is silent, since a
# notification that never arrives looks exactly like a quiet night. With `none` the tool writes an
# empty flutter_service_worker.js and omits serviceWorkerSettings from the bootstrap, so nothing is
# registered until lib/push/push_client_web.dart does it.
#
# What this gives up is offline caching of the app shell, which is worth nothing here: every screen
# needs the Server. Note the flag prints a deprecation warning — the fallback when it is eventually
# removed is a custom web/flutter_bootstrap.js, which the tool uses verbatim when present.
RUN flutter build web --release --pwa-strategy=none \
        --dart-define=SOURCE_REVISION=$SOURCE_REVISION \
        --dart-define=SERVAL_VERSION=$SERVAL_VERSION

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Vulkan gets llama.cpp onto the GPU; see Serval.Ai.csproj. Overridable so an image can still be
# built for a GPU-less host: --build-arg LLAMA_BACKEND=Cpu.
ARG LLAMA_BACKEND=Vulkan

# Both empty on a build that is not the image workflow's. VersionPrefix then falls back to 0.0.0
# and the absent SourceRevisionId is what marks the result -dev — see the version block in
# Directory.Build.props. Declared after the restore-layer ARGs would be wrong: they must be in
# scope at the publish step below.
ARG SERVAL_VERSION=""
ARG SOURCE_REVISION=""

# The solution-wide properties every project inherits: the version block, and the Product / Company /
# Copyright / license / repository metadata that becomes assembly attributes. MSBuild finds it by
# walking up from each project, so it has to sit at /src, and it has to be here for the restore as
# well as the publish — both evaluate it. Its own layer because it changes far less often than the
# csproj files below.
COPY Directory.Build.props .

# Restore first with only the csproj files present, so the (slow) restore layer stays cached
# until a dependency actually changes rather than on every source edit.
COPY Server/Serval.Server/Serval.Server.csproj Server/Serval.Server/
COPY Shared/Serval.Ai/Serval.Ai.csproj Shared/Serval.Ai/
COPY Shared/Serval.Ai.Core/Serval.Ai.Core.csproj Shared/Serval.Ai.Core/
COPY Shared/Serval.Contracts/Serval.Contracts.csproj Shared/Serval.Contracts/
RUN dotnet restore Server/Serval.Server/Serval.Server.csproj -p:LlamaBackend=$LLAMA_BACKEND

COPY Server/Serval.Server/ Server/Serval.Server/
COPY Shared/ Shared/

# ${VAR:+...} so an empty build arg passes no property at all rather than an empty one. The two are
# not the same: -p: sets a *global* property, which a project cannot override, so the
# `Condition="'$(VersionPrefix)' == ''"` fallback to 0.0.0 in Directory.Build.props evaluates true and
# is then discarded, and Version comes out as a bare `-dev`. Neither error names the property or the
# file: in this stage it is MSB4044 about a missing NuGetVersion, outside it "'-dev' is not a valid
# version string".
RUN dotnet publish Server/Serval.Server/Serval.Server.csproj \
        -c Release -o /app --no-restore -p:LlamaBackend=$LLAMA_BACKEND \
        ${SERVAL_VERSION:+-p:VersionPrefix=$SERVAL_VERSION} \
        ${SOURCE_REVISION:+-p:SourceRevisionId=$SOURCE_REVISION}

# ---- Edge TPU natives ----
#
# Carried by every image, loaded only when Serval:Ai:Detection:Device selects tflite-edgetpu.
# One image serves a CPU-only host and a host with Corals, because which one it is is a *setting*
# rather than a build: what genuinely differs per deployment is hardware passthrough, and that lives
# in the compose file (devices:, device_cgroup_rules:).
#
# The cost of carrying them is ~8 MB in the runtime layer plus a build stage that only re-runs when
# TF_TAG changes. The cost of not carrying them would be two images to build, tag, push and keep in
# step.
#
# ---- why libtensorflowlite_c.so is built rather than fetched ----
#
# Nobody publishes it. LiteRT's releases carry only headers (litert_cc_sdk.zip); PINTO0309's
# TensorflowLite-bin ships Python wheels; and Frigate — the usual reference for EdgeTPU in a container —
# drives the Coral through Python (tflite_runtime + load_delegate) and so has no C API library to
# borrow. It is needed because libedgetpu is only a TFLite *delegate*: it contributes the Edge TPU
# kernels, while loading a model, allocating tensors and calling Invoke are all the TFLite C API.
#
# CMake rather than bazel — TensorFlow supports this target directly, needs no JDK, and takes ~5
# minutes. debian:11 so the result links glibc 2.31 and therefore loads in the aspnet runtime image;
# built-on-old runs on new, not the reverse.
FROM debian:11 AS tflite

ARG TF_TAG=v2.16.2

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git cmake build-essential ca-certificates python3 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
RUN git clone --depth 1 --branch ${TF_TAG} https://github.com/tensorflow/tensorflow.git

WORKDIR /build
RUN cmake /src/tensorflow/tensorflow/lite/c -DCMAKE_BUILD_TYPE=Release \
    && cmake --build . -j"$(nproc)" --target tensorflowlite_c \
    && mkdir -p /out \
    && cp libtensorflowlite_c.so /out/ \
    && sha256sum /out/libtensorflowlite_c.so


# ---- libedgetpu.so.1 ----
#
# NOT from Google's own apt repository, and this is the most important line in this file.
#
# libedgetpu is a TFLite delegate, so its ABI has to match the libtensorflowlite_c.so above. Google's
# `coral-edgetpu-stable` ships libedgetpu 16.0 built against TFLite **2.5** — dated July 2021, paired in
# that repo with python3-tflite-runtime 2.5.0.post1. Against a 2.16 TFLite it does not fail to load: it
# **segfaults inside libtensorflowlite_c.so** once the interpreter walks the delegate, because the
# delegate machinery changed substantially by 2.16. Measured, not theorised.
#
# feranick/libedgetpu publishes the same Apache-2.0 source rebuilt against modern TensorFlow, tagged by
# the TF version it matches. 16.0TF2.16.1 pairs with TF_TAG above and is validated end to end on two USB
# Corals, producing scores identical to pycoral's reference decoder.
#
# Pinned by URL and verified by hash — the provenance discipline Docs/rk3588.md applies to the closed
# Rockchip binaries. Bump both together and re-validate: a mismatch is a segfault at best, and at worst
# silently degraded confidence.
#
# LICENSING: Apache-2.0 code that ships a proprietary firmware blob, uploaded to the device on first
# open. If that is unacceptable for how this image is distributed, drop this stage and the COPY below,
# bind-mount the library on the host, and point Serval:Ai:Detection:EdgeTpuLibraryDirectory at it.
FROM debian:11 AS edgetpu

ARG EDGETPU_DEB=https://github.com/feranick/libedgetpu/releases/download/16.0TF2.16.1-1/libedgetpu1-std_16.0tf2.16.1-1.bullseye_amd64.deb
ARG EDGETPU_SHA256=a6d3909053bcf286248687478fe723b6ca61cbe10691f30452892179cfc43879

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates binutils \
    && rm -rf /var/lib/apt/lists/* \
    && curl -fsSL -o /tmp/libedgetpu.deb "${EDGETPU_DEB}" \
    && echo "${EDGETPU_SHA256}  /tmp/libedgetpu.deb" | sha256sum -c - \
    && dpkg-deb -x /tmp/libedgetpu.deb /tmp/edgetpu \
    && mkdir -p /out \
    && cp /tmp/edgetpu/usr/lib/x86_64-linux-gnu/libedgetpu.so.1.0 /out/libedgetpu.so.1 \
    && rm -rf /tmp/libedgetpu.deb /tmp/edgetpu


# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# ffmpeg + ffprobe are required: the Server shells out to them for every camera.
#
# Both VAAPI drivers ship, because a VA driver is a userspace .so loaded by ffmpeg *in this
# container* — the host's copy is not visible here however complete it is, and only the kernel half
# (i915/amdgpu, /dev/dri) is shared. mesa-va-drivers covers AMD; intel-media-va-driver is iHD, and
# without it every VAAPI encode on an Intel host fails at ffmpeg with "Failed to initialise VAAPI
# connection". That failure cannot be caught earlier: `ffmpeg -encoders` lists h264_vaapi whether or
# not a driver can initialise, so FfmpegCapabilities has nothing to go on. Both still need
# --device /dev/dri passed at runtime.
#
# mesa-vulkan-drivers carries both Vulkan drivers llama.cpp can offload through when
# Serval:Ai:Vision:GpuLayers is non-zero — RADV on AMD, ANV on Intel. vainfo/vulkaninfo are
# diagnostics — a few MB that turn "why is the GPU idle" into one command inside the running
# container.
#
# libusb-1.0-0 is libedgetpu's only runtime dependency. usbutils earns its few megabytes by the same
# argument as vainfo and vulkan-tools: it turns "why is the TPU idle" into one command inside the
# running container, and `lsusb -t` is specifically how a Coral on USB 2 (about a third of the
# throughput) is told from one on USB 3.
# curl + bzip2 exist for /app/fetch-models.sh alone (the sherpa-onnx archives are .tar.bz2) —
# see the COPY further down.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg \
        mesa-va-drivers intel-media-va-driver \
        libvulkan1 mesa-vulkan-drivers \
        vainfo vulkan-tools \
        libusb-1.0-0 usbutils \
        curl bzip2 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./
# The web UI, served from wwwroot alongside the API — see UseStaticFiles/MapFallbackToFile in
# Program.cs. Same origin as /api/*, so the App needs no separate host and no CORS to reach it.
COPY --from=app /src/App/serval_app/build/web ./wwwroot

# The model fetcher, runnable as a compose one-shot so a deployment without a repo checkout can
# populate its models volume (`docker compose --profile setup run --rm fetch-models` — see
# deploy/docker-compose.yml). models-seed/ carries the one weight vendored in git (Silero VAD v5,
# version-pinned) so the fetcher finds it without a checkout; everything else it downloads.
COPY scripts/fetch-models.sh ./fetch-models.sh
COPY CameraModule/Serval.CameraModule/models/silero_vad.onnx ./models-seed/silero_vad.onnx

# Vulkan offload is decided by which libggml.so gets loaded, and without this it is never the right
# one. LLamaSharp picks a runtimes/linux-x64/native/ folder by CPU feature detection — avx2 on both
# typical deployment hosts — and never picks vulkan/. The ggml backend registry lives in libggml.so, and
# only vulkan/'s copy references ggml_backend_vk_reg; the avx copies register the CPU and nothing
# else. libllama.so is byte-identical across all of them, so it pulls libggml-vulkan.so in as a
# DT_NEEDED either way — the library loads, sits mapped, and its registration function is never
# called. Symptom: "CLIP using CPU backend" and no /dev/dri fd, while the log reports gpuLayers=99
# and the status page looks healthy.
#
# Overlaying rather than pointing LLamaSharp at vulkan/ wholesale, because that folder ships no
# libggml-cpu.so — it carries the registry, not the CPU kernels, which stay per-variant and must
# keep matching the folder they live in.
#
# libggml-vulkan.so is 62 MB and hard-linked; copying it four times would add a quarter gigabyte to
# the image for four identical files. libggml.so is 55 KB and copied, so the replacement is plain.
# A symlink would be smaller still and is wrong: glibc expands $ORIGIN from the resolved path, so the
# link would pull libggml-base.so out of vulkan/ and reintroduce the two-registry split fixed here.
#
# Safe on a host with no GPU: ggml registers the Vulkan backend, enumerates zero devices and uses the
# CPU, which is the ordinary llama.cpp path. libvulkan1 and mesa-vulkan-drivers are installed
# unconditionally above, so libvulkan.so.1 always resolves and the load cannot fail for want of it.
# linux-x64 is the only arch that needs this: server-image.yml builds linux/amd64 alone.
RUN set -eu; \
    N=/app/runtimes/linux-x64/native; \
    for d in avx avx2 avx512 noavx; do \
        cp -f  "$N/vulkan/libggml.so"        "$N/$d/libggml.so"; \
        ln -f  "$N/vulkan/libggml-vulkan.so" "$N/$d/libggml-vulkan.so"; \
    done

# In their own directory rather than dropped into /app. .NET's default probing would look for
# "libedgetpu.so" and this ships as "libedgetpu.so.1", so the detector resolves them by their real
# filenames — which also avoids touching LD_LIBRARY_PATH and the load order of the native runtimes
# already in /app (sherpa-onnx, llama.cpp, ONNX Runtime), where a filename collision has bitten before.
COPY --from=tflite /out/libtensorflowlite_c.so /app/edgetpu/
COPY --from=edgetpu /out/libedgetpu.so.1 /app/edgetpu/

# Where to find them, not a decision to use them. Detection:Device decides that, and defaults to
# onnx-cpu — so a host that has never heard of a Coral behaves exactly as it did before these files
# existed.
ENV Serval__Ai__Detection__EdgeTpuLibraryDirectory=/app/edgetpu

# Listen on a fixed container port; map it in compose.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# The container runs as root. What that costs: the server spawns an ffmpeg per camera, on bytes the
# camera chooses, and a parsing bug in that subprocess would land as uid 0 with the media volume
# and — where /dev/dri is passed through — the GPU render node already mounted.
#
# Uncommenting the line below drops that to the base image's unprivileged `app` user, which is a
# real reduction. It is off by default because it is not free, and the two costs land on the host
# rather than here:
#
#   * Bind-mounted media and model directories must be readable (and media writable) by UID 1654.
#     A named volume inherits ownership on creation and just works; a ZFS dataset bind-mounted from
#     the host does not, and recording fails at the first segment write.
#   * VAAPI/Vulkan offload needs the container user in the host's `render` group —
#     `group_add: ["<host render gid>"]` in compose, and that GID varies by distribution.
#
# So: turn it on, then confirm recording still writes and `vainfo` still enumerates.
# USER $APP_UID

ENTRYPOINT ["dotnet", "Serval.Server.dll"]
