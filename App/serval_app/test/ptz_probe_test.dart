// What the pad's centre key does, and how the four probe states read.
//
// Worth its own test because the wrong answer here does not throw — it physically turns a camera
// somebody is watching, or draws a control the camera does not have. Both cases were live: the old
// code sent preset '1' unconditionally, and the real pan/tilt camera on the NVR stores no presets
// at all.
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/ptz.dart';

void main() {
  PtzKnown known({
    bool panTilt = true,
    bool zoom = false,
    bool home = false,
    List<PtzPreset> presets = const [],
  }) => PtzKnown(panTilt: panTilt, zoom: zoom, home: home, presets: presets);

  group('the home key', () {
    test('a real home position wins over everything', () {
      final action = PtzHomeAction.of(
        known(home: true, presets: const [PtzPreset('1', 'Gate')]),
      );

      expect(action, isA<PtzHomeGoHome>());
    });

    test('a preset named home stands in for one', () {
      final action = PtzHomeAction.of(
        known(
          presets: const [PtzPreset('4', 'Gate'), PtzPreset('7', ' Home ')],
        ),
      );

      expect(action, isA<PtzHomeGoPreset>());
      expect((action as PtzHomeGoPreset).preset.token, '7');
    });

    test('a single preset is recalled directly', () {
      final action = PtzHomeAction.of(
        known(presets: const [PtzPreset('3', 'Drive')]),
      );

      expect((action as PtzHomeGoPreset).preset.token, '3');
    });

    test('several presets and no home offers a choice rather than guessing', () {
      // "Preset 1" is an ordering accident. Recalling the wrong one points a camera somewhere the
      // person watching did not ask for, which is a physical, visible wrong answer.
      final action = PtzHomeAction.of(
        known(
          presets: const [PtzPreset('1', 'Gate'), PtzPreset('2', 'Street')],
        ),
      );

      expect(action, isA<PtzHomeChoose>());
      expect((action as PtzHomeChoose).presets, hasLength(2));
    });

    test('no home and no presets means no key at all', () {
      // The live NVR's pan/tilt camera reports exactly this: home false, presets empty. The old
      // code fired preset '1' at it regardless.
      expect(PtzHomeAction.of(known()), isA<PtzHomeNone>());
    });
  });

  group('what is worth drawing', () {
    test('a fixed camera with an ONVIF endpoint has nothing to draw', () {
      // Not an error, and the common case — the live NVR's bullet camera answers this way.
      expect(known(panTilt: false).any, isFalse);
    });

    test('pan and tilt without zoom is still worth a pad', () {
      expect(known(zoom: false).any, isTrue);
    });

    test('zoom without pan and tilt is worth the slider alone', () {
      expect(known(panTilt: false, zoom: true).any, isTrue);
    });
  });

  group('preset labels', () {
    test('an unnamed preset is called by its token', () {
      expect(const PtzPreset('5', null).label, 'Preset 5');
      expect(const PtzPreset('5', 'Gate').label, 'Gate');
    });
  });

  group('device information', () {
    test('make and model read together', () {
      const info = DeviceInformation(
        manufacturer: 'Reolink',
        model: 'RLC-810WA',
      );

      expect(info.productLabel, 'Reolink RLC-810WA');
    });

    test('a placeholder make is dropped rather than printed', () {
      // Observed on the live NVR: Reolink's E1 Pro firmware answers the literal string
      // "Manufacturer", which would render as "Manufacturer E1 Pro".
      const info = DeviceInformation(
        manufacturer: 'Manufacturer',
        model: 'E1 Pro',
      );

      expect(info.productLabel, 'E1 Pro');
    });

    test('a camera that says nothing has no label at all', () {
      expect(const DeviceInformation().productLabel, isNull);
    });

    test('a make with no model still reads', () {
      expect(
        const DeviceInformation(manufacturer: 'Axis').productLabel,
        'Axis',
      );
    });
  });

  // The distinction the whole type exists for. A measured position is the camera's answer and can
  // be stated; a reckoned one is a count of our own commands and cannot, because it is wrong the
  // moment anything else touches the camera and there is no way to notice.
  group('the zoom position', () {
    test('a measured position states a percentage of travel', () {
      expect(const ZoomPosition.measured(0.4).label, '40%');
      expect(const ZoomPosition.measured(0).label, '0%');
      expect(const ZoomPosition.measured(1).label, '100%');
    });

    test('a reckoned position says nothing at all', () {
      // Not "unknown", not a dash — the row is left out. Printing our own arithmetic where a
      // measurement goes is a claim the camera never made.
      expect(const ZoomPosition.reckoned(0.4).label, isNull);
    });

    test('a camera that reports nothing starts mid-travel and unmeasured', () {
      expect(ZoomPosition.unknown.measured, isFalse);
      expect(ZoomPosition.unknown.label, isNull);

      // Mid-travel rather than either end: both ends are a claim about the lens, and the middle
      // reads as a starting point.
      expect(ZoomPosition.unknown.value, 0.5);
    });

    test('dragging keeps whether the position was measured', () {
      // A drag moves the knob optimistically before the camera is re-read. It must not turn a
      // reckoned position into a measured one on the way — the readout would appear mid-gesture
      // showing a number nothing had reported.
      expect(const ZoomPosition.reckoned(0.2).withValue(0.6).measured, isFalse);
      expect(const ZoomPosition.measured(0.2).withValue(0.6).measured, isTrue);
      expect(const ZoomPosition.measured(0.2).withValue(0.6).value, 0.6);
    });

    test('a drag past either end clamps', () {
      expect(const ZoomPosition.measured(0.5).withValue(1.4).value, 1.0);
      expect(const ZoomPosition.measured(0.5).withValue(-0.3).value, 0.0);
    });

    test('absolute zoom is off unless the camera reported the space', () {
      // Sending an AbsoluteMove to a camera that only takes velocities is a command it rejects, so
      // the default has to be the one that still works.
      expect(known(zoom: true).absoluteZoom, isFalse);
      expect(
        PtzKnown(
          panTilt: false,
          zoom: true,
          absoluteZoom: true,
          home: false,
        ).absoluteZoom,
        isTrue,
      );
    });
  });
}
