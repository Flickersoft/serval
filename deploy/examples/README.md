# Deployment examples

Two real, tested deployment shapes and one optional override. Neither deployment is the
recommended starting point — that is [../compose.yaml](../compose.yaml) — but both show what a
tuned deployment looks like on specific hardware, and their comments carry the measurements behind
every number.

| File | Host it was written against | What it demonstrates |
|---|---|---|
| [compose.google-home.yaml](compose.google-home.yaml) | Any — an override for `../compose.yaml` | Cameras in Google Home and the Cast button. Needs public HTTPS; read [Docs/google-home.md](../../Docs/google-home.md) first |
| [docker-compose.amd-gpu.yml](docker-compose.amd-gpu.yml) | TrueNAS SCALE box with an AMD APU (Vega iGPU) | TrueNAS Apps deployment (no `build:`, `pull_policy: always`), bind mounts on ZFS datasets, VAAPI encode + optional Vulkan vision offload on one device, full server-side AI on |
| [docker-compose.intel-coral.yml](docker-compose.intel-coral.yml) | Intel N100 (4 cores) with two USB Coral Edge TPUs | Coral USB passthrough done right (`/dev/bus/usb` + the `device_cgroup_rules` line every guide omits), detection on the TPUs, tiled regions, thread budgets for a 4-core host, Intel GPU stats via `CAP_PERFMON` |

Values marked `HOST-SPECIFIC` — mount paths, LAN addresses, secrets, retention — are one
machine's choices and must be replaced, not copied. Everything else is a measured default worth
keeping unless your hardware disagrees.
