/// What a camera's pan/tilt/zoom can actually do, as the camera itself reports it.
///
/// Sealed rather than a bag of nullable flags so the house rule is structurally enforced: you
/// cannot read an axis without first matching the case that says we asked and were told. The four
/// states are genuinely different things, and collapsing any two of them draws something untrue —
/// "we have not asked yet" is not "there is no pan/tilt", and neither is "the camera did not
/// answer".
///
/// A `supportsPtz` flag alone cannot carry any of that, and on the live NVR it draws both of its
/// cameras wrong: a fixed bullet camera gets a pad and a zoom slider it has neither of, and a
/// pan/tilt camera with a fixed lens gets a zoom slider that does nothing.
sealed class PtzProbe {
  const PtzProbe();
}

/// Asked, no answer yet.
///
/// Draws nothing. Not a disabled pad: a control that materialises a moment later reads as a
/// glitch, and the probe usually lands before the WebRTC session does anyway.
class PtzProbing extends PtzProbe {
  const PtzProbing();
}

/// This camera has no ONVIF endpoint, so there is nothing to ask.
///
/// Draws nothing, and says nothing — the settings screen already explains how to configure it,
/// and repeating that over the video would be nagging.
class PtzNotConfigured extends PtzProbe {
  const PtzNotConfigured();
}

/// We asked and could not be told — the camera was unreachable, or its ONVIF service faulted.
///
/// Draws no controls **and** says why, in the Server's own words. Hiding this silently would be
/// indistinguishable from "this camera has no pan/tilt", which is a different fact; drawing a
/// working-looking pad would be the guess the house rule forbids.
class PtzUnknown extends PtzProbe {
  const PtzUnknown(this.reason);

  final String reason;
}

/// What the camera said it can do.
///
/// [panTilt] and [zoom] are decided Server-side on the ONVIF *continuous velocity* spaces, because
/// that is how both axes are driven. [absoluteZoom] is the exception and reads the absolute space:
/// zoom is the one axis with a position worth showing, so a camera that can be told where to go is
/// driven there rather than nudged towards it.
///
/// All-false is a real and common answer, not an error: a fixed camera with an ONVIF endpoint
/// reports exactly this.
class PtzKnown extends PtzProbe {
  const PtzKnown({
    required this.panTilt,
    required this.zoom,
    required this.home,
    this.absoluteZoom = false,
    this.presets = const [],
    this.maximumPresets,
  });

  final bool panTilt;
  final bool zoom;

  /// The camera takes an absolute zoom position, so a drag can be a destination rather than a
  /// direction. False leaves the slider on velocity nudges plus a read-back.
  final bool absoluteZoom;

  /// The camera has a stored home position — ONVIF `HomeSupported`.
  final bool home;

  /// Stored positions, in the camera's own order.
  final List<PtzPreset> presets;

  /// How many presets the camera will hold, where it says.
  final int? maximumPresets;

  /// Whether there is any control worth drawing at all.
  bool get any => panTilt || zoom;
}

/// Where the zoom slider sits, and whether that is a measurement or a guess.
///
/// The distinction is the whole point of the type. A camera that reports its lens position gives a
/// knob that means something — reopen the view on a zoomed-in camera and it is still zoomed in.
/// A camera that does not leaves only dead reckoning: counting the commands *we* sent since the
/// view opened, which starts wrong the moment anything else touches the camera and cannot recover.
///
/// Both draw the same track, because there is nothing useful to draw instead. What differs is the
/// readout: a measured position states a percentage of the lens's travel, and a reckoned one says
/// nothing at all rather than dressing our own arithmetic up as a reading.
///
/// The value is a fraction of travel in ONVIF's generic zoom space, never a magnification.
/// Nothing in ONVIF publishes the optical range that would turn 0.4 into `2.4×`, and the curve
/// between them is vendor-specific — so the factor the design drew is not merely missing, it is
/// not derivable.
class ZoomPosition {
  const ZoomPosition._(this.value, this.measured);

  /// The camera told us where it is.
  const ZoomPosition.measured(double value) : this._(value, true);

  /// Nobody told us; this is what we have asked for since the view opened.
  const ZoomPosition.reckoned(double value) : this._(value, false);

  /// A camera that reports no position, before anything has been asked of it. Mid-travel rather
  /// than at either end, because both ends are a claim and the middle is visibly a starting point.
  static const unknown = ZoomPosition._(0.5, false);

  /// 0 (wide) to 1 (tight).
  final double value;

  /// Whether [value] came from the camera. False means dead reckoning — see the class comment.
  final bool measured;

  /// What to print beside the track, or null when there is nothing honest to print.
  String? get label => measured ? '${(value * 100).round()}%' : null;

  ZoomPosition withValue(double next) =>
      ZoomPosition._(next.clamp(0.0, 1.0), measured);
}

/// One stored position. [token] is what the Server's `/ptz/preset` takes — never the name and
/// never the index, both of which drift.
class PtzPreset {
  const PtzPreset(this.token, this.name);

  final String token;

  /// Optional in ONVIF, and plenty of cameras leave it off.
  final String? name;

  /// What to call it in a menu when the camera did not name it.
  String get label => name ?? 'Preset $token';
}

/// What the pad's centre key should do, worked out from what the camera reported.
///
/// A sealed result rather than a nullable callback so the decision is testable on its own — it is
/// a small ladder with a wrong answer at every rung, and the wrong answer physically turns a
/// camera somebody is watching.
sealed class PtzHomeAction {
  const PtzHomeAction();

  /// The ladder, in order: a real ONVIF home position; else a preset actually named "home"; else
  /// the only preset there is; else a choice between several; else nothing to do.
  ///
  /// A fixed preset token is not a rung: a camera with **no presets stored at all** is the common
  /// case, and sending `'1'` there fires a token nothing will answer to.
  factory PtzHomeAction.of(PtzKnown capabilities) {
    if (capabilities.home) return const PtzHomeGoHome();

    final named = capabilities.presets.where(
      (p) => (p.name ?? '').trim().toLowerCase() == 'home',
    );
    if (named.isNotEmpty) return PtzHomeGoPreset(named.first);

    return switch (capabilities.presets.length) {
      0 => const PtzHomeNone(),
      1 => PtzHomeGoPreset(capabilities.presets.single),
      _ => PtzHomeChoose(capabilities.presets),
    };
  }
}

/// `POST /ptz/home` — the camera has a real home position.
class PtzHomeGoHome extends PtzHomeAction {
  const PtzHomeGoHome();
}

/// Recall one preset directly, by its token.
class PtzHomeGoPreset extends PtzHomeAction {
  const PtzHomeGoPreset(this.preset);

  final PtzPreset preset;
}

/// Several presets and no home: the key opens a menu rather than guessing which one you meant.
class PtzHomeChoose extends PtzHomeAction {
  const PtzHomeChoose(this.presets);

  final List<PtzPreset> presets;
}

/// Nothing to recall. The pad draws an empty centre cell rather than a key that does nothing.
class PtzHomeNone extends PtzHomeAction {
  const PtzHomeNone();
}

/// What a camera says it is — ONVIF `GetDeviceInformation`.
///
/// Every field is optional in practice: ONVIF requires all of them and cameras omit them anyway,
/// so null means *the camera did not say* and should render as absence rather than as "unknown".
///
/// There is no uptime. The design's `up 26 days` has no ONVIF call behind it — the Device service
/// exposes the system clock, not how long the device has been running — so it stays absent.
class DeviceInformation {
  const DeviceInformation({
    this.manufacturer,
    this.model,
    this.firmwareVersion,
    this.serialNumber,
    this.hardwareId,
  });

  final String? manufacturer;
  final String? model;
  final String? firmwareVersion;
  final String? serialNumber;
  final String? hardwareId;

  /// `Reolink RLC-810WA`, or just the model where the make is missing or useless.
  ///
  /// Reolink's E1 Pro firmware reports the literal string `Manufacturer` — observed on the live
  /// NVR — so a make that is obviously a placeholder is dropped rather than printed. The model is
  /// the identifying half anyway.
  String? get productLabel {
    final make = manufacturer?.trim();
    final name = model?.trim();

    final usableMake =
        make != null &&
        make.isNotEmpty &&
        make.toLowerCase() != 'manufacturer' &&
        make.toLowerCase() != 'model';

    if (name == null || name.isEmpty) return usableMake ? make : null;
    return usableMake ? '$make $name' : name;
  }
}
