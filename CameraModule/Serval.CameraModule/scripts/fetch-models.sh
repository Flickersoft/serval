#!/usr/bin/env bash
# Moved to scripts/fetch-models.sh at the repo root — the Server needs the same weights, and one
# script serves both hosts. This stub keeps the CameraModule's default model directory.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export MODEL_DIR="${MODEL_DIR:-$HERE/../models}"
exec "$HERE/../../../scripts/fetch-models.sh" "$@"
