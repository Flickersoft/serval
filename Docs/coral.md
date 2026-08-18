# Coral Edge TPU deployment

**A Server deployment**, for a small x86 host that has no cores to spare. Selected with
`Serval:Ai:Detection:Device=tflite-edgetpu`; `onnx-cpu` remains the default and remains supported, and
both run from the same image.

Not to be confused with [rk3588.md](rk3588.md), which is the *CameraModule's* deployment on an arm64
SBC and whose accelerator is an NPU doing **vision descriptions**. This one is x86, containerised, and
accelerates **object detection**. The two share no code path beyond `IObjectDetector`.

Object detection remains **server-only in the pipeline** — the CameraModule constructs no detector for
its own frames (see [rk3588.md](rk3588.md), *Object detection is server-only*). Its `--detect` and
`--replay-gates` diagnostics do go through the same factory, though, which is how this runtime gets
validated against real hardware without deploying a whole server:

```bash
CameraModule__Detection__Device=tflite-edgetpu \
CameraModule__Detection__ModelPath=/path/model_edgetpu.tflite \
CameraModule__Detection__LabelsPath=/path/coco_labels.txt \
CameraModule__Detection__EdgeTpuLibraryDirectory=/path/natives \
  ./camera-module --detect frame.jpg
```

Note that needs write access to the USB device node — see the gotchas.

**Measured on an Intel N100 with two USB Corals, against `ssdlite_mobiledet` at 320×320:**

| | inferences/sec | p50 |
|---|---|---|
| USB 3 device | **64.2** | 15.6 ms |
| USB 2 device | **29.7** | 33.6 ms |
| both, pooled | **95.8** | — |

For scale: six cameras at `DetectFps=2` ask for 12 whole-frame inferences a second. The same host's
four Gracemont cores were estimated at 10–13/s for the ONNX path, so this is roughly **eight times** the
detection throughput while leaving the CPU to ffmpeg and the vision model.

Detection quality matches pycoral, the reference decoder, exactly: `person 0.77 / bus 0.77 / person 0.73
/ person 0.65` against its `0.770 / 0.770 / 0.719 / 0.648` on the same frame.

## Bring-up

```bash
lsusb                       # 18d1:9302 once firmware is loaded, 1a6e:089a before
lsusb -t                    # both devices should read 5000M — see the gotchas
```

Verified when it works, from the Server's own startup line:

```
Object detector ready: edgetpu/yolov9-s-relu6-tpumax_512_int8_edgetpu —
yolo/dfl head, fixed 512x512 input, 17 classes (Positional),
2 device(s) at 2-2, 1-1. libedgetpu ... RuntimeVersion(14).
```

Every part of that is worth reading: **the head was chosen from the model's own output signature**, not
from configuration — `yolo/dfl` here, `ssd/detection-postprocess` for a MobileDet or EfficientDet-Lite,
`yolo/end-to-end` for a one-tensor export. The shape is the one compiled into the file, the class count
comes from the model where the head declares one, and the device paths are what the lanes bound to.

**No kernel module is needed.** `gasket`/`apex` is for the PCIe and M.2 parts. Installing
`gasket-dkms` for a USB Accelerator does nothing at all, and then costs an hour.

## Configuration

`deploy/examples/docker-compose.intel-coral.yml` is a working starting point. The parts that matter:

```yaml
Serval__Ai__Detection__Device: "tflite-edgetpu"
Serval__Ai__Detection__ModelPath: /app/models/detect/yolov9-s-relu6-tpumax_512_int8_edgetpu.tflite
Serval__Ai__Detection__LabelsPath: /app/models/detect/labels-coco17.txt
devices:
  - /dev/bus/usb:/dev/bus/usb
device_cgroup_rules:
  - "c 189:* rmw"
```

**The model path and the labels path move together, always.** Two families are supported and their
label lists are different lengths — YOLOv9 is 17 classes, the SSD and EfficientDet-Lite models are
COCO-90. Changing one line and not the other either renames every class to its neighbour or, on the
YOLO head, refuses to start. See *the labels file is 17 lines* below.

**One `ModelPath` serves both runtimes**, and the `tflite-` prefix on the device is what says to read
it as the `edgetpu_compiler` output. The startup check reads the file's own header rather than its
extension, so a path left pointing at `.onnx` weights is refused by name at startup instead of failing
somewhere inside a native loader.

**There is no fallback, deliberately.** `tflite-edgetpu` refuses to start when no device is found,
rather than quietly running something else and looking healthy on the status page while delivering a
fraction of the throughput. The settings page greys the device out on a host with no Coral, so the
absence is visible before a restart rather than after one.

**`MaxConcurrency`, `InputPixels` and `NumThreads` do not apply.** An Edge TPU runs one inference at a
time, so concurrency is the device count; a compiled model has its input shape baked in; and the lane
is pinned to one host thread. The settings page dims all three on this device, and the runtime logs
when `MaxConcurrency` has been set, so an operator who configured it learns it did nothing.

**`ScoreThreshold` is per model and wants deriving per model.** The SSD needs it raised well above the
0.25 default because its noise floor sits at 0.29 on a blank frame; the YOLO head has no such floor but
scores more conservatively, so a value inherited from the SSD may be too high rather than too low. In
one test a correctly-identified cat scored 0.44 against an inherited 0.40 threshold — clear, but only
just. Derive it from a batch of frames across the day's light, never from one fixture.

## Re-validating against pycoral

Several gotchas below end in "re-validate against pycoral". pycoral is Google's own Python decoder, and
comparing scores against it is the only way to tell a *correct* result from a *plausible* one — a
mismatched library or stale firmware degrades confidence silently rather than failing.

The whole harness is one throwaway container, so it costs nothing to keep out of the repo:

```dockerfile
# debian:11 — the coral-edgetpu-stable apt suite has no trixie, and these binaries want an older libc.
FROM debian:11
RUN apt-get update && apt-get install -y --no-install-recommends curl gnupg ca-certificates usbutils \
    && curl -fsSL https://packages.cloud.google.com/apt/doc/apt-key.gpg \
        | gpg --dearmor -o /usr/share/keyrings/coral-edgetpu.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/coral-edgetpu.gpg] \
https://packages.cloud.google.com/apt coral-edgetpu-stable main" \
        > /etc/apt/sources.list.d/coral-edgetpu.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        libedgetpu1-std python3-pycoral python3-numpy edgetpu-compiler \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /work
```

Note this deliberately installs **Google's** libedgetpu, matching pycoral's pinned TFLite 2.5 — that
pairing is self-consistent and is what makes it a valid reference. It is *not* the pairing the Server
uses, which is the whole point of the first gotcha below.

```bash
docker build -f coral-spike.Dockerfile -t coral-spike .
docker run --rm -v "$PWD:/work" \
    --device /dev/bus/usb:/dev/bus/usb --device-cgroup-rule "c 189:* rmw" \
    coral-spike python3 -c "
from pycoral.adapters import detect
from pycoral.utils.edgetpu import make_interpreter
from PIL import Image
import numpy as np
it = make_interpreter('/work/model_edgetpu.tflite'); it.allocate_tensors()
d = it.get_input_details()[0]
_, h, w, _ = d['shape']
img = Image.open('/work/frame.jpg').convert('RGB').resize((w, h))
it.set_tensor(d['index'], np.asarray(img, dtype=np.uint8)[np.newaxis, ...]); it.invoke()
for o in detect.get_objects(it, 0.25)[:5]: print(o.id, round(o.score, 3), o.bbox)
"
```

Then run the same image through the Server's own path and compare — `--detect` on the CameraModule is
the quickest way (see the top of this page). Scores should agree to within rounding. **A run that opens
the device on the first attempt but returns everything near the score threshold is stale firmware, not
a bad model** — see the second gotcha.

## Gotchas

Each of these cost real debugging, and each is named by its symptom rather than its cause.

**libedgetpu must be ABI-matched to TFLite, and Google's own build is not.**
*Symptom: a segfault inside `libtensorflowlite_c.so`, after the USB device has visibly reset.*
libedgetpu is a TFLite *delegate*, so it has to match the TFLite it is loaded beside. Google's
`coral-edgetpu-stable` ships libedgetpu 16.0 built against TFLite **2.5** — dated July 2021, and paired
in that repo with `python3-tflite-runtime 2.5.0.post1`. Against a 2.16 runtime it does not fail to load;
it crashes once the interpreter walks the delegate. `feranick/libedgetpu` publishes the same Apache-2.0
source rebuilt per TF version, and the Dockerfile pins `16.0TF2.16.1` by URL and sha256 against its
`TF_TAG=v2.16.2`. **Bump the two together and re-validate against pycoral.**

**Swapping libedgetpu versions without power-cycling the device gives silently wrong results.**
*Symptom: no error, no crash — every score sits just above the threshold. A frame that should score 0.77
scores 0.34, and dozens of low-confidence rows appear across many classes.*
The firmware is uploaded on first open and is RAM-resident, so a device still running the previous
library's firmware produces degraded output rather than refusing. **Unplug and replug after changing the
library.** Treat "everything scores near the threshold" as this rather than as a bad model — it is
indistinguishable from a quantisation problem by inspection.

**A per-device path breaks on re-enumeration.**
*Symptom: the first inference works, then `EPERM` / "Failed to open device", while `ls /dev/bus/usb`
plainly shows a node.*
A Coral enumerates as `1a6e:089a`, and libedgetpu uploads firmware on first open, after which it
re-enumerates as `18d1:9302` **with a new device number**. So `/dev/bus/usb/001/004` is stale after one
inference. Pass the whole bus tree.

**Missing `device_cgroup_rules`.**
*Symptom: identical to the above — "Failed to open device" with the node visibly present and readable
from the host.*
Bind-mounting the bus tree gives the container the nodes across re-enumeration, but the device cgroup
still denies unlisted minors, so the newly-numbered node is refused. `c 189:* rmw` permits usb-device
generally. Not `privileged: true`, which most guides reach for and which hands the container the host.

**`edgetpu_create_delegate` needs write access to the device node.**
*Symptom: "The device is present but could not be opened", as a non-root user.*
The nodes are `crw-rw-r-- root root` and libedgetpu issues control transfers. Invisible in the container,
which runs as root; it bites when running a binary directly on the host to diagnose something. A udev
rule granting `plugdev` is the fix if you need that.

**A pre-firmware Coral reads 480M, and that is normal.**
*Symptom: `lsusb -t` says `480M` for a device you just plugged into a blue port.*
`1a6e:089a` is a USB 2.0 device by design; it only trains at SuperSpeed after firmware upload turns it
into `18d1:9302`. **Only judge the link speed after the device has been opened.** If it stays at 480M
once post-firmware, it really is on a USB 2.0 path — swap the two sticks between ports to tell a bad
cable from a bad port.

**A Coral on USB 2 costs about two thirds of its throughput.**
*Symptom: one device measures roughly a third to a half of its twin on the same model.*
Measured at a consistent **~59 ns per byte** of input against USB 3, across three input sizes. At a
320×320×3 input that is 29.7/s against 64.2/s; at 640×384×3 it would be worse, because the penalty
scales with the transfer. It is still worth keeping — the slow device added **45–50%** on top of the
fast one in every measurement — and the lane pool handles the asymmetry by design, renting on demand so
the fast device serves more frames.

**"Model compiled successfully" is not a pass.**
*Symptom: a model that compiled cleanly, reports `Off-chip memory ... 0.00B`, and runs at CPU speed.*
Read the operation split, not the exit code. Compiling a float32 NCHW export reported success and then:

```
Number of operations that will run on Edge TPU: 5
Number of operations that will run on CPU: 284
```

The pass conditions are **≥95% of operations on the TPU** *and* **off-chip streaming of exactly 0 B**.
Non-zero off-chip means parameters are streamed per inference, which shows up as good latency at startup
decaying under load.

**`edgetpu_compiler` will not install on Debian 13.**
*Symptom: apt reports no candidate; the `coral-edgetpu-stable` suite has no `trixie`.*
Compile in a `debian:11` container. The compiler binary also wants an older libc regardless.

**A converter newer than the compiler produces a model it rejects.**
*Symptom: "Model not quantized", on a model that is plainly quantised.*
`edgetpu_compiler` 16.x is the last release. Pin the TFLite converter to 2.16.x.

**No thermal telemetry exists for a USB Coral.**
`/sys/class/apex/*/temp` is the PCIe part. The throughput curve over a long run is the only thermal
signal there is, which is why the soak measures a distribution rather than a mean.

**Enumeration order is not stable.** `edgetpu_list_devices` returned two devices in one order and then
the other on consecutive runs, so a lane binds to the sysfs path and never to an index. The backend does
this; it is written down because anything else built on this API needs to.

**The first open after a firmware upload can fail transiently.** The bus is briefly unsettled and the
error is an opaque delegate-creation failure. The backend retries with backoff; a diagnostic script
should too.

## Logs

Stdout only, as everywhere else — the daemon rotates it. The service is `server`, so the container name
depends on the compose project; address it by service instead:

```bash
docker compose -f deploy/examples/docker-compose.intel-coral.yml logs server \
  | grep -E "Object detector ready|Detection budget|Detection labels"
docker compose -f deploy/examples/docker-compose.intel-coral.yml logs server | grep -iE "Edge TPU|capacity"
sudo dmesg --time-format iso | grep -iE "usb|xhci" | tail -20
```

`dmesg` is the one that matters when a device misbehaves: `reset SuperSpeed USB device` appears once per
delegate creation as a matter of course, but repeatedly during steady-state inference means USB
instability — check autosuspend (`usbcore.autosuspend=-1`, or a udev `power/control` rule).

Losing a device is reported rather than inferred. The budget is rescaled within one reconcile tick, a
`DetectionDegraded` alert names the device that went away, and the status page carries it — see below.
Coverage alone will *not* show it: capacity halving turns into dropped frames, and coverage counts only
shed regions.

## On the status page

*Settings → Server status* carries an **Edge TPU** meter beside Processor, Memory and Graphics, drawn
only where the detector reports devices — a CPU deployment sees the page it always did.

The bar is the **pool**, not the busiest device: a frame goes to whichever Coral is idle and the budget
is their sum, so how close the pool is to saturated is the question worth a meter. (The Graphics meter
beside it is the opposite — its busiest *engine* — because render and video are not interchangeable and
one number has to pick one.) Under it, one sentence per device:

```
Edge TPU                                                             61%
2-2 at 63 a second, 15.8 ms each over USB 3. 1-1 at 29 a second, 33.4 ms each over USB 2.
```

That line is the point of the whole card. **A Coral on a USB 2 path is invisible in the pooled figure**
and looks like a slow model — the per-device split, and the link speed beside it, is the only place
that fault is named. Compare the latencies against the table at the top of this page.

Where the numbers come from: each lane accumulates `Stopwatch` ticks spent inside
`TfLiteInterpreterInvoke` — not the copy in or the decode out, which are processor work — plus its own
inference count. The detector only ever counts; the vitals sampler holds the previous reading and
divides by the interval between its own two samples. Busy % comes from the time, throughput from the
count, mean latency from one over the other. Same instrument as the i915 perf counters on the meter
above, and the reason both are honest about the window they cover.

Three readings worth knowing how to interpret:

- **`Declining N frames a second because every device was busy`** — the pool is the bottleneck. This is
  `DroppedWhileBusy`, and it is different from the shed regions under *Detection coverage*: the budget
  did not refuse these, the hardware did.
- **`2-2 has stopped answering`** — that device is sick and waiting out its 5 s reopen cooldown. It keeps
  its row, because a list that shrank would report a halved accelerator as a smaller healthy one.
- **`not reported`, hatched** — fewer than two counter readings so far, which is the first five seconds
  after a restart. Never a bar at 0%: an idle accelerator and an unmeasured one are different things.

`InferenceBudget` runs real inferences at startup to time the backend, and those land in the totals. They
perturb the first window only — which is the window there is no previous reading for anyway.

Still no thermal figure, for the reason in the gotchas: `/sys/class/apex/*/temp` is the PCIe part. The
throughput line over a long run remains the only thermal signal a USB Coral gives.

## The model

Detection weights are not bundled, and the accelerator path needs **two** files that
`fetch-models.sh` does not download: an `edgetpu_compiler` output and **its own labels file**.

Validated model: `ssdlite_mobiledet_coco_qat_postprocess_edgetpu.tflite` from coral.ai's model zoo,
which is pre-compiled and needs no toolchain at all. 320×320, COCO-90, four output tensors.

The backend picks its decode from the model's output signature rather than from configuration — four
tensors is the SSD detection-postprocess head, one `[1, N, 6]` tensor is the YOLO end-to-end head — and
refuses loudly on anything else, because a four-tensor head read as one decodes into plausible nonsense.

### Three things about the SSD head that are counter-intuitive

Each is measured and each is encoded in `SsdPostprocessTests`:

1. **Boxes are normalised 0–1, not model pixels**, unlike the YOLO end-to-end head.
2. **The component order is `ymin, xmin, ymax, xmax` — y first.** Pinned against pycoral on a real frame:
   reading y-first reproduced its box to within rounding (2.2 px total), reading x-first was out by 548 px.
   A transposed read does not throw; it produces plausible boxes that are wrong forever.
3. **The count tensor is not a count.** It reported 100 — the full row capacity — on every frame including
   a blank one. The score threshold is the only usable filter, and this model scored spurious rows up to
   **0.29 on a blank frame**, above the 0.25 default.

### The class list is not portable

Coral's `coco_labels.txt` is 90 positional entries with no `???` filler. All eight of Serval's
`DefaultClasses` are present with **byte-identical spelling**, so no renaming is needed — but the
**indices differ** from COCO-80 (`cat` is 16 here and 15 there, because COCO-90 includes `street sign`).
Serval matches on the string, so the labels file simply has to travel with the weights. A labels file
from the wrong family renames most of the vocabulary without changing a single box, and nothing
downstream can detect it.

The factory warns for any configured class the labels file cannot provide, which is the signal that
turns this from silent into obvious.

### YOLOv9 on EdgeTPU: built, and running the raw detect head

**The export toolchain was the blocker, never the device.** Eight attempts at exporting one ourselves
failed — ending at `onnx2tf` unable to transpose ultralytics' Conv weights — and that was read for a
while as YOLO being impossible here. It is not: pre-compiled YOLOv9-s EdgeTPU weights are published
freely by [dbro/frigate-detector-edgetpu-yolo9](https://github.com/dbro/frigate-detector-edgetpu-yolo9),
and Frigate+ sells an `edgetpu` variant of its own `yolov9` type. Serval decodes them.

Two export facts from those attempts are still worth keeping, for anyone compiling their own:

- **Ultralytics' `int8=True` does not produce an EdgeTPU-usable model** on current versions — it routes
  through `litert_torch` and emits `[1, 3, 384, 640]` float32 **NCHW**. EdgeTPU needs NHWC with integer
  boundaries, so quantising only the weights is not enough.
- **Modern ONNX tooling and TF 2.16 cannot share an environment**: `onnx >= 1.18` and `onnxscript` want
  `ml_dtypes >= 0.5`, TF 2.16.2 pins `0.3.x`. The export has to be split into two containers with the
  `.onnx` handed over on disk.

#### What the head emits, and why the host has to finish it

Unlike the SSD and end-to-end heads, this one arrives **undecoded** — the operations that would finish
it are ones the accelerator either cannot run or runs badly, so the compiler leaves them out. Read from
`yolov9-s-relu6-tpumax_320_int8_edgetpu.tflite`:

```
in  [1, 320, 320, 3]  uint8  scale=0.00392157 zero=0
out [1, 2100, 64]     int8   scale=0.0796904  zero=-45     boxes, DFL bins
out [1, 2100, 17]     int8   scale=0.0271951  zero=19      class logits, not probabilities
out [1, 2100, 1]      int8   scale=0.0271951  zero=19      per-anchor maximum, unread
```

2100 is 40² + 20² + 10², the strides 8/16/32 grids over the input; the 512 model is the same head at
5376. `YoloDflPostprocessor` softmaxes each group of 16 bins to a distance, places it against the
anchor's centre, sigmoids the logits and suppresses overlaps — about the two milliseconds the reference
implementation also spends. **It suppresses per class where the reference suppresses across all of
them**, because a dog against its owner should not cost one of them.

The third tensor is deliberately unread: it exists so a decoder can find interesting anchors before
dequantising, which is worth a lot when the decoder owns dequantisation. Serval's backend dequantises
every output before the head sees it, so it buys nothing here.

That is also this path's one known inefficiency, and it is smaller than it sounds. The backend converts
every output tensor before the head runs — 441k values at 512 — to use roughly 640 of them, about ten
surviving anchors of 64 bins, plus a whole tensor converted and never read. Removing the waste means
letting a head see quantised tensors, which cuts across the seam that keeps postprocessors pure float
and testable with no hardware. A subtract and a multiply per element is a fraction of a millisecond
against tens for the inference itself, so **measure before trading that away** — an earlier reading of
this page asserted it was the bottleneck on latency figures that could not support the claim.

#### The labels file is 17 lines, not 90

The EdgeTPU YOLOv9 weights are trained on a COCO subset — people, vehicles and animals, dropping street
furniture — and `labels-coco17.txt` ships with them. All eight of Serval's `DefaultClasses` are present.

**This is the one head that can catch a labels mismatch at load**, because the class tensor's last
dimension states the class count; every other head declares nothing, which is why the wrong-order labels
file described above is silently wrong forever. A 90-entry list against these weights is refused.

#### Detection quality, tested on an Intel N100 with two USB Corals

Five candidate models over the same frames from a 6-camera site, at a 0.40 score threshold. Three
subjects with known ground truth: a **static garden ornament** on a 1536x432 32:9 camera, a **domestic
cat** occupying about 30x23 px of a 640x360 frame, and a **parked car** on the same camera.

| Model | cat | car | ornament read as a person |
|---|---|---|---|
| SSD MobileDet 320 | **missed**, and 3 phantom people beside it | 0.63 | **yes**, once shrunk past 0.75x |
| EfficientDet-Lite0 320 | **missed** | **missed** | no |
| EfficientDet-Lite1 384 | 0.56 | 0.46 | whole frame only, 0.54 |
| EfficientDet-Lite2 448 | 0.64 | 0.48 | whole frame only, 0.42 |
| YOLOv9-s 320 | missed (0.21) | 0.44 | **never, at any geometry** |
| **YOLOv9-s 512** | **0.44** | **0.67** | **never, at any geometry** |

**A model that finds nothing looks excellent on false positives**, which is what Lite0 demonstrates and
why every comparison here carries a positive control. On an empty room the SSD returned 46 boxes
including 12 phantom people and a phantom dog, where YOLOv9-s 512 returned none — but that is only
worth anything next to the cat and car columns.

YOLOv9-s 512 scores lower than EfficientDet-Lite3 did on the cat (0.44 against 0.82), so it is the more
conservative of the two. For a recorder whose failure mode was inventing people, that is the right
direction, and it is a real trade rather than a free win.

Both YOLOv9 models read some garden scenery as a `train` at around 0.5. Wrong, but harmless: `train` is
not in `DefaultClasses`, so the allowlist drops it before it becomes an episode.

#### Throughput, same host

From the Server's own `Detection budget` line, which times the prepared-buffer path at the backend's
concurrency. **Read the spread, not the number** — repeated runs on identical hardware vary widely:

| Model | measured, inferences/sec | runs |
|---|---|---|
| SSD MobileDet 320 | **32.5 – 52.9** | 5 |
| YOLOv9-s 320 | 48.3 | 1 |
| **YOLOv9-s 512** | **17.4 – 28.1** | 3 |
| EfficientDet-Lite1 384 | 16.7 | 1 |

YOLOv9-s at 320 lands inside the SSD's own run-to-run variance, so at equal input size this head is not
meaningfully more expensive. At 512 it costs real throughput but still beats a 384 EfficientDet.

#### A larger input can *reduce* the load, which is not obvious

Tiling and region cropping both switch on from the ratio between frame and input, so a bigger input can
turn them off. Moving a 6-camera site from a 320 to a 512 input:

| Camera | at 320 | at 512 |
|---|---|---|
| 1536x432 (32:9) | 0.21x, tiled into 12 | **0.33x, tiled into 4** |
| 640x360 (16:9) | 0.50x, tiled into 6 | **0.80x, crops off; 2 tiles run whole with `TiledFloor`** |
| 480x640 (3:4) | 0.50x, tiled into 6 | **0.80x, crops off; 2 tiles run whole with `TiledFloor`** |

Reserved tile work fell from about 12/s to about 3/s at `DetectFps=2`, and the picture each camera is
shown improved at the same time. **The catch:** with crops off, a whole-frame pass runs *every* frame
and is `Floor`, which `InferenceScheduler` never sheds. That converts a sheddable load into an
unsheddable one, so check the budget covers it before assuming a bigger input is free.

#### `--detect` latency cannot be used to compare models

Serial latency through `--detect` was 60 ms at 320 and 116 ms at 512 against 46 ms for the SSD, and
**none of that is usable as a cost per head.** Three reasons, worth stating because the same trap waits
for the next model measured this way:

- **It is serial and rents one of several unequal devices.** Two USB Corals split across USB generations
  differ by more than 2x on the same model, so which one answered decides the number. The scheduler's
  budget describes every lane at once, so `lanes ÷ budget` is not per-device time either.
- **It decodes a JPEG and letterboxes through ImageSharp**, which the live path never does — that hands
  over a prepared yuv420p buffer from `FramePreparer`. The overhead is in every `--detect` figure and is
  not paid in production.
- **Input size and anchor count scale together.** 320 → 512 multiplies canvas area by 2.56 and anchor
  count by 2.56, so two such points cannot separate image preparation from decoding.

Use the `Detection budget` line to compare models. Use `--detect` to compare *what they find*.

The figure that means something is `InferenceBudget`'s, reported in the *Detection budget* line at
startup: it times the prepared-buffer path at the backend's own concurrency, on blank frames, which is
what the scheduler spends.

**One real inefficiency, of unknown but probably small size.** The backend dequantises every output
before the head runs — 441k values at 512 — to use about 640 of them, roughly ten surviving anchors of
64 bins, plus a third tensor converted and never read. The reference implementation filters on quantised
scores first. Fixing it means letting a head see quantised tensors, which cuts across the seam that
keeps postprocessors pure float and testable with no hardware. A subtract and a multiply per element is
a fraction of a millisecond against tens for the inference, so measure before trading that away.

## Input shape, and what tiling is for

An EdgeTPU graph is compiled for **one** shape, so every camera shares it — the per-camera rectangular
shapes the ONNX path uses cannot carry over. And the shape is square, where cameras are not: at 320×320 a
16:9 stream arrives at half scale and a 32:9 panoramic at a fifth.

**How much a small input buys depends on the input, and 512 is the awkward one.** At 320² a 640×360
stream is squeezed to half, a 2.0x gain that `Regions:Mode = auto` takes without hesitation. At 512² the
same stream arrives at 0.80x — a gain of only 1.25, and 56 % of the model's field is grey padding.

**Cropping is the wrong tool for that 1.25x.** With `Regions:MaxRegionScale` holding a crop to native
scale, the smallest crop such a camera can cut is the detector's own input — a 512×360 window out of a
640×360 frame, four fifths of the picture. Turning crops on also moves the whole-frame pass from every
frame to once per `Regions:FloorSeconds`. That trades the acquisition guarantee for a close-up which is
not one, which is why `Regions:AutoMinRatio` is 1.5 and must not be lowered to admit these cameras.

The 512×360 window is still the right *picture*: measured on a living-room camera with a cat in plain
view, over 92 frames, it took animal detections from 4 frames to 15 and produced the first cat
detections that camera had registered. `Regions:TiledFloor` delivers it without touching the schedule.

What cropping does not cover is the **floor pass** — the whole-frame look that guarantees acquisition of
something that arrived while motion was blind. That pass covers every pixel at a scale which has already
discarded the far field, so covering is not examining.

`Regions:TiledFloor` replaces it with a sweep of native-scale tiles. **Off by default**, because it
changes how the coverage guarantee itself is made and must not arrive as a side effect of anything else.
`Regions:TiledFloorMinGain` is 1.2 — read it as "does tiling buy any scale at all", not as a cost guard,
because tile count *grows* with the squeeze and so a minimum-gain threshold admits the expensive cameras.

**The sweep runs on one of two schedules, and they make different promises.**

A sweep of at most `Regions:SweepAtOnce` tiles — 2 by default, which is what a 16:9 camera costs against
a square input — is run **whole, on every frame**. Every pixel is then examined exactly as often as under
the single shrunken pass it replaces, at native scale instead. That is a strict improvement, it needs
nothing alongside it, and it is what the 640×360 and 480×640 cameras get.

A longer sweep is spread **one tile per frame**, restarting every `Regions:FloorSeconds`, so the
guaranteed cost stays at one inference per frame. This one **needs `Regions:Mode` on alongside it, and
the planner enforces that**: it examines a given pixel once per cycle, and `ObjectTracker` drops a
tentative track the moment one frame passes without matching it, so a new object seen that rarely never
confirms and never becomes an episode. Cropping is what bridges the gap — a retention crop is planned for
every *live* track, tentative ones included, and re-sights the subject on the frames the sweep is looking
elsewhere. The panoramic runs this way.

Because a sweep run whole examines overlapping tiles in the same frame, one object is found once per tile
it falls in; `OverlappingRegions.Fold` reduces those to the best copy before the tracker sees them, since
the tracker's own answer to a second copy is a second track.

**Cost is very different at the two ends, and the tile geometry is why.** A 1536×432 frame needs 12
tiles at a 320 input — 6 seconds at 2 fps, longer than the default 5 s `FloorSeconds`, so that camera's
sweeps run back-to-back at one reserved inference per frame. A 640×360 frame at 512² needs **two**, and
because the last tile sits flush to the far edge they are 512-wide tiles at x=0 and x=128, overlapping by
384 px. Run whole, that is two reserved inferences per frame instead of one — the only place in this
design where the guarantee gets more expensive, and the reason `SweepAtOnce` is a setting rather than a
constant. At `DetectFps=2` it is 4/s per camera; check it against the `Detection budget` line, which
reports what the host was measured at.

`Regions:TileOverlapFraction` is not a tuning nicety: an object lying across a tile boundary is cut in
two and detected as neither half. It has to exceed the largest thing worth finding as a share of a tile.

## Footprint

The two native libraries add about **8 MB** to the image — `libtensorflowlite_c.so` at 6.7 MB and
`libedgetpu.so.1` at 1.2 MB — plus `libusb-1.0-0` and `usbutils`. They ship in every image and stay
inert unless `Device` selects them, which is what lets one artefact serve both a CPU host and this one.

`libtensorflowlite_c.so` is built from source in the image, because nobody publishes it: LiteRT's
releases carry only headers, PINTO0309's builds are Python wheels, and Frigate drives the Coral through
Python so has no C API library to borrow. CMake rather than bazel — TensorFlow supports the target
directly, needs no JDK, and takes about five minutes, cached until `TF_TAG` changes.

Each lane holds its own model and interpreter, which is a few megabytes, plus a pooled scratch buffer per
output tensor sized at load. Two devices is well under 100 MB.
