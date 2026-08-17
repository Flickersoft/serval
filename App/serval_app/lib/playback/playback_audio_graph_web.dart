import 'dart:async';
import 'dart:js_interop';

import 'package:flutter/foundation.dart';
import 'package:web/web.dart' as web;

import 'playback_audio_graph.dart';
import 'playback_volume.dart';

/// The module that registers the `playback-gate` processor. Vendored beside `hls.js` and served with
/// the page, for the same reason it is — an offline run should still work.
const _gateModuleUrl = 'playback_gate.js';

/// `AudioParamMap` is exposed by `package:web` as a bare `JSObject`, so its Map-like `get` has to be
/// declared to be reachable. The alternative is recreating the worklet node on every threshold change,
/// which would put a gap in the audio each time the slider moved.
extension type _AudioParamMap._(JSObject _) implements JSObject {
  external web.AudioParam? get(String name);
}

/// One context for the whole page.
///
/// Browsers cap how many an origin may have, and there are up to three players alive at once — the
/// live view, the scrubber and a clip. Created on first use rather than at startup so a session that
/// never boosts anything never makes one.
web.AudioContext? _context;
bool _contextUnavailable = false;

web.AudioContext? _sharedContext() {
  if (_contextUnavailable) return null;
  final existing = _context;
  if (existing != null) return existing;

  try {
    return _context = web.AudioContext();
  } on Object catch (error) {
    // Old or locked-down browsers. Remembered so this is not retried on every attach, and reported
    // once: from here on every camera silently falls back to unboosted playback, which is worth
    // being able to find in a console log.
    _contextUnavailable = true;
    debugPrint('Serval: no AudioContext, playback gain unavailable ($error).');
    return null;
  }
}

/// Elements already routed through a graph.
///
/// `createMediaElementSource` may be called only once per element, and calling it twice throws — so
/// this is not an optimisation. The live view's `<audio>` element in particular outlives individual
/// attach attempts, because it belongs to `flutter_webrtc` rather than to us.
final _claimed = <String>{};

/// Resolves once the gate module has loaded, or to false if it cannot.
Future<bool>? _gateModule;

Future<bool> _ensureGateModule(
  web.AudioContext context,
) => _gateModule ??= context.audioWorklet
    .addModule(_gateModuleUrl)
    .toDart
    .then((_) => true)
    .onError<Object>((error, _) {
      // Deliberately not fatal. Without the gate a boosted camera hisses during silence; without the
      // audio it is silent. The first is a worse experience, the second is a bug report, so the
      // chain is built either way and this is logged.
      debugPrint(
        'Serval: playback gate worklet did not load, boost will hiss ($error).',
      );
      return false;
    });

PlaybackGainChain? attachPlatformPlaybackGain(String elementId) {
  if (_claimed.contains(elementId)) return null;

  final element = web.document.getElementById(elementId);
  if (element == null || !element.isA<web.HTMLMediaElement>()) return null;

  final context = _sharedContext();
  if (context == null) return null;

  try {
    final chain = _WebPlaybackGainChain(
      context,
      elementId,
      element as web.HTMLMediaElement,
    );
    _claimed.add(elementId);
    return chain;
  } on Object catch (error) {
    debugPrint('Serval: could not route $elementId through WebAudio ($error).');
    return null;
  }
}

void resumePlatformPlaybackAudio() {
  final context = _context;
  if (context == null) return;
  if (context.state == 'suspended') {
    // Fire and forget: it resolves on the next tick and there is nothing to do differently if it
    // fails, since a context that will not resume is a context whose audio nobody can hear anyway.
    context.resume().toDart.ignore();
  }
}

/// gate → gain → limiter → destination, around one media element.
class _WebPlaybackGainChain implements PlaybackGainChain {
  _WebPlaybackGainChain(
    this._context,
    this._elementId,
    web.HTMLMediaElement element,
  ) : _source = _context.createMediaElementSource(element),
      _gain = _context.createGain(),
      _limiter = _context.createDynamicsCompressor() {
    // The limiter, and it is load-bearing rather than a safety net: 100x puts a transient that
    // already reaches full scale far past the ceiling, so without this, boost trades inaudible
    // content for a burst of clipping. A hard knee and a high ratio because this is a limiter, not
    // a compressor — it should do nothing at all until the ceiling is reached.
    _limiter.threshold.value = -6;
    _limiter.knee.value = 0;
    _limiter.ratio.value = 20;
    _limiter.attack.value = 0.003;
    _limiter.release.value = 0.25;
    // No makeup gain: _gain ahead of it is already supplying all of it.

    // Connected without the gate to begin with, because the worklet loads asynchronously and audio
    // must not wait on it. The gate is spliced in when it arrives.
    _source.connect(_gain);
    _gain.connect(_limiter);
    _limiter.connect(_context.destination);

    unawaited(_installGate());
  }

  final web.AudioContext _context;
  final String _elementId;
  final web.MediaElementAudioSourceNode _source;
  final web.GainNode _gain;
  final web.DynamicsCompressorNode _limiter;

  web.AudioWorkletNode? _gate;
  bool _disposed = false;

  /// The threshold as last asked for, held because [setGate] can be called before the worklet exists.
  double? _pendingGateRms;

  Future<void> _installGate() async {
    final context = _context;
    if (!await _ensureGateModule(context) || _disposed) return;

    try {
      final gate = web.AudioWorkletNode(
        context,
        'playback-gate',
        web.AudioWorkletNodeOptions(
          numberOfInputs: 1,
          numberOfOutputs: 1,
          parameterData:
              {
                    'threshold': _pendingGateRms ?? 0,
                    'floor': gateFloor,
                    'attackMs': gateAttackMs,
                    'releaseMs': gateReleaseMs,
                  }.jsify()!
                  as JSObject,
        ),
      );

      // Splice it in ahead of the gain. Reordering a live graph is audible only as the gate's own
      // first move, which is why the parameters above are set at construction rather than after.
      _source.disconnect();
      _source.connect(gate);
      gate.connect(_gain);
      _gate = gate;
    } on Object catch (error) {
      debugPrint(
        'Serval: playback gate node failed, boost will hiss ($error).',
      );
    }
  }

  @override
  void setLevel({required double volume, required double db}) {
    if (_disposed) return;
    _gain.gain.value = normalizeVolume(volume) * boostFactor(db);
  }

  @override
  void setGate(double? rms) {
    _pendingGateRms = rms;
    if (_disposed) return;
    // 0 is the worklet's bypass, and it copies through bit-transparently there — so "no gate" and
    // "gate at zero" are the same thing on purpose, matching what a null threshold means everywhere
    // else.
    _gate?.parameters.threshold?.value = (rms ?? 0).toDouble();
  }

  @override
  void dispose() {
    if (_disposed) return;
    _disposed = true;

    // Disconnected but not forgotten: the element stays claimed for the life of the page, because
    // `createMediaElementSource` cannot be undone and a second call on the same element throws.
    try {
      _gate?.disconnect();
      _source.disconnect();
      _gain.disconnect();
      _limiter.disconnect();
    } on Object catch (error) {
      debugPrint(
        'Serval: tearing down the gain chain for $_elementId ($error).',
      );
    }
  }
}

extension on web.AudioParamMap {
  /// The named parameter, or null if the processor declared no such thing.
  web.AudioParam? get threshold => (this as _AudioParamMap).get('threshold');
}
