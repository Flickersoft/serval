import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../data/serval_repository.dart';
import '../models/camera.dart';
import 'microphone_gate.dart';
import 'playback_audio_graph.dart';
import 'playback_volume.dart';

enum WebRtcStage { connecting, live, failed }

/// One live WebRTC session to one camera: signalling, tracks and level —
/// everything but the pixels. `WebRtcView` renders [renderer] and forwards its
/// knobs here; owning the session outside the widget tree is what keeps a
/// prop-driven widget from doing network I/O.
///
/// A session is for one camera, once: switching cameras is a new session, not
/// a restart of this one.
class WebRtcSession {
  WebRtcSession({
    required this._repository,
    required this._camera,
    required this._talking,
    required this._theirAudio,
    required this._volume,
    required this._gainDb,
    this._gateRms,
  });

  final ServalRepository _repository;
  final Camera _camera;

  final renderer = RTCVideoRenderer();

  /// Where the session is, for the view to draw. [failure] carries the reason
  /// once this lands on [WebRtcStage.failed].
  final stage = ValueNotifier<WebRtcStage>(WebRtcStage.connecting);
  String? failure;

  RTCPeerConnection? _connection;
  MediaStreamTrack? _remoteAudio;

  /// The sending half of the audio, on a camera that has a backchannel. Declared with the offer
  /// and empty until someone speaks — see [_talkbackMicrophone].
  RTCRtpTransceiver? _talkback;

  /// The WebAudio chain carrying this camera's gain, on the web and only above 0 dB. Null on the
  /// desktop always — libwebrtc's own volume goes past unity there, so there is nothing to build.
  PlaybackGainChain? _gain;

  /// Set once a chain could not be built, so it is not retried on every level change.
  bool _gainUnavailable = false;

  /// The empty stream the sending audio is declared against. Held only so it can be released:
  /// nothing is ever added to it.
  MediaStream? _talkbackStream;

  /// Whether the microphone has been handed to [_talkback] yet. The handover is once per session;
  /// after it, a press only flips `enabled`.
  bool _attached = false;

  bool _talking;
  bool _theirAudio;
  double _volume;
  double _gainDb;
  double? _gateRms;

  /// Guards against a late answer or track arriving after [dispose].
  bool _disposed = false;

  /// The microphone, opened on the first press and not before — see [MicrophoneGate] for why the
  /// operating system's indicator is worth this much machinery.
  late final _talkbackMicrophone = MicrophoneGate<MediaStream>(
    open: () => navigator.mediaDevices.getUserMedia(const {
      'audio': true,
      'video': false,
    }),
    route: _routeMicrophone,
    close: _closeMicrophone,
  );

  /// Where the microphone is, for *Hold to talk* to say so. [micFailure] carries the reason it
  /// could not be opened, for a log line rather than the screen.
  ValueListenable<MicStage> get micStage => _talkbackMicrophone.stage;
  String? get micFailure => _talkbackMicrophone.failure;

  /// The peer connection's own last word, or null before it has said anything.
  ///
  /// Kept because [stage] is not a substitute: a session that has been [WebRtcStage.live] and lost
  /// its transport reads live until something notices, and "something notices" is exactly what
  /// this is for.
  RTCPeerConnectionState? _connectionState;

  /// Set while a [RTCPeerConnectionStateDisconnected] is being given time to sort itself out.
  Timer? _settling;

  /// How long a dropped transport is given to come back on its own before the view is told.
  ///
  /// WebRTC recovers from `disconnected` routinely — a Wi-Fi handover, a moment of congestion —
  /// and swapping the picture out for a fallback on every one of those would be a worse view than
  /// the frozen frame this exists to fix. Long enough to cover a recovery, short enough that a
  /// picture nobody should trust is not on screen for a whole breath.
  static const _settleAfter = Duration(seconds: 5);

  /// Whether this session still believes it has a transport.
  ///
  /// Read on resume, where it is the difference between rebuilding a perfectly good peer connection
  /// and leaving it alone. Null — nothing reported yet — counts as healthy: a session still in the
  /// middle of starting has not failed, and tearing it down would restart the handshake it is in.
  bool get isHealthy =>
      _connectionState == null ||
      _connectionState == RTCPeerConnectionState.RTCPeerConnectionStateNew ||
      _connectionState ==
          RTCPeerConnectionState.RTCPeerConnectionStateConnecting ||
      _connectionState ==
          RTCPeerConnectionState.RTCPeerConnectionStateConnected;

  Future<void> start() async {
    try {
      await renderer.initialize();
      _applyVolume();

      final connection = await createPeerConnection(const {
        // No STUN or TURN. go2rtc advertises the host's address as an ICE candidate and both
        // ends are on the LAN, which is the deployment this is for — see the Server README's
        // note that reaching it from outside needs a TURN server that is not set up.
        'iceServers': <Map<String, dynamic>>[],
        'sdpSemantics': 'unified-plan',
      });
      if (_disposed) {
        await connection.close();
        return;
      }
      _connection = connection;

      connection.onTrack = (event) {
        if (_disposed) return;
        if (event.track.kind == 'audio') {
          _remoteAudio = event.track;
          _remoteAudio?.enabled = _theirAudio;
        }
        if (event.streams.isNotEmpty) {
          renderer.srcObject = event.streams.first;
          // Again here: the native path needs a source object before it can reach a gain, so the
          // call at initialise is a no-op and this is the one that takes effect.
          _applyVolume();
          stage.value = WebRtcStage.live;
        }
      };

      connection.onConnectionState = (state) {
        if (_disposed) return;
        _connectionState = state;

        switch (state) {
          // Recovered, or never really gone. Whatever was being waited out is moot.
          case RTCPeerConnectionState.RTCPeerConnectionStateConnected:
            _settling?.cancel();
            _settling = null;

          // Not a failure yet, and treating it as one is how a Wi-Fi handover becomes a fallback
          // card. But it is also what a phone's session lands in while the App is in the
          // background, where the picture on screen is frozen and being presented as live — so it
          // gets a countdown rather than a shrug.
          case RTCPeerConnectionState.RTCPeerConnectionStateDisconnected:
            _settling?.cancel();
            _settling = Timer(_settleAfter, () {
              if (_disposed) return;
              if (_connectionState ==
                  RTCPeerConnectionState.RTCPeerConnectionStateConnected) {
                return;
              }
              // Back to connecting rather than failed: nothing has said this cannot work, and the
              // view restarts from here. `failed` is for what will not come back on its own.
              stage.value = WebRtcStage.connecting;
            });

          // Both are terminal for this peer connection. A new session is the only way on, which
          // `WebRtcView` reads this stage to know.
          case RTCPeerConnectionState.RTCPeerConnectionStateFailed:
          case RTCPeerConnectionState.RTCPeerConnectionStateClosed:
            _settling?.cancel();
            _settling = null;
            failure = 'The connection to the camera dropped.';
            stage.value = WebRtcStage.failed;

          case RTCPeerConnectionState.RTCPeerConnectionStateNew:
          case RTCPeerConnectionState.RTCPeerConnectionStateConnecting:
            break;
        }
      };

      // Declared as transceivers rather than inferred, so the offer says what we want even before
      // anything is added to it. Video is only ever received.
      await connection.addTransceiver(
        kind: RTCRtpMediaType.RTCRtpMediaTypeVideo,
        init: RTCRtpTransceiverInit(direction: TransceiverDirection.RecvOnly),
      );

      if (_camera.twoWayAudio) {
        // Audio goes out sending, but with nothing on it. The microphone is not opened until
        // someone holds the button, and `replaceTrack` fills this sender in later without
        // touching the SDP — which is the point, since there is nowhere to renegotiate to.
        //
        // The empty stream is what keeps the offer the shape go2rtc has always answered: a sender
        // declared without one writes `a=msid:-` instead of naming a stream.
        _talkbackStream = await createLocalMediaStream('talkback');
        _talkback = await connection.addTransceiver(
          kind: RTCRtpMediaType.RTCRtpMediaTypeAudio,
          init: RTCRtpTransceiverInit(
            direction: TransceiverDirection.SendRecv,
            streams: [_talkbackStream!],
          ),
        );

        // A press can beat the connection: the gate may be holding a microphone that had nowhere
        // to go when it landed.
        final microphone = _talkbackMicrophone.microphone;
        if (microphone != null) _routeMicrophone(microphone, sending: _talking);
      } else {
        await connection.addTransceiver(
          kind: RTCRtpMediaType.RTCRtpMediaTypeAudio,
          init: RTCRtpTransceiverInit(direction: TransceiverDirection.RecvOnly),
        );
      }

      final offer = await connection.createOffer();
      await connection.setLocalDescription(offer);

      // go2rtc's signalling is a single request/response, so the offer has to be complete when
      // it is sent — there is nowhere to trickle a late candidate to.
      final local = await _gatheredLocalDescription(connection);
      if (_disposed) return;

      final answer = await _repository.openWebRtc(_camera.id, local);
      if (_disposed) return;

      await connection.setRemoteDescription(
        RTCSessionDescription(answer, 'answer'),
      );
    } on Object catch (error) {
      if (_disposed) return;
      failure = error.toString();
      stage.value = WebRtcStage.failed;
    }
  }

  /// Points the microphone at the camera, and gates whether it is speaking.
  ///
  /// The handover is once: `replaceTrack` gives the sender its source without changing a line of
  /// the SDP, so from then on a press is only `enabled`. Note that a disabled track still
  /// *transmits* — silence rather than nothing — which is why the mic is closed rather than
  /// merely disabled when the session ends.
  void _routeMicrophone(MediaStream microphone, {required bool sending}) {
    final tracks = microphone.getAudioTracks();
    for (final track in tracks) {
      track.enabled = sending;
    }

    final sender = _talkback?.sender;
    if (sender == null || _attached || tracks.isEmpty) return;
    _attached = true;
    unawaited(sender.replaceTrack(tracks.first));
  }

  /// Detached and then stopped, in that order and both.
  ///
  /// Neither platform reports whether the detach landed — the web wrapper drops the underlying
  /// promise and the desktop plugin discards the result — so `stop` is the release that can
  /// actually be relied on to put the operating system's indicator out.
  Future<void> _closeMicrophone(MediaStream microphone) async {
    _attached = false;
    try {
      await _talkback?.sender.replaceTrack(null);
    } on Object {
      // A connection already torn down has no sender left to detach from, which is not a failure
      // to release anything: the stop below is what frees the device.
    }

    for (final track in microphone.getTracks()) {
      await track.stop();
    }
    await microphone.dispose();
  }

  /// Waits for ICE gathering to finish, then returns the SDP with every candidate in it.
  ///
  /// Capped rather than open-ended: a host that never reports `complete` — which happens — would
  /// otherwise hang the view forever, and the candidates gathered by then are usually the
  /// host-local ones this LAN deployment actually uses.
  Future<String> _gatheredLocalDescription(RTCPeerConnection connection) async {
    if (connection.iceGatheringState !=
        RTCIceGatheringState.RTCIceGatheringStateComplete) {
      final gathered = Completer<void>();

      connection.onIceGatheringState = (state) {
        if (state == RTCIceGatheringState.RTCIceGatheringStateComplete &&
            !gathered.isCompleted) {
          gathered.complete();
        }
      };

      await gathered.future.timeout(
        const Duration(seconds: 3),
        onTimeout: () {},
      );
    }

    final description = await connection.getLocalDescription();
    return description?.sdp ?? '';
  }

  /// Opens the microphone if this is the first press of the session, and gates it after that.
  void setTalking(bool talking) {
    _talking = talking;
    _talkbackMicrophone.setTalking(talking);
  }

  /// Whether the camera's own audio plays at all, as distinct from how loudly.
  void setTheirAudio(bool theirAudio) {
    _theirAudio = theirAudio;
    _remoteAudio?.enabled = theirAudio;
  }

  /// The listening level, this camera's gain, and the level its gate treats as silence.
  void setLevel({
    required double volume,
    required double gainDb,
    double? gateRms,
  }) {
    _volume = volume;
    _gainDb = gainDb;
    _gateRms = gateRms;
    _applyVolume();
  }

  /// Applied to the renderer rather than the track, because that is the call that reaches an
  /// actual gain on both platforms: on web it sets the backing audio element's `volume`, and on
  /// native it forwards to the track's own volume.
  ///
  /// Called after the renderer initialises, again once a stream is attached, and on every change
  /// — not just the last. The native implementation throws when it has no source object yet and
  /// swallows it, so a level set before the track arrives is a silent no-op.
  ///
  /// Above unity the two platforms part company, and they have to. On native, the renderer forwards
  /// to libwebrtc's `RTCAudioTrack::SetVolume`, which really does run 0..10 — so the lift is just a
  /// bigger number, and [maxBoostDb] is exactly where that range runs out. On web the same call
  /// clamps itself to 1.0 against an audio element the app cannot reach, so the lift has to come from
  /// routing that element through WebAudio instead. See [_ensureGainChain].
  void _applyVolume() {
    final db = _gainDb;

    if (db <= 0) {
      _releaseGainChain();
      unawaited(renderer.setVolume(webRtcVolume(_volume)));
      return;
    }

    if (!kIsWeb) {
      unawaited(renderer.setVolume(nativeWebRtcBoostedVolume(_volume, db)));
      return;
    }

    _ensureGainChain();
    final gain = _gain;
    if (gain == null) {
      // No graph to be had. Unity is the most this path can deliver, and it is what the camera
      // sounded like before boost existed — quiet, but present.
      unawaited(renderer.setVolume(webRtcVolume(_volume)));
      return;
    }

    // Wide open into the graph: the element's `volume` is applied *before* the source node, so
    // anything less than 1 here would attenuate the signal the gain is about to lift and the level
    // would be applied twice.
    unawaited(renderer.setVolume(1));
    gain.setGate(_gateRms);
    gain.setLevel(volume: _volume, db: db);
  }

  /// The `<audio>` element `flutter_webrtc` plays this renderer's audio through.
  ///
  /// Found by id rather than held, because the package creates it privately and appends it to a div
  /// of its own — the id is the only handle it exposes. Deriving it here couples this to the
  /// package's naming, which is the price of boost on the web path; if the package renames it, the
  /// element is simply not found and the view falls back to unity rather than going silent.
  String? get _liveAudioElementId {
    final int? textureId = renderer.textureId;
    return textureId == null ? null : 'audio_RTCVideoRenderer-$textureId';
  }

  void _ensureGainChain() {
    if (_gain != null || _gainUnavailable) return;

    final id = _liveAudioElementId;
    if (id == null) return;

    final chain = attachPlaybackGain(id);
    if (chain == null) {
      _gainUnavailable = true;
      return;
    }

    _gain = chain;
    // The stream is live and arrived on a click, so this is a legal moment to start the context.
    resumePlaybackAudio();
  }

  void _releaseGainChain() {
    _gain?.dispose();
    _gain = null;
    // Deliberately not clearing [_gainUnavailable]: whether this browser has WebAudio, and whether
    // this element is already claimed, do not change because a camera's gain went back to zero.
  }

  Future<void> dispose() async {
    _disposed = true;
    _settling?.cancel();
    _settling = null;
    _remoteAudio = null;
    // Before the source object goes: the chain is wired to the element that plays it, and a new
    // session gets a new renderer with a new element rather than reusing this one.
    _releaseGainChain();
    renderer.srcObject = null;

    // Before the connection goes, so the detach has a sender to reach.
    await _talkbackMicrophone.dispose();
    _talkback = null;

    final talkbackStream = _talkbackStream;
    _talkbackStream = null;
    await talkbackStream?.dispose();

    final connection = _connection;
    _connection = null;
    await connection?.close();

    await renderer.dispose();
    stage.dispose();
  }
}
