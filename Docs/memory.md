# Memory

Serval's memory is dominated by things that are not the server: one ffmpeg per stream, a model on
the GPU, and page cache from writing recordings. The process itself is the smallest part of it. That
is why the per-service `mem_limit` figures in [deploy/examples/](../deploy/examples/) look larger
than the code would suggest, and why the number the app reports is not the number `docker stats`
reports.

Everything below was measured on one reference host unless it says otherwise: an Intel N100, four
cores, 15.4 GiB usable, seven cameras, Coral USB detection, and the full AI stack with
`GpuLayers=99`. Where a figure is estimated it says so.

## The host budget

The limits are per service and nothing adds them up for you:

| Service | `mem_limit` | Measured | Notes |
|---|---|---|---|
| **server** | **12g** | 12.0 GiB charged, ~9.2 GiB working set | Fills its limit by design; see [below](#what-the-warning-measures) |
| mongo | 2g | 632 MiB | Also capped at `--wiredTigerCacheSizeGB 1` |
| go2rtc | 512m | 83 MiB | Only while a viewer is connected |

That is 14.5 GiB of limits on a 15.4 GiB box. It does not overcommit in practice, because the
server's limit is mostly reclaimable page cache that the kernel hands back under pressure rather
than memory anyone is holding — but the arithmetic is worth knowing before raising anything. The
example host at [docker-compose.intel-coral.yml](../deploy/examples/docker-compose.intel-coral.yml)
ships `10g` for the same shape; the reference host runs `12g` because it carries seven cameras
rather than six and every model enabled.

## What a camera costs

One ffmpeg per stream role, measured across seven cameras:

| Session | Per camera | What it does |
|---|---|---|
| **detect + snapshot** | **~200 MB** | Sub stream: raw detect frames, JPEG stills, and the preview ring |
| record | ~100 MB | Main stream, stream-copy to HLS — no decode, which is why it is the cheaper one |
| audio tap | ~55 MB | Only on cameras with audio enabled |

Seven cameras came to ~2,250 MB of ffmpeg. Budget **~300 MB per camera** with detection and
recording both on, and note that the detect session is the expensive one because it is the only one
that decodes: a recording is a copy.

## The fixed stack

- **`dotnet` itself: ~3.0 GB resident.** The managed heap, ONNX Runtime for the audio models, the
  Coral runtime, and llama.cpp's host-side allocations. Not the vision model's weights — see below.
- **Mongo: 632 MiB** against a 1 GB WiredTiger cache cap. It stays small and wants IOPS, not RAM.
- **Page cache: whatever is left.** Recordings on their way to disk, reclaimable, and grown by the
  kernel into whatever the limit leaves spare — so it is a consequence of the limit you set rather
  than a cost to add up. On a busy recorder it is easily the largest line here. Not a leak, and not
  something to tune.

## Why Intel looks different

With `GpuLayers` set, the vision model's weights, projector and KV cache do not appear anywhere in
Serval's address space. i915 charges GEM buffers to whichever cgroup allocated them and accounts
them as **shmem**, so on an Intel host the model shows up as ~2.9 GiB of shared memory that the
process never maps. amdgpu accounts the same allocation elsewhere, which is why an AMD host's cgroup
shows tens of MiB of shmem for the identical model and settings.

The consequence is that `10g` on an Intel host is roughly 4 GB of headroom, not the 8 GB the process
figures suggest.

To confirm where it went, read the render node's own accounting rather than guessing from `ps`:

```bash
# The container's pid, not the server's own: pgrep -f matches its own command line too.
PID=$(sudo docker inspect -f '{{.State.Pid}}' <server>)

# The glob goes inside sudo — /proc/<pid>/fdinfo is unreadable to anyone else, so a shell
# expanding it first finds nothing.
sudo sh -c "grep -sH -E 'drm-resident-system0|drm-engine-render' /proc/$PID/fdinfo/*"
#   /proc/2180/fdinfo/348:drm-resident-system0:  0
#   /proc/2180/fdinfo/348:drm-engine-render:     0 ns
#   /proc/2180/fdinfo/354:drm-resident-system0:  2942460 KiB          ← the model, on the iGPU
#   /proc/2180/fdinfo/354:drm-engine-render:     24141437569132 ns    ← and doing work
```

There are two render-node handles and only one of them holds anything; that is normal. A large
resident figure with `drm-engine-render` at zero would not be: it would mean memory was allocated
and nothing ever ran on it.

## What the warning measures

The app warns at 90% of the container's limit, and the figure behind it is **not** `memory.current`.
`MemoryStats.UsedBytes` is `memory.current` less `inactive_file`
([CgroupV2.cs](../Server/Serval.Server/Vitals/CgroupV2.cs)) — reporting the raw figure would read as
100% forever on any host whose media volume is not ZFS.

Two things follow, and both surprise people:

- **`active_file` is not subtracted.** On the reference host that is ~2.2 GiB of page cache —
  genuinely reclaimable — counted against the threshold. The reported percentage is deliberately
  conservative and reads high on a healthy NVR.
- **`docker stats` will not agree with the app**, and the app is the one worth watching. `docker
  stats` shows the charge including all cache.

So the level itself says less than you would expect — where it settles depends on how many cameras
are recording and how much limit is left for cache to grow into. What matters is the shape: a
working set that rises and falls is cache doing its job, and one that climbs over days and never
comes back down is something nobody is reaping.

## The vision model

Qwen3-VL-2B Q8_0 plus its mmproj is ~2.3 GB on disk and ~2.9 GiB resident once loaded and offloaded.
It is the single largest consumer on the host and the only one where a smaller number is available
for the asking: a Q4 quantisation roughly halves it. That is a quality trade, not free — see
[deployment.md](deployment.md#server-side-ai-on-a-gpu) for what the offload buys and what it costs.

Turning the vision model off entirely is the difference between the ~8–10 GB budget and the ~2 GB
recording-only one quoted in the [root README](../README.md#storage-and-sizing).

## When the number is wrong

A working set that grows and never falls is a process nobody is reaping, not cache. The check is
process counts, not memory:

```bash
pgrep -c ffprobe    # expect 0-2; a probe should live seconds
pgrep -c ffmpeg     # expect roughly one per stream role, so ~2-3 per camera
```

What strands a process is a camera that stops answering without closing its TCP connection: the
socket stays ESTABLISHED, RTSP carries no keepalive to discover the peer is gone, and anything
reading it waits forever. Cameras do this, and nothing on this end can stop them.

What Serval controls is whether that costs a process, and it is bounded in two places: a socket
deadline handed to ffmpeg and ffprobe themselves
([SourceArguments.cs](../Server/Serval.Server/Ingest/SourceArguments.cs)), which is what normally
ends the child and lets it report why, and a kill of the whole process tree whenever a helper is
abandoned ([ChildProcess.cs](../Server/Serval.Server/ChildProcess.cs)), which is what guarantees it
even when the deadline does not fire. Disposing a process does neither — it releases the handle and
leaves the child running — so the kill is deliberate rather than incidental.

A count that keeps climbing therefore means one of those two is not holding, and that is where to
look rather than at the memory figure.
