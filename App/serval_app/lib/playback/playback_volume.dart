/// The one place a volume slider's position is turned into what each backend wants.
///
/// One control, and [unityTravel] is the whole idea: full volume with nothing added sits three
/// quarters along, so the track has room above it. Below that the slider attenuates; above it the
/// audio is amplified. A listener asking "why can't I hear this" has one place to answer it, and
/// "100%" is a labelled point on the track rather than the end of it.
///
/// The control is one axis. The *pipeline* is still two, and [playbackFromTravel] is the seam: it
/// splits a position into the volume the player takes and the dB something behind the player has to
/// add. That split is not an implementation detail to be tidied away. Attenuation is free and
/// exact — every backend multiplies by it natively — while amplification needs a gate in front of it
/// and a limiter behind it, and the limiter's threshold only means anything if it knows how much
/// gain preceded it. Keeping them separate below the seam is what lets the top of the track be loud
/// instead of clipped.
///
/// Three players carry audio here and no two agree on a range: libmpv is 0..100, a `<video>` element
/// is 0..1, and WebRTC's track volume runs 0..10 with 1.0 as unity gain. Left at the call sites that
/// would be three magic multipliers in three files, and the failure mode of getting one wrong is not
/// a crash — it is a slider that reaches full volume a tenth of the way along, which reads as a
/// broken control rather than a bug.
///
/// Amplification is worse than that, because the backends do not merely disagree on units — they
/// disagree on what is *possible*. A `<video>` element's `volume` is clamped to 1.0 by the HTML spec
/// and libwebrtc's is clamped to 10, so above-unity gain is reachable on some paths only by leaving
/// the player's own volume alone and routing the audio through something else. See [maxBoostDb].
library;

import 'dart:math' as math;

/// The app's canonical volume: silent at 0, unattenuated at 1.
typedef PlaybackVolume = double;

/// Clamps to the range every mapping below assumes.
double normalizeVolume(double volume) => volume.clamp(0.0, 1.0);

/// Where unity lands on the control: full volume, nothing added, three quarters along.
///
/// Three quarters rather than half because attenuating is the common case and amplifying is the
/// exception — most cameras are listenable and a few need lifting, so the exception gets the smaller
/// share of the track. It also puts this control where VLC and mpv put theirs, which reach unity at
/// roughly 80% and 77% of their travel respectively.
const double unityTravel = 0.75;

/// libmpv, via media_kit's `Player.setVolume`. 0..100, and 100 is unity.
///
/// Deliberately not the place amplification is applied, even though libmpv accepts above 100 as
/// software amplification. mpv caps that at `volume-max`, and its software amp clips rather than
/// limiting. The `af` chain from [mpvAudioFilter] does the job properly instead, which leaves this
/// function only ever describing the attenuating half of the track.
double mpvVolume(double volume) => normalizeVolume(volume) * 100;

/// An `HTMLVideoElement`'s `volume`. Already the app's own range, and spec-clamped to it.
///
/// Amplification cannot go through here at all. Above unity the web player pins this to 1 and puts
/// the whole level on a WebAudio `GainNode` instead — see `playback_audio_graph.dart`.
double htmlVideoVolume(double volume) => normalizeVolume(volume);

/// A WebRTC audio track at unity or below, via `RTCVideoRenderer.setVolume`.
///
/// Clamped to 0..1 rather than scaled to libwebrtc's 0..10, so the same number means the same
/// loudness on both platforms. See [nativeWebRtcBoostedVolume] for the native path above unity, and
/// note the web path has no equivalent — its backing audio element clamps itself, so the web live
/// view silences the renderer and routes the stream through WebAudio instead.
double webRtcVolume(double volume) => normalizeVolume(volume);

/// The most the control can add, in dB. 10x.
///
/// Sized from the typical moment rather than the loud one. Measured across a real deployment, a
/// camera's *median* four-second segment peaks around -42 dBFS and the quietest camera's around
/// -67 dBFS, while the rare transient reaches full scale — so the content worth hearing sits well
/// below the ceiling even on cameras whose microphones are set correctly.
///
/// Also the point where every playback path agrees. libwebrtc's `RTCAudioTrack::SetVolume` stops at
/// 10x with no filter insertion point and no WebAudio equivalent behind it, so the native live view
/// cannot exceed this however it is asked; landing the control's own ceiling here means the number
/// on the slider is deliverable everywhere rather than on three paths out of four. A control that
/// silently delivers half the gain it promises is indistinguishable from the gain not working.
///
/// Usable only because of the gate. An unguarded 10x lifts the codec's own quantisation noise into
/// audible hiss, and these streams are on their noise floor most of the time.
const double maxBoostDb = 20;

/// The gain a dB lift asks for, as a multiplier. 1.0 at 0 dB.
double boostFactor(double db) =>
    math.pow(10, db.clamp(0, maxBoostDb) / 20).toDouble();

/// The stops the camera's starting volume offers, as positions on the control — the same 0..100 the
/// pill shows, so the two places a volume is set are quoting the same number.
///
/// Round in both units, which is why they are fives: [unityTravel] is 75, and each five points above
/// it is exactly 4 dB.
///
/// Stepped rather than continuous because this one is set once, from a settings page, against a
/// meter — and above unity a step of five points is a change anyone can hear, while the difference
/// between 86 and 87 is not something to aim at.
const List<double> startingVolumeStops = [75, 80, 85, 90, 95, 100];

/// Splits a slider position into the volume a player takes and the dB something behind it must add.
///
/// Below unity the volume is the *square* of the position. `volume` is a raw amplitude multiplier on
/// every backend, and amplitude is not what loudness follows: fed the position directly, the bottom
/// two thirds of a track do almost nothing audible and the last third does all of it, which reads as
/// a control that is broken until it suddenly is not. Squaring is the cheap approximation of the
/// curve people expect, and it is exact at both ends — silent at 0, unattenuated at unity.
///
/// Above unity the volume is pinned and the position becomes dB, linearly. Linear in dB rather than
/// in the percentage the control displays, because dB is the scale that sounds evenly spaced: half
/// way up the amplifying quarter is +10 dB, which the readout calls 316% rather than 550%. The knob
/// moving evenly is worth more than the number doing so.
({double volume, double db}) playbackFromTravel(double travel) {
  final t = travel.clamp(0.0, 1.0);

  if (t <= unityTravel) {
    final position = t / unityTravel;
    return (volume: position * position, db: 0);
  }

  final above = (t - unityTravel) / (1 - unityTravel);
  return (volume: 1, db: above * maxBoostDb);
}

/// Where a stored volume and dB put the knob. The inverse of [playbackFromTravel].
///
/// Needed because the pair is what gets persisted and what a camera's starting volume is expressed
/// in, while the knob is a single position: without this the control could set a level but not open
/// showing one.
///
/// Any gain at all wins over the volume, matching the split [playbackFromTravel] produces — above
/// unity it always reports `volume: 1`, so a pair carrying both a gain and an attenuation is not
/// something this mapping can emit, and the gain is the half that carries the intent.
double travelFor({required double volume, required double db}) {
  final gain = db.clamp(0.0, maxBoostDb);
  if (gain > 0) {
    return unityTravel + (gain / maxBoostDb) * (1 - unityTravel);
  }
  return math.sqrt(normalizeVolume(volume)) * unityTravel;
}

/// A slider position as the control shows it: the position itself, 0 to 100.
///
/// The knob's place on its own track, and nothing else. Not the amplitude — the squaring in
/// [playbackFromTravel] is a feel correction, and surfacing it would have the middle of the track
/// read "44%" and look mislabelled. And explicitly **not** the multiplier above unity: 10x is 1000%,
/// and a volume control that reads four digits is a control asking to be understood rather than used.
/// Amplifying is a thing the track shows by changing colour, not a number to be reasoned about.
///
/// So full volume with nothing added reads 75%, which is what [unityTravel] means, and the mark on
/// the track is what says the rest.
String volumeLabel(double travel) =>
    '${(travel.clamp(0.0, 1.0) * 100).round()}%';

/// How the gate follows the signal. Shared by both implementations of it — ffmpeg's `agate` on the
/// desktop and the `AudioWorklet` in `web/playback_gate.js` — so the same camera sounds the same on
/// both, and so a change here cannot reach one and miss the other.
///
/// The release is the number that matters. A gate that shuts the instant the level drops chops the
/// tail off every word and chatters between them, which is far more objectionable on speech than the
/// hiss it was brought in to remove.
const double gateAttackMs = 5;
const double gateReleaseMs = 150;

/// How far the gate attenuates when shut, as a multiplier — not to true silence.
///
/// -60 dB, which lands below audibility once the gain has been applied. Full mute would be no
/// quieter to any listener and would make the gate's opening and closing an audible event in itself.
const double gateFloor = 0.001;

/// The libmpv `af` chain for a lift, or the empty string for no filter.
///
/// Empty at 0 dB, which is the whole attenuating half of the track: nothing in the chain, and
/// `mpvVolume` alone carrying the level.
///
/// The gate is included only when there is both a lift and a threshold. Gating without amplification
/// would take quiet content away in exchange for nothing: the hiss it exists to suppress is only
/// audible once something has amplified it.
///
/// `detection=rms` is stated rather than left to ffmpeg's default so the stored threshold keeps
/// meaning the same thing across ffmpeg versions — it is an RMS, matching the meter it is set
/// against.
String mpvAudioFilter(double db, double? gateRms) {
  final gain = db.clamp(0.0, maxBoostDb);
  if (gain <= 0) return '';

  final filters = <String>[
    if (gateRms case final threshold? when threshold > 0)
      'agate=threshold=${_ffmpegDecimal(threshold)}'
          ':detection=rms'
          ':ratio=9'
          ':range=${_ffmpegDecimal(gateFloor)}'
          ':attack=$gateAttackMs'
          ':release=$gateReleaseMs',
    'volume=${gain}dB',
    // The limiter, and it is load-bearing rather than a safety net. Every camera reaches within a
    // few dB of full scale occasionally — a door slamming, a dog — and the top of the track puts
    // that transient past the ceiling. Without this, amplification trades inaudible content for a
    // burst of clipping.
    //
    // `level=disabled` is not a preference. ffmpeg's `alimiter` auto-levels its output by default,
    // which normalises the result back up to full scale and cancels the limiting exactly: measured
    // against a signal driven 10 dB past the ceiling, the default lets it through at 0.0 dBFS while
    // this holds it at -3.1. It would also be a second, uncontrolled gain stage that the WebAudio
    // chain has no equivalent of, so the two platforms would disagree on the level of everything.
    'alimiter=limit=0.7:level=disabled',
  ];

  return filters.join(',');
}

/// A small positive double as a plain decimal ffmpeg will parse.
///
/// `toString` would give `6e-7` for a small enough threshold, which ffmpeg reads as a filter-syntax
/// error rather than a number. Eight places covers the whole settable range — the meter these are
/// set against starts at 0.0002 — without ever reaching exponent notation.
String _ffmpegDecimal(double value) => value.toStringAsFixed(8);

/// A WebRTC audio track above unity on the native path, via `RTCVideoRenderer.setVolume`.
///
/// libwebrtc's range is 0..10, so this is the one mapping in this file that deliberately scales past
/// 1. Native only: the web renderer sets a backing audio element's `volume` and clamps itself, so
/// the same call there would silently deliver 1.0 however much was asked for.
///
/// [maxBoostDb] is exactly this path's ceiling, so the outer clamp is arithmetic belt-and-braces
/// rather than the thing doing the capping. No limiter is available behind this, which is the other
/// half of why the control's ceiling is where it is — 10x with nothing to catch a transient is
/// already as far as this path should be pushed.
double nativeWebRtcBoostedVolume(double volume, double db) {
  final gain = db.clamp(0.0, maxBoostDb);
  return (normalizeVolume(volume) * boostFactor(gain)).clamp(0.0, 10.0);
}
