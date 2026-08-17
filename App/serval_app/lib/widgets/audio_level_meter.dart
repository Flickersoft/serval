import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';

import '../data/audio_levels_socket.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';

/// What a camera's microphone is actually hearing, with the threshold drawn on it.
///
/// The instrument the thresholds beside it are set against. Without it they are set blind, which
/// is how a gate came to sit above a room's speech and discard ten of every eleven utterances with
/// nothing anywhere saying so — the camera was enabled, the model was loaded, and sound events
/// were still arriving.
///
/// Log-scaled, and labelled in dBFS, for the same reason [ZoomControl](ptz_pad.dart) uses a log
/// track: the useful range spans two decades and a linear rail would bunch every value that
/// matters into its leftmost few percent. -52 dBFS is also a number a person can reason about in a
/// way 0.0025 is not.
///
/// Not a Material progress bar: this system's meters are a rail, a fill and a hairline.
class AudioLevelMeter extends StatelessWidget {
  const AudioLevelMeter({
    super.key,
    required this.level,
    required this.threshold,
  });

  /// The live reading, or null when there is no Server to open a feed against — or when the feed
  /// has dropped. Both render the same "no level" state, because a bar frozen on its last value
  /// is indistinguishable from a silent room, which is the exact confusion this removes.
  final ValueListenable<AudioLevel?>? level;

  /// The threshold in force, as the form currently has it. Taken from the form rather than from
  /// the reading so the line tracks the slider while it is being dragged, before any save.
  final double threshold;

  /// The rail's ends. 0.0002 is below the noise floor of a 16-bit capture and 0.05 is well above
  /// speech at conversational distance, so every value worth setting is on the track.
  static const _minRms = 0.0002;
  static const _maxRms = 0.05;

  static double _fraction(double rms) {
    if (rms <= _minRms) return 0;
    final logMin = math.log(_minRms);
    final logMax = math.log(_maxRms);
    return ((math.log(rms) - logMin) / (logMax - logMin)).clamp(0.0, 1.0);
  }

  static String _dbfs(double rms) {
    if (rms <= 0) return '−∞ dB';
    return '${(20 * math.log(rms) / math.ln10).round()} dB';
  }

  @override
  Widget build(BuildContext context) {
    final listenable = level;

    if (listenable == null) {
      return _Rail(threshold: threshold, reading: null, unavailable: true);
    }

    // Wraps the rail alone. Readings arrive about ten times a second, and a rebuild any higher
    // would relayout the whole settings form at that rate.
    return ValueListenableBuilder<AudioLevel?>(
      valueListenable: listenable,
      builder: (context, reading, _) => _Rail(
        threshold: threshold,
        reading: reading,
        unavailable: reading == null,
      ),
    );
  }
}

class _Rail extends StatelessWidget {
  const _Rail({
    required this.threshold,
    required this.reading,
    required this.unavailable,
  });

  final double threshold;
  final AudioLevel? reading;
  final bool unavailable;

  @override
  Widget build(BuildContext context) {
    final rms = reading?.rms ?? 0;
    final peak = reading?.peak ?? 0;
    final open = reading?.speechGateOpen ?? false;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            // Flexible, so a narrow settings column takes it out of the label rather than
            // overflowing the row — the reading to its right is the part worth keeping whole.
            Flexible(
              child: Text(
                'What this camera hears',
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12,
                  color: Nocturne.mix(Nocturne.text, 70),
                ),
              ),
            ),
            const Spacer(),
            Text(
              unavailable
                  ? 'no live level'
                  : '${AudioLevelMeter._dbfs(peak)} peak',
              style: TextStyle(
                fontFamily: Nocturne.fontMono,
                fontSize: 11,
                // The pill goes quiet rather than green/red: the question is whether the bar
                // crosses the line, and colour-coding the number would answer it twice.
                color: unavailable
                    ? Nocturne.mix(Nocturne.text, 35)
                    : (open
                          ? Serval.healthyText
                          : Nocturne.mix(Nocturne.text, 55)),
              ),
            ),
          ],
        ),
        const SizedBox(height: 6),
        LayoutBuilder(
          builder: (context, constraints) {
            final width = constraints.maxWidth;
            final thresholdX = AudioLevelMeter._fraction(threshold) * width;

            return SizedBox(
              height: 18,
              child: Stack(
                alignment: Alignment.centerLeft,
                clipBehavior: Clip.none,
                children: [
                  Container(
                    height: 8,
                    decoration: BoxDecoration(
                      color: Nocturne.mix(Nocturne.text, 8),
                      borderRadius: BorderRadius.circular(4),
                    ),
                  ),
                  if (!unavailable)
                    FractionallySizedBox(
                      widthFactor: AudioLevelMeter._fraction(rms),
                      child: Container(
                        height: 8,
                        decoration: BoxDecoration(
                          color: open
                              ? Serval.healthy
                              : Nocturne.mix(Nocturne.text, 30),
                          borderRadius: BorderRadius.circular(4),
                        ),
                      ),
                    ),
                  // Peak-hold, as a hairline. This is the mark that answers the question: the
                  // mean is what the room sounds like, the peak is what has to clear the line.
                  if (!unavailable && peak > 0)
                    Positioned(
                      left: (AudioLevelMeter._fraction(peak) * width - 1).clamp(
                        0.0,
                        math.max(0.0, width - 2),
                      ),
                      child: Container(
                        width: 2,
                        height: 12,
                        color: Nocturne.accent300,
                      ),
                    ),
                  // The threshold, drawn over everything: the whole point is reading the bar
                  // against it.
                  Positioned(
                    left: (thresholdX - 1).clamp(0.0, math.max(0.0, width - 2)),
                    child: Container(width: 2, height: 18, color: Serval.alert),
                  ),
                ],
              ),
            );
          },
        ),
        const SizedBox(height: 4),
        Text(
          unavailable
              ? 'Connect to the Server to see this camera’s level.'
              : 'Anything left of the amber line is ignored.',
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 11,
            color: Nocturne.mix(Nocturne.text, 45),
          ),
        ),
      ],
    );
  }
}
