import 'package:flutter/widgets.dart';

import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';

/// How a camera is doing, as far as anything can tell.
///
/// The Server has no `Camera.Status`, no health endpoint and no event for ingest failure — a
/// camera whose source is unreachable is retried forever and says so only in the log. So this
/// is derived from the one signal that does cross the wire: whether the wall socket is still
/// delivering that camera's frames.
enum CameraHealth {
  /// Frames are arriving.
  healthy('Healthy', 'Recording'),

  /// Registered, enabled, and its frames have stopped.
  unreachable('Can’t reach it', 'Can’t reach it'),

  /// Nothing is wrong — it is switched off on purpose.
  disabled('Turned off', 'Turned off'),

  /// Enabled, and no frame has arrived yet. A cold start, not a fault.
  connecting('Starting up', 'Starting up');

  const CameraHealth(this.label, this.listLabel);

  /// What the header pill says.
  final String label;

  /// What the list row says, where it shares a line with the retention summary.
  final String listLabel;

  Color get color => switch (this) {
    CameraHealth.healthy => Serval.healthy,
    CameraHealth.unreachable => Serval.recording,
    CameraHealth.disabled => Nocturne.mix(Nocturne.text, 28),
    CameraHealth.connecting => Serval.alert,
  };

  Color get textColor => switch (this) {
    CameraHealth.healthy => Serval.healthyText,
    CameraHealth.unreachable => Serval.recordingText,
    CameraHealth.disabled => Nocturne.mix(Nocturne.text, 50),
    CameraHealth.connecting => Serval.alertText,
  };
}

/// The 7px dot at the right of a camera row.
class StatusDot extends StatelessWidget {
  const StatusDot({super.key, required this.health, this.size = 7});

  final CameraHealth health;
  final double size;

  @override
  Widget build(BuildContext context) => SizedBox(
    width: size,
    height: size,
    child: DecoratedBox(
      decoration: BoxDecoration(color: health.color, shape: BoxShape.circle),
    ),
  );
}

/// The header's health pill: a dot, a word, and a tint of the same hue.
class HealthPill extends StatelessWidget {
  const HealthPill({super.key, required this.health});

  final CameraHealth health;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
    decoration: BoxDecoration(
      color: Nocturne.mix(health.color, 14),
      borderRadius: BorderRadius.circular(20),
      border: Border.all(color: Nocturne.mix(health.color, 35)),
    ),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        StatusDot(health: health, size: 5),
        const SizedBox(width: 5),
        Text(
          health.label,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 11.5,
            fontWeight: Nocturne.headingWeight,
            color: health.textColor,
          ),
        ),
      ],
    ),
  );
}

/// The mono `id 1` chip beside a camera's name.
///
/// The id is worth showing because it is not decoration: it is the media directory name, the URL
/// segment telemetry is POSTed to, and the one field that cannot be changed after creation.
class IdChip extends StatelessWidget {
  const IdChip({super.key, required this.id});

  final String id;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(5),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
    ),
    child: Text(
      'id $id',
      style: TextStyle(
        fontFamily: Nocturne.fontMono,
        fontSize: 11,
        color: Nocturne.mix(Nocturne.text, 40),
      ),
    ),
  );
}
