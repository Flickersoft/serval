# Deployment examples

Two tested hardware configurations and one optional integration override. For a standard install,
start from [../compose.yaml](../compose.yaml); these files show how to adapt it for specific GPU,
Coral Edge TPU, or ZFS setups.

| File | Target host | What it configures |
|---|---|---|
| [compose.google-home.yaml](compose.google-home.yaml) | Any — Compose override for `../compose.yaml` | Google Home integration and Chromecast receiver. Requires public HTTPS; see [Docs/google-home.md](../../Docs/google-home.md) |
| [docker-compose.amd-gpu.yml](docker-compose.amd-gpu.yml) | TrueNAS SCALE host with an AMD APU (Vega iGPU) | Pre-built GHCR image (`pull_policy: always`), ZFS dataset bind mounts, VAAPI transcoding + Vulkan vision offload on `/dev/dri`, full server-side AI enabled |
| [docker-compose.intel-coral.yml](docker-compose.intel-coral.yml) | Intel N100 (4 cores) with two USB Coral Edge TPUs | Coral USB passthrough (`/dev/bus/usb` with `device_cgroup_rules` so USB re-enumeration works after firmware upload), Edge TPU detection, tiled regions, thread limits for 4 cores, Intel iGPU stats via `CAP_PERFMON` |

Values marked `HOST-SPECIFIC` (mount paths, LAN IP addresses, secrets, retention days) must be
updated for your machine before running.
