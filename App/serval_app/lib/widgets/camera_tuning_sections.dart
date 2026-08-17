/// The per-camera overrides for what a camera looks for, which sounds it reports, and how much has
/// to change before it says anything.
///
/// Split out of [CameraSettingsForm](camera_settings_form.dart), which is long enough already.
/// Every one of these is a card in the shape the Server settings page uses — see
/// [SettingCard](settings_cards.dart) — because a camera setting and a Server setting are the same
/// kind of thing and should not be drawn differently.
///
/// Three rules that outlive any particular field:
///
/// **Null is not zero.** Every field means "use the Server's default" when unset. A card that
/// overrides nothing shows the Server's own value with the *using the default* chip beside it, so
/// the number on screen is always the number in force.
///
/// **An empty bag is no bag.** The Server collapses an all-null override object to nothing on save,
/// so these do the same on the way out — otherwise the form reports an unsaved change that would
/// not survive the trip.
///
/// **Kept visible when the capability is off.** A stored threshold nobody can see is worse than an
/// inert one on screen.
library;

import 'package:flutter/widgets.dart';

import '../data/camera_record.dart';
import '../data/json_coerce.dart';
import '../models/server_camera_defaults.dart';
import '../models/server_settings.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'label_chips.dart';
import 'paired_rows.dart';
import 'settings_cards.dart';

/// One camera-overridable field as a card.
///
/// The whole of the camera-versus-Server story lives here. A field this camera sets is
/// [SettingSource.user] and reads *changed here*; one it leaves alone is [SettingSource.builtIn],
/// reads *using the default*, and shows the Server's own value — because that is what the camera is
/// actually running on. Identical vocabulary to the Server page, for the same three states.
class CameraSettingCard extends StatelessWidget {
  const CameraSettingCard({
    super.key,
    required this.field,
    required this.defaults,
    required this.value,
    required this.onChanged,
    this.compact = false,
  });

  final CameraSetting field;

  /// The catalogue behind this field: its label, its sentence, its bounds, and what the Server
  /// holds.
  final ServerCameraDefaults defaults;

  /// This camera's override, or null where it falls through to the Server.
  final Object? value;

  /// A new override, or null to stop overriding.
  final ValueChanged<Object?> onChanged;

  final bool compact;

  ServerSetting get _descriptor => defaults[field];

  bool get _overridden => value != null;

  /// What this camera is actually running on: its own value, or the Server's behind it.
  Object? get _effective => value ?? defaults.valueOf(field);

  @override
  Widget build(BuildContext context) => SettingCard(
    label: _descriptor.label,
    source: _overridden ? SettingSource.user : SettingSource.builtIn,
    headerTrailing: _headerTrailing,
    control: _control,
    help: Text(_descriptor.help, style: settingHelpStyle()),
    resetLabel: _overridden ? _resetLabel : null,
    onReset: _overridden ? () => onChanged(null) : null,
    compact: compact,
  );

  /// *Use the default*, naming the value it would restore. For a camera "the default" is the
  /// Server's value, which is the thing worth naming — a reset with the before-and-after on it is a
  /// decision rather than a guess.
  String get _resetLabel {
    final fallback = defaults.valueOf(field);
    if (fallback == null) return 'Use the default';
    if (fallback case final num number) {
      return 'Use the default · ${settingFigure(number)}';
    }
    return 'Use the default';
  }

  Widget? get _headerTrailing {
    if (_isList) {
      return _overridden
          ? SettingsLinkText(
              'Use the default list',
              onTap: () => onChanged(null),
            )
          : null;
    }
    if (settingSlidable(_descriptor.min, _descriptor.max)) {
      return Text(
        settingReadout(_effective, _descriptor.unit),
        style: monoStyle(fontSize: 12.5, color: Nocturne.text),
      );
    }
    return null;
  }

  bool get _isList => _descriptor.kind == SettingKind.textList;

  /// The bounds, at the end of a number's row. First thing to give up a narrow card: the Server
  /// refuses a value outside them and says so, which the box cannot.
  Widget? get _rowTrailing {
    if (compact) return null;
    if (_descriptor.rangeShort case final range?) {
      return Text(
        range,
        style: monoStyle(
          fontSize: 10.5,
          color: Nocturne.mix(Nocturne.text, 35),
        ),
      );
    }
    return null;
  }

  Widget get _control {
    if (_isList) {
      // A list is the one control that must not show the Server's value as its own. Real chips
      // read as "this camera names these", so a camera following the Server draws no chips and
      // gets the Server's list behind them as ghosts — and typing the first label then *replaces*
      // that list rather than appending to it, which is what the Server does with an override.
      final override = switch (value) {
        final List<dynamic> items => [for (final item in items) '$item'],
        _ => null,
      };

      return LabelChipList(
        value: override ?? const [],
        fallback: defaults.listFor(field),
        // Two empty states, not one. Null is "follow the Server" and shows its list; an empty
        // override is "record none of it", a real instruction, and drawing the Server's labels
        // behind that would say the opposite of what is stored.
        showFallback: override == null,
        onChanged: onChanged,
      );
    }

    return SettingNumberControl(
      value: _effective,
      whole: _descriptor.kind == SettingKind.integer,
      min: _descriptor.min,
      max: _descriptor.max,
      unit: _descriptor.unit,
      trailing: _rowTrailing,
      onChanged: onChanged,
    );
  }
}

/// What this camera looks for, and what it treats as news — everything but the masks, which have
/// their own section and their own editor.
List<PairedItem> cameraDetectionCards({
  required DetectionTuningSettings? tuning,
  required ServerCameraDefaults defaults,
  required ValueChanged<DetectionTuningSettings?> onChanged,
  required bool compact,
}) {
  final current = tuning ?? const DetectionTuningSettings();
  void emit(DetectionTuningSettings updated) =>
      onChanged(updated.isEmpty ? null : updated);

  Widget card(CameraSetting field, Object? value, ValueChanged<Object?> set) =>
      CameraSettingCard(
        field: field,
        defaults: defaults,
        value: value,
        onChanged: set,
        compact: compact,
      );

  return [
    PairedItem(
      card(
        CameraSetting.detectionClasses,
        current.classes,
        (v) => emit(current.copyWith(classes: _list(v))),
      ),
      wide: true,
    ),
    PairedItem(
      card(
        CameraSetting.describeClasses,
        current.describeClasses,
        (v) => emit(current.copyWith(describeClasses: _list(v))),
      ),
      wide: true,
    ),
    PairedItem(
      card(
        CameraSetting.alertClasses,
        current.alertClasses,
        (v) => emit(current.copyWith(alertClasses: _list(v))),
      ),
      wide: true,
    ),
    PairedItem(
      card(
        CameraSetting.scoreThreshold,
        current.scoreThreshold,
        (v) => emit(current.copyWith(scoreThreshold: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.alertMinConfidence,
        current.alertMinConfidence,
        (v) => emit(current.copyWith(alertMinConfidence: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.minObjectFraction,
        current.minObjectFraction,
        (v) => emit(current.copyWith(minObjectFraction: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.trackConfirmSeconds,
        current.trackConfirmSeconds,
        (v) => emit(current.copyWith(trackConfirmSeconds: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.trackCoastSeconds,
        current.trackCoastSeconds,
        (v) => emit(current.copyWith(trackCoastSeconds: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.maxFps,
        current.maxFps,
        (v) => emit(current.copyWith(maxFps: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.minMovementFraction,
        current.minMovementFraction,
        (v) => emit(current.copyWith(minMovementFraction: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.absenceSeconds,
        current.absenceSeconds,
        (v) => emit(current.copyWith(absenceSeconds: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.noveltySeconds,
        current.noveltySeconds,
        (v) => emit(current.copyWith(noveltySeconds: asDouble(v))),
      ),
    ),
  ];
}

/// How much of the picture has to change before this camera says anything.
List<PairedItem> cameraMotionCards({
  required MotionTuningSettings? tuning,
  required ServerCameraDefaults defaults,
  required ValueChanged<MotionTuningSettings?> onChanged,
  required bool compact,
}) {
  final current = tuning ?? const MotionTuningSettings();
  void emit(MotionTuningSettings updated) =>
      onChanged(updated.isEmpty ? null : updated);

  Widget card(CameraSetting field, Object? value, ValueChanged<Object?> set) =>
      CameraSettingCard(
        field: field,
        defaults: defaults,
        value: value,
        onChanged: set,
        compact: compact,
      );

  return [
    PairedItem(
      card(
        CameraSetting.motionMinChangedFraction,
        current.minChangedFraction,
        (v) => emit(current.copyWith(minChangedFraction: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.motionMaxChangedFraction,
        current.maxChangedFraction,
        (v) => emit(current.copyWith(maxChangedFraction: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.motionPixelDelta,
        current.pixelDelta,
        (v) => emit(current.copyWith(pixelDelta: asInt(v))),
      ),
    ),
  ];
}

/// Which sounds this camera reports, and which of them are alarming.
///
/// The gate in front of all of this — *Counts as silence below* — is **not** here. It is an RMS
/// level, and a generic card snaps its slider to two decimal places, which would put every value
/// worth setting on a real camera below the first stop. The camera editor draws it as a log-scaled
/// row of its own, labelled from this same catalogue.
List<PairedItem> cameraSoundCards({
  required SoundTuningSettings? tuning,
  required ServerCameraDefaults defaults,
  required ValueChanged<SoundTuningSettings?> onChanged,
  required bool compact,
}) {
  final current = tuning ?? const SoundTuningSettings();
  void emit(SoundTuningSettings updated) =>
      onChanged(updated.isEmpty ? null : updated);

  Widget card(CameraSetting field, Object? value, ValueChanged<Object?> set) =>
      CameraSettingCard(
        field: field,
        defaults: defaults,
        value: value,
        onChanged: set,
        compact: compact,
      );

  return [
    PairedItem(
      card(
        CameraSetting.soundAlertLabels,
        current.alertLabels,
        (v) => emit(current.copyWith(alertLabels: _list(v))),
      ),
      wide: true,
    ),
    PairedItem(
      card(
        CameraSetting.soundIgnoredLabels,
        current.ignoredLabels,
        (v) => emit(current.copyWith(ignoredLabels: _list(v))),
      ),
      wide: true,
    ),
    PairedItem(
      card(
        CameraSetting.soundMinConfidence,
        current.minConfidence,
        (v) => emit(current.copyWith(minConfidence: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.soundAlertMinConfidence,
        current.alertMinConfidence,
        (v) => emit(current.copyWith(alertMinConfidence: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.soundCooldownSeconds,
        current.cooldownSeconds,
        (v) => emit(current.copyWith(cooldownSeconds: asDouble(v))),
      ),
    ),
    PairedItem(
      card(
        CameraSetting.soundAlertCooldownSeconds,
        current.alertCooldownSeconds,
        (v) => emit(current.copyWith(alertCooldownSeconds: asDouble(v))),
      ),
    ),
  ];
}

/// A card's value on its way back into a typed record.
///
/// [SettingNumberControl] and [LabelChipList] both speak `Object?`, because the Server page's
/// settings are untyped on the wire. A camera's are not, so this is where the two meet.

List<String>? _list(Object? value) => switch (value) {
  final List<dynamic> items => [for (final item in items) '$item'],
  _ => null,
};

/// What a section has to say about itself — an inert setting, a value that contradicts another.
class TuningNote extends StatelessWidget {
  const TuningNote(this.text, {super.key, this.warning = false});

  final String text;
  final bool warning;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
    decoration: BoxDecoration(
      color: warning
          ? Serval.alert.withValues(alpha: 0.08)
          : Nocturne.mix(Nocturne.text, 3),
      borderRadius: BorderRadius.circular(8),
      border: Border.all(
        color: warning
            ? Serval.alert.withValues(alpha: 0.35)
            : Nocturne.mix(Nocturne.text, 10),
      ),
    ),
    child: Text(
      text,
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 11.5,
        height: 1.45,
        color: warning ? Serval.alertText : Nocturne.mix(Nocturne.text, 50),
      ),
    ),
  );
}
