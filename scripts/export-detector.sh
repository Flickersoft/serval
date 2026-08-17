#!/usr/bin/env bash
# Exports the object-detection model Serval is developed and tested against — Ultralytics
# YOLO26n, stock COCO-80, end-to-end head — into $MODEL_DIR/detect as model.onnx + labels.txt.
#
#   MODEL_DIR=./models ./scripts/export-detector.sh
#
# Prefers the official ultralytics Docker image so no Python environment is needed; falls back
# to a local ultralytics install. From a deploy/ directory the compose one-shot does the same:
#
#   docker compose --profile setup run --rm export-detector
#
# labels.txt is written from the model's own class names, so the two cannot disagree — a
# mismatched labels file names every detection wrongly forever, with no error anywhere.
# dynamic=True lets each camera get an input shape at its own aspect ratio; see the notes in
# scripts/fetch-models.sh for the full reasoning and the fixed-shape alternative.
#
# Ultralytics and the YOLO26n weights are AGPL-3.0, the same license as Serval.
set -euo pipefail

ULTRALYTICS_IMAGE="${ULTRALYTICS_IMAGE:-ultralytics/ultralytics:8.4.20}"
DETECT_DIR="${MODEL_DIR:-./models}/detect"
mkdir -p "$DETECT_DIR"

if [ -f "$DETECT_DIR/model.onnx" ] && [ -f "$DETECT_DIR/labels.txt" ]; then
  echo "Detection model already present in $DETECT_DIR."
  exit 0
fi

PYCODE='
from ultralytics import YOLO
m = YOLO("yolo26n.pt")
m.export(format="onnx", simplify=True, dynamic=True)
with open("labels.txt", "w") as f:
    f.writelines(m.names[i] + "\n" for i in range(len(m.names)))
import os
os.replace("yolo26n.onnx", "model.onnx")
os.remove("yolo26n.pt")
print("wrote model.onnx and labels.txt")
'

if command -v docker >/dev/null 2>&1; then
  echo "Exporting via $ULTRALYTICS_IMAGE ..."
  docker run --rm -v "$(cd "$DETECT_DIR" && pwd)":/out -w /out "$ULTRALYTICS_IMAGE" \
    python -c "$PYCODE"
elif python3 -c "import ultralytics" >/dev/null 2>&1; then
  echo "Exporting with the local ultralytics install ..."
  (cd "$DETECT_DIR" && python3 -c "$PYCODE")
else
  echo "Neither Docker nor a local ultralytics install found. Either install Docker, or:" >&2
  echo "  pip install 'ultralytics>=8.4.0'" >&2
  echo "and re-run this script." >&2
  exit 1
fi

echo "Done: $DETECT_DIR/model.onnx + labels.txt"
