// Reading this account's own preferences off the Server.
//
// The wall layout stopped living in `shared_preferences` because it is the one piece of this state
// that is genuinely about the person rather than the machine — an arrangement you make once and
// want on the next browser. What stayed behind (volume, the activity panel's collapsed state) is
// per-device on purpose, which is a decision worth not undoing by accident.
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/user_preferences.dart';

void main() {
  group('reading', () {
    test('a saved arrangement comes back tile for tile', () {
      final preferences = UserPreferences.fromJson(const {
        'wallLayout': [
          {
            'cameraId': 'front-door',
            'column': 0,
            'row': 0,
            'columnSpan': 12,
            'rowSpan': 4,
          },
          {
            'cameraId': 'driveway',
            'column': 12,
            'row': 0,
            'columnSpan': 6,
            'rowSpan': 2,
          },
        ],
        'updatedAt': '2026-08-08T10:30:00Z',
      });

      expect(preferences.wallLayout.length, 2);
      expect(preferences.wallLayout.first.cameraId, 'front-door');
      expect(preferences.wallLayout.first.columnSpan, 12);
      expect(preferences.wallLayout.last.column, 12);
      expect(preferences.updatedAt, isNotNull);
    });

    test('an account that never saved anything reads as empty', () {
      // The Server answers 200 with an empty list rather than 404, because "I have not arranged my
      // wall" and "my wall is empty" mean the same thing here — both pack the default.
      final preferences = UserPreferences.fromJson(const {
        'wallLayout': <Map<String, dynamic>>[],
        'updatedAt': '2026-08-08T10:30:00Z',
      });

      expect(preferences.wallLayout, isEmpty);
    });

    test('a missing wallLayout is empty rather than a crash', () {
      // A Server too old to know about preferences, or a response shape that changed underneath a
      // cached build. Losing the arrangement is survivable; throwing on the wall's first read is
      // not.
      expect(UserPreferences.fromJson(const {}).wallLayout, isEmpty);
      expect(UserPreferences.fromJson(const {}).updatedAt, isNull);
    });

    test('a tile missing its spans falls back to one cell', () {
      final preferences = UserPreferences.fromJson(const {
        'wallLayout': [
          {'cameraId': 'front-door', 'column': 3, 'row': 2},
        ],
      });

      final tile = preferences.wallLayout.single;
      expect(tile.column, 3);
      expect(tile.row, 2);
      expect(tile.columnSpan, 1);
      expect(tile.rowSpan, 1);
    });

    test('an unparseable timestamp is absent rather than thrown', () {
      final preferences = UserPreferences.fromJson(const {
        'wallLayout': <Map<String, dynamic>>[],
        'updatedAt': 'not a date',
      });

      expect(preferences.updatedAt, isNull);
    });

    test('the default instance is an empty wall', () {
      // What the repository falls back to when the request fails. It has to be indistinguishable
      // from a never-arranged wall, or a failed fetch would draw something different from a cold
      // start and look like data loss.
      const preferences = UserPreferences();

      expect(preferences.wallLayout, isEmpty);
      expect(preferences.updatedAt, isNull);
    });
  });

  // Every nullable field on a rule means *inherit* by being absent, and the value that would be
  // inherited is not the empty one — an empty class list notifies about nothing, and a zero wait
  // notifies about everything. A round trip that flattened either into the other would look
  // identical on screen the day it shipped and be wrong the day an admin changed a default.
  group('a notification rule on the wire', () {
    test('a rule that chose nothing comes back choosing nothing', () {
      final rule = CameraNotificationRule.fromJson(
        const CameraNotificationRule(cameraId: 'driveway').toJson(),
      );

      expect(rule.cameraId, 'driveway');
      expect(rule.enabled, isTrue);
      expect(rule.objectClasses, isNull);
      expect(rule.soundLabels, isNull);
      expect(rule.cooldownSeconds, isNull);
      expect(rule.isDefault, isTrue);
    });

    test('choices survive it', () {
      final rule = CameraNotificationRule.fromJson(
        const CameraNotificationRule(
          cameraId: 'driveway',
          enabled: false,
          objectClasses: ['person'],
          soundLabels: [],
          cooldownSeconds: 900,
        ).toJson(),
      );

      expect(rule.enabled, isFalse);
      expect(rule.objectClasses, ['person']);
      expect(rule.soundLabels, isEmpty);
      expect(rule.cooldownSeconds, 900);
    });

    test('a wait of zero is not the same as never having set one', () {
      final chosen = CameraNotificationRule.fromJson(
        const CameraNotificationRule(
          cameraId: 'driveway',
          cooldownSeconds: 0,
        ).toJson(),
      );

      expect(chosen.cooldownSeconds, 0);

      // And the rule is not thrown away on save, which is what `isDefault` decides. A zero that
      // read as default would be dropped and silently become whatever the deployment does.
      expect(chosen.isDefault, isFalse);
    });

    test('a body from a build that predates the wait reads as inherit', () {
      final rule = CameraNotificationRule.fromJson(const {
        'cameraId': 'driveway',
        'enabled': true,
        'objectClasses': null,
        'soundLabels': null,
      });

      expect(rule.cooldownSeconds, isNull);
      expect(rule.isDefault, isTrue);
    });

    test('clearing a wait puts it back to inherit rather than to zero', () {
      const rule = CameraNotificationRule(
        cameraId: 'driveway',
        cooldownSeconds: 300,
      );

      expect(rule.copyWith(clearCooldownSeconds: true).cooldownSeconds, isNull);

      // And an untouched copy keeps it: copyWith's null argument means "leave alone", which is why
      // clearing needs a flag of its own.
      expect(rule.copyWith(enabled: false).cooldownSeconds, 300);
    });
  });
}
