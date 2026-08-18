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
# The models root, kept separate from detect/ because the export is handed back to whoever owns
# the root — see the PYCODE below. MODEL_UID/MODEL_GID override that, and matter only where the
# root was created by Docker rather than by a clone.
MODEL_DIR="${MODEL_DIR:-./models}"
DETECT_DIR="$MODEL_DIR/detect"
mkdir -p "$DETECT_DIR"

if [ -f "$DETECT_DIR/model.onnx" ] && [ -f "$DETECT_DIR/labels.txt" ]; then
  echo "Detection model already present in $DETECT_DIR."
  exit 0
fi

# The same script as the export-detector one-shot in deploy/docker-compose.yml, which runs this
# export without a checkout and so cannot reference a file here. The two are copies and have to
# stay identical. Single-quoted, so no apostrophe may appear anywhere inside it.
PYCODE='
import os

# MODEL_DIR is the models root, not detect/ — the ownership this export is handed back to
# lives on the root directory, and abspath is taken before the chdir below so a relative
# MODEL_DIR from a checkout still resolves afterwards.
models = os.path.abspath(os.environ.get("MODEL_DIR", "/models"))
detect = os.path.join(models, "detect")
os.makedirs(detect, exist_ok=True)
os.chdir(detect)


def hand_back():
    # Root wrote everything here, into a bind mount of a directory belonging to whoever
    # ran this, so it is handed back to the ownership of the models root itself — which
    # is what a clone already carries — or to MODEL_UID/MODEL_GID where there was no
    # clone and Docker created that directory as root. In a finally block because a
    # failed export still leaves the downloaded weights behind, and those have to be as
    # deletable as a finished run. Skipped when not root, which is this same script run
    # against a local ultralytics install by its owner. A refusal is a warning rather
    # than a failure: the export itself succeeded.
    if os.geteuid() != 0:
        return
    st = os.stat(models)
    uid = int(os.environ.get("MODEL_UID") or st.st_uid)
    gid = int(os.environ.get("MODEL_GID") or st.st_gid)
    try:
        for path, _, names in os.walk(detect):
            os.chown(path, uid, gid)
            for name in names:
                os.chown(os.path.join(path, name), uid, gid)
    except OSError as err:
        print(f"WARNING: {detect} stays owned by root ({err}).")


try:
    from ultralytics import YOLO
    m = YOLO("yolo26n.pt")
    m.export(format="onnx", simplify=True, dynamic=True)
    with open("labels.txt", "w") as f:
        f.writelines(m.names[i] + "\n" for i in range(len(m.names)))
    os.replace("yolo26n.onnx", "model.onnx")
    os.remove("yolo26n.pt")
    print("wrote model.onnx and labels.txt")
finally:
    hand_back()
'

if command -v docker >/dev/null 2>&1; then
  echo "Exporting via $ULTRALYTICS_IMAGE ..."
  # The models root is mounted rather than detect/, because the script reads the ownership of the
  # root to decide who the export belongs to. Naming MODEL_UID/MODEL_GID without a value passes
  # each one only if it is set here, so an unset variable stays unset in the container.
  docker run --rm \
    -v "$(cd "$MODEL_DIR" && pwd)":/models \
    -e MODEL_DIR=/models -e MODEL_UID -e MODEL_GID \
    "$ULTRALYTICS_IMAGE" python -c "$PYCODE"
elif python3 -c "import ultralytics" >/dev/null 2>&1; then
  echo "Exporting with the local ultralytics install ..."
  MODEL_DIR="$MODEL_DIR" python3 -c "$PYCODE"
else
  echo "Neither Docker nor a local ultralytics install found. Either install Docker, or:" >&2
  echo "  pip install 'ultralytics>=8.4.0'" >&2
  echo "and re-run this script." >&2
  exit 1
fi

echo "Done: $DETECT_DIR/model.onnx + labels.txt"
