#!/usr/bin/env bash
# Downloads the model weights Serval needs. Weights are not in git.
#
# Serves both hosts: the Server (server-side AI for IP cameras) and the CameraModule (edge AI).
# MODEL_DIR says where the weights land — default is ./models under the current directory:
#
#   cd deploy && MODEL_DIR=./models ../scripts/fetch-models.sh   # a Server deployment
#   docker compose --profile setup run --rm fetch-models         # same, without a clone
#   ./scripts/fetch-models.sh                                    # a CameraModule checkout, via its stub
#
# Partial installs: SKIP_VISION=1 (the 2.3 GB Qwen3-VL), SKIP_SPEAKER=1, SKIP_SOUND=1,
# SKIP_DETECTION=1 each skip one capability's files.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MODEL_DIR="${MODEL_DIR:-./models}"
mkdir -p "$MODEL_DIR"

# The general multilingual SenseVoice (zh/en/ja/ko/yue) with emotion + audio events.
#
# Do NOT "upgrade" this to sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09.
# Despite the identical-looking name, that build is a Cantonese-specialised fine-tune
# (ASLP-lab/WSYue-ASR). It reports language=yue for every input and garbles English.
SENSEVOICE="sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17"
SENSEVOICE_URL="https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/${SENSEVOICE}.tar.bz2"

if [ ! -f "$MODEL_DIR/$SENSEVOICE/model.int8.onnx" ]; then
  echo "Downloading SenseVoice (ASR + emotion + audio events)..."
  curl -fL --retry 3 "$SENSEVOICE_URL" -o "/tmp/${SENSEVOICE}.tar.bz2"
  tar -xjf "/tmp/${SENSEVOICE}.tar.bz2" -C "$MODEL_DIR"
  rm -f "/tmp/${SENSEVOICE}.tar.bz2"
  echo "SenseVoice ready."
else
  echo "SenseVoice already present."
fi

# Silero VAD v5 is vendored in git (2.3MB) rather than downloaded, so its version is pinned —
# sherpa-onnx's release asset of the same name is v4 and segments differently. It is copied from
# a seed rather than fetched, looked for in order: $SILERO_SEED, the repo copy relative to this
# script, and the server image's baked-in copy (which is how the compose one-shot finds it
# without a clone).
if [ ! -f "$MODEL_DIR/silero_vad.onnx" ]; then
  for seed in \
      "${SILERO_SEED:-}" \
      "$SCRIPT_DIR/../CameraModule/Serval.CameraModule/models/silero_vad.onnx" \
      "$SCRIPT_DIR/models-seed/silero_vad.onnx"; do
    if [ -n "$seed" ] && [ -f "$seed" ]; then
      echo "Copying Silero VAD from $seed ..."
      cp "$seed" "$MODEL_DIR/silero_vad.onnx"
      break
    fi
  done
  if [ ! -f "$MODEL_DIR/silero_vad.onnx" ]; then
    echo "ERROR: no Silero VAD seed found. It is tracked in git at" >&2
    echo "  CameraModule/Serval.CameraModule/models/silero_vad.onnx" >&2
    echo "Run this script from a checkout, or point SILERO_SEED at a copy of that file." >&2
    exit 1
  fi
else
  echo "Silero VAD already present."
fi

# Qwen3-VL: scene description. Needs both the weights and the mmproj vision projector —
# the model cannot see images without the projector.
#
# Chosen over Gemma 4 because Gemma 4's vision cannot run on the RK3588 NPU (RKLLM lists
# Gemma4 under text models only), and this keeps the NPU option open for later.
VLM_DIR="$MODEL_DIR/Qwen3-VL-2B-Instruct-GGUF"
VLM_BASE="https://huggingface.co/ggml-org/Qwen3-VL-2B-Instruct-GGUF/resolve/main"

if [ "${SKIP_VISION:-0}" = "1" ]; then
  echo "Skipping vision model (SKIP_VISION=1)."
else
  mkdir -p "$VLM_DIR"
  for f in "Qwen3-VL-2B-Instruct-Q8_0.gguf" "mmproj-Qwen3-VL-2B-Instruct-Q8_0.gguf"; do
    if [ ! -f "$VLM_DIR/$f" ]; then
      echo "Downloading $f ..."
      curl -fL --retry 3 "$VLM_BASE/$f" -o "$VLM_DIR/$f"
    else
      echo "$f already present."
    fi
  done
fi

# Speaker labelling. Two models for two independent jobs:
#   - embedding: live per-utterance speaker labels
#   - pyannote segmentation: after-the-fact diarization over a whole conversation
SPK_DIR="$MODEL_DIR/speaker"
EMB_URL="https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models"
SEG_URL="https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models"
EMB_MODEL="3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"
SEG_MODEL="sherpa-onnx-pyannote-segmentation-3-0"

if [ "${SKIP_SPEAKER:-0}" = "1" ]; then
  echo "Skipping speaker models (SKIP_SPEAKER=1)."
else
  mkdir -p "$SPK_DIR"

  if [ ! -f "$SPK_DIR/$EMB_MODEL" ]; then
    echo "Downloading speaker embedding model ..."
    curl -fL --retry 3 "$EMB_URL/$EMB_MODEL" -o "$SPK_DIR/$EMB_MODEL"
  else
    echo "Speaker embedding model already present."
  fi

  if [ ! -f "$SPK_DIR/$SEG_MODEL/model.onnx" ]; then
    echo "Downloading pyannote segmentation model ..."
    curl -fL --retry 3 "$SEG_URL/$SEG_MODEL.tar.bz2" -o "/tmp/$SEG_MODEL.tar.bz2"
    tar -xjf "/tmp/$SEG_MODEL.tar.bz2" -C "$SPK_DIR"
    rm -f "/tmp/$SEG_MODEL.tar.bz2"
  else
    echo "Segmentation model already present."
  fi

  # Fixtures with KNOWN speaker counts, for ./camera-module-tools --speakers. English only: a fixture
  # nobody here can read is a fixture nobody can sanity-check, and sherpa's Mandarin four-speaker
  # clip was only ever pinning a threshold by its speaker count while its words went unread.
  mkdir -p "$SPK_DIR/fixtures"
  for f in "1-two-speakers-en.wav" "2-two-speakers-en.wav"; do
    [ -f "$SPK_DIR/fixtures/$f" ] || curl -fsL --retry 3 "$SEG_URL/$f" -o "$SPK_DIR/fixtures/$f"
  done

fi

# AudioSet tagging: 527 non-speech classes — car horns, glass, dogs, sirens, doors. This is what
# hears anything the VAD rejects, which is everything that is not speech.
#
# The *small* zipformer, and int8, deliberately: it runs alongside SenseVoice and the VLM on the
# same cores, and 26 MB against the 88 MB fp32 build costs accuracy the thresholds absorb.
# class_labels_indices.csv ships with the weights and must stay beside them — without it every
# class comes back unnamed.
TAGGING="sherpa-onnx-zipformer-small-audio-tagging-2024-04-15"
TAGGING_URL="https://github.com/k2-fsa/sherpa-onnx/releases/download/audio-tagging-models/${TAGGING}.tar.bz2"

if [ "${SKIP_SOUND:-0}" = "1" ]; then
  echo "Skipping audio tagging model (SKIP_SOUND=1)."
elif [ ! -f "$MODEL_DIR/$TAGGING/model.int8.onnx" ]; then
  echo "Downloading audio tagging model (AudioSet, 527 classes)..."
  curl -fL --retry 3 "$TAGGING_URL" -o "/tmp/${TAGGING}.tar.bz2"
  tar -xjf "/tmp/${TAGGING}.tar.bz2" -C "$MODEL_DIR"
  rm -f "/tmp/${TAGGING}.tar.bz2"
  echo "Audio tagging model ready."
else
  echo "Audio tagging model already present."
fi

# Object detection: what is present in a frame, which is what gates the vision model on the Server.
#
# Not downloaded — you supply the weights. The decoder reads the end-to-end detection head: output
# [1, detections, 6], one row per detection carrying x1, y1, x2, y2, score, class.
#
# What Serval is developed and tested against, so there is one known-good answer:
#
#   yolo export model=yolo26n.pt format=onnx simplify=True dynamic=True
#
# Ultralytics YOLO26n, stock COCO-80 — [1, 300, 6], with the 80 COCO names as labels.txt. Needs
# ultralytics 8.4.0 or newer, where the YOLO26 weights landed.
#
# dynamic=True is the shape that matters. With it the model takes any stride-32 input, and Serval
# gives each camera the shape nearest its own aspect at the Detection:InputPixels budget: 640x384 for
# a 16:9 stream, 416x576 for a 3:4 doorbell, 960x256 for a 32:9 panoramic. Those last two are the
# point — forced into a landscape 640x384 they are 45% and 47% picture, the rest mid-grey the
# convolutions pay for anyway, with the subject shrunk 40% before the model sees it. Dynamic axes are
# free: bit-identical boxes and inference within noise of a fixed export at the same shape.
#
# A fixed shape still works and pins every camera to it — pass imgsz=384,640 (height,width) instead.
# That is what a single-camera SBC wants, and what an accelerator needs: RKNN and Edge TPU compile
# ahead of time against one shape, and the TensorRT and OpenVINO providers build an engine per shape.
#
# Do not pass end2end=False. It asks for the one-to-many head, which this does not decode and which
# is rejected at startup. A pre-YOLO26 model needs nms=True to get the end-to-end head at all — that
# is how the tested YOLOv8n export was made — since YOLO11, v8 and everything before them have the
# one-to-many head by default. Stock YOLOX and D-FINE do not drop in either: YOLOX is one-to-many and
# transposed, D-FINE is DETR-style with separate outputs.
#
# Whatever you choose, the labels file must match the weights exactly, and nothing checks it at
# load: this head declares no class count, so there is nothing to compare a labels file against. A
# mismatched list produces confidently *wrong* names forever with no other symptom anywhere in the
# system. Only a class index past the end of the file is noticed, as a warning on the first frame.
DETECT_DIR="$MODEL_DIR/detect"

if [ "${SKIP_DETECTION:-0}" = "1" ]; then
  echo "Skipping detection model check (SKIP_DETECTION=1)."
elif [ -f "$DETECT_DIR/model.onnx" ] && [ -f "$DETECT_DIR/labels.txt" ]; then
  echo "Detection model already present."
else
  mkdir -p "$DETECT_DIR"
  echo
  echo "Object detection model NOT fetched — export it (a two-minute step):"
  echo "  $DETECT_DIR/model.onnx    an end-to-end YOLO ONNX export"
  echo "  $DETECT_DIR/labels.txt    one class name per line, in the model's own order"
  echo
  echo "With Docker (no Python needed) — from the deploy/ directory:"
  echo "  docker compose --profile setup run --rm export-detector"
  echo
  echo "Or by hand (needs ultralytics >= 8.4.0):"
  echo "  pip install ultralytics"
  echo "  yolo export model=yolo26n.pt format=onnx simplify=True dynamic=True"
  echo "  cp yolo26n.onnx $DETECT_DIR/model.onnx"
  echo "  # dynamic=True lets each camera get a shape at its own aspect, which is what a portrait"
  echo "  # doorbell or an ultrawide needs; it costs nothing against a fixed export at one shape."
  echo "  # for a fixed shape instead, pass imgsz=384,640 (height,width; both axes divide by 32)"
  echo "  # labels.txt is the 80 COCO names, one per line, in the model's own order"
  echo "  # do NOT pass end2end=False; a pre-YOLO26 model additionally needs nms=True"
  echo
  echo "Until both files exist the Server warns at startup, names the paths, and cameras keep"
  echo "using the motion gate."
  echo
fi

echo "All models present in $MODEL_DIR"
