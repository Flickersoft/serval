/*
 * A noise gate for camera audio, as an AudioWorklet.
 *
 * Why this file exists at all: WebAudio has no gate. `DynamicsCompressorNode` only works downward
 * above a threshold and cannot expand below one, so there is no built-in node that makes silence
 * silent. On the desktop, ffmpeg's `agate` does this job inside libmpv's filter chain for free — this
 * is the browser's half of the same setting. See lib/playback/playback_volume.dart, which owns the
 * constants both halves are configured from.
 *
 * Why a gate is needed: a camera's audio spends nearly all its time on its noise floor, 40 dB or more
 * below the content worth hearing. The gain that makes the content audible also lifts the AAC
 * quantisation noise into audible hiss. Gating ahead of the gain keeps silence silent. A compressor
 * would be the wrong instrument rather than a gentler one — an AGC applies its greatest gain exactly
 * when there is nothing but noise to find.
 *
 * Vendored rather than fetched, for the same reason hls.js and the fonts are: an offline run should
 * still work. Loaded with `audioWorklet.addModule()` rather than a <script> tag — a worklet runs in
 * its own global scope on the audio thread, where `AudioWorkletProcessor` exists and the DOM does not.
 */

/*
 * How far *above* the threshold the level must rise to re-open a shut gate, as a multiplier.
 *
 * About +3 dB of hysteresis. Without some, a level sitting near the threshold crosses it repeatedly
 * and the gate stutters on every crossing — the artifact that would make this feature unpleasant
 * rather than merely imperfect. The release time smooths most of that; this covers the rest.
 *
 * Above rather than below, and that direction is load-bearing. The setting means "treat quieter than
 * this as silence", so everything under the threshold has to be gated — including a noise floor
 * sitting just under it, which is exactly where an operator setting this against the level meter will
 * put it. Hysteresis on the closing side instead would leave a band under the threshold in which the
 * gate silently kept whatever state it started in, and since it starts open, the common case would be
 * a gate that never closed at all.
 *
 * This is also what ffmpeg's `agate` does — it attenuates below the threshold — so the two
 * implementations of this one setting agree.
 */
const OPEN_RATIO = 1.4;

class PlaybackGateProcessor extends AudioWorkletProcessor {
  static get parameterDescriptors() {
    return [
      // The RMS of a 16 kHz window, matching the unit the setting is stored in and the meter it is
      // set against. 0 disables the gate rather than holding it shut, which is the reading that
      // matches "no threshold set".
      { name: 'threshold', defaultValue: 0, minValue: 0, maxValue: 1, automationRate: 'k-rate' },
      // What the gate attenuates to when shut. Not zero: see `gateFloor` in playback_volume.dart.
      { name: 'floor', defaultValue: 0.001, minValue: 0, maxValue: 1, automationRate: 'k-rate' },
      { name: 'attackMs', defaultValue: 5, minValue: 0.1, maxValue: 1000, automationRate: 'k-rate' },
      { name: 'releaseMs', defaultValue: 150, minValue: 1, maxValue: 5000, automationRate: 'k-rate' },
    ];
  }

  constructor() {
    super();
    // Starts open. A gate that starts shut would swallow the first moments of playback while it
    // worked out that there was something there, and those are the moments someone pressed play for.
    this._gain = 1;
    this._open = true;
  }

  process(inputs, outputs, parameters) {
    const input = inputs[0];
    const output = outputs[0];

    // No input yet — the source has not connected, or the stream has nothing in it. Returning true
    // keeps this node alive waiting for one; returning false would remove it from the graph and take
    // the audio with it, permanently.
    if (!input || input.length === 0) return true;

    const threshold = parameters.threshold[0];
    const floor = parameters.floor[0];

    // Disabled: copy through untouched rather than pretending to gate at zero. This is the state a
    // camera with no threshold set is in, and it must be bit-transparent.
    if (!(threshold > 0)) {
      for (let c = 0; c < input.length; c++) {
        if (output[c] && input[c]) output[c].set(input[c]);
      }
      this._gain = 1;
      this._open = true;
      return true;
    }

    const frames = input[0].length;

    // One RMS for the whole render quantum across all channels. At 128 frames this is under 3 ms of
    // audio, which is finer than the attack time — measuring per sample would cost more and change
    // nothing audible.
    let sum = 0;
    let count = 0;
    for (let c = 0; c < input.length; c++) {
      const channel = input[c];
      for (let i = 0; i < frames; i++) {
        sum += channel[i] * channel[i];
      }
      count += frames;
    }
    const rms = count > 0 ? Math.sqrt(sum / count) : 0;

    // Hysteresis, not a single comparison: anything below the threshold shuts the gate, and getting
    // back open takes a level some way above it.
    if (this._open) {
      if (rms < threshold) this._open = false;
    } else if (rms >= threshold * OPEN_RATIO) {
      this._open = true;
    }

    const target = this._open ? 1 : floor;

    // One-pole smoothing toward the target, per sample, so the gain never steps. A step in gain is a
    // click, and a gate that clicks every time it moves is worse than the hiss it removes.
    const timeMs = target > this._gain
      ? parameters.attackMs[0]
      : parameters.releaseMs[0];
    const coeff = Math.exp(-1 / Math.max(1, (timeMs / 1000) * sampleRate));

    let gain = this._gain;
    for (let i = 0; i < frames; i++) {
      gain = target + (gain - target) * coeff;
      for (let c = 0; c < input.length; c++) {
        const source = input[c];
        const sink = output[c];
        if (source && sink) sink[i] = source[i] * gain;
      }
    }
    this._gain = gain;

    return true;
  }
}

registerProcessor('playback-gate', PlaybackGateProcessor);
