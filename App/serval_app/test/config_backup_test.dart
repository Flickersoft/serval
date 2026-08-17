// What the App makes of a restore's result, and of a backup file it has been handed.
//
// Both directions are deliberately forgiving. The result comes from a Server that may be newer than
// this build, so an unknown section is drawn rather than dropped; the file comes from a disk and
// may be anything at all, so reading it here never throws — the Server is the thing that judges a
// file, and its refusal is written for the person who picked it.
import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/config_backup.dart';

void main() {
  group('restore result', () {
    ConfigRestoreResult parse(Map<String, dynamic> json) =>
        ConfigRestoreResult.fromJson(json);

    test('keeps the sections in the order the Server applied them', () {
      final result = parse({
        'restoredAt': '2026-08-08T14:31:02Z',
        'fileCreatedAt': '2026-08-01T09:30:12Z',
        'fileCreatedBy': 'jeremiah',
        'sections': [
          {
            'name': 'settings',
            'created': 3,
            'updated': 5,
            'skipped': 1,
            'cleared': 2,
          },
          {
            'name': 'cameras',
            'created': 1,
            'updated': 4,
            'skipped': 1,
            'cleared': 0,
          },
          {
            'name': 'users',
            'created': 2,
            'updated': 1,
            'skipped': 0,
            'cleared': 0,
          },
          {
            'name': 'preferences',
            'created': 0,
            'updated': 0,
            'skipped': 0,
            'cleared': 0,
          },
        ],
        'skipped': [],
        'notes': [],
      });

      expect(result.sections.map((s) => s.name), [
        'settings',
        'cameras',
        'users',
        'preferences',
      ]);
      expect(result.fileCreatedBy, 'jeremiah');
      expect(result.changed, 16);
      expect(result.hasSkips, isFalse);
    });

    /// The Server's reasons are written for the person who caused them — a refused transcode names
    /// the encoder and the fix. Anything this App paraphrased instead would be vaguer.
    test('carries the Server’s reasons through untouched', () {
      const reason =
          "Stream 'main' asks to transcode to 'av1', and this host's ffmpeg does "
          'not have the libsvtav1 encoder.';

      final result = parse({
        'restoredAt': '2026-08-08T14:31:02Z',
        'fileCreatedAt': '2026-08-01T09:30:12Z',
        'skipped': [
          {'section': 'cameras', 'item': 'garage', 'reason': reason},
        ],
      });

      expect(result.hasSkips, isTrue);
      expect(result.skipped.single.item, 'garage');
      expect(result.skipped.single.reason, reason);
    });

    /// Section names are labels rather than an enum precisely so a later Server can back up
    /// something this build has never heard of. Dropping the row would under-report the restore.
    test('draws a section it has never heard of rather than dropping it', () {
      final result = parse({
        'restoredAt': '2026-08-08T14:31:02Z',
        'fileCreatedAt': '2026-08-01T09:30:12Z',
        'sections': [
          {
            'name': 'schedules',
            'created': 2,
            'updated': 0,
            'skipped': 0,
            'cleared': 0,
          },
        ],
      });

      expect(result.sections.single.label, 'Schedules');
      expect(result.sections.single.summary, '2 added');
    });

    test('an older Server’s response with no notes reads as none', () {
      final result = parse({
        'restoredAt': '2026-08-08T14:31:02Z',
        'fileCreatedAt': '2026-08-01T09:30:12Z',
      });

      expect(result.notes, isEmpty);
      expect(result.sections, isEmpty);
      expect(result.skipped, isEmpty);
    });

    /// A section with nothing to do says so, rather than reading as three zeros — the same instinct
    /// the rest of this page is built on, where a figure that is missing is drawn as missing.
    test('a section with nothing to do says so', () {
      expect(const RestoreSection(name: 'users').summary, 'nothing to change');
      expect(
        const RestoreSection(name: 'settings', updated: 5, cleared: 2).summary,
        '5 updated · 2 removed',
      );
      expect(
        const RestoreSection(
          name: 'cameras',
          created: 1,
          updated: 4,
          skipped: 1,
        ).summary,
        '4 updated · 1 added · 1 skipped',
      );
    });
  });

  group('backup file summary', () {
    ConfigBackupSummary read(Object? json) =>
        ConfigBackupSummary.fromJson(json);

    test('reads what a real backup says about itself', () {
      final summary = read(
        jsonDecode(
          jsonEncode({
            'kind': 'serval.config-backup',
            'version': 1,
            'warning': 'THIS FILE CONTAINS SECRETS IN PLAIN TEXT…',
            'createdAt': '2026-08-01T09:30:12Z',
            'createdBy': 'jeremiah',
            'settings': {'Serval:Media:RetentionDays': '21'},
            'cameras': [
              {'id': 'front-door'},
              {'id': 'garage'},
            ],
            'users': [
              {'username': 'jeremiah'},
            ],
            'preferences': <Object>[],
          }),
        ),
      );

      expect(summary.isServalBackup, isTrue);
      expect(summary.version, 1);
      expect(summary.createdBy, 'jeremiah');
      expect(summary.contents, '2 cameras, 1 setting and 1 account');
    });

    test('a file that is not a Serval backup says so rather than throwing', () {
      expect(read(jsonDecode('{"hello": "world"}')).isServalBackup, isFalse);
      expect(read(jsonDecode('[1, 2, 3]')).isServalBackup, isFalse);
      expect(read(null).isServalBackup, isFalse);
    });

    /// Every field is tolerated as missing. This is not validation — the Server judges the file,
    /// and a half-read one should still reach it to be refused in the Server's own words.
    test('a backup missing everything but its kind still reads', () {
      final summary = read(jsonDecode('{"kind": "serval.config-backup"}'));

      expect(summary.isServalBackup, isTrue);
      expect(summary.createdAt, isNull);
      expect(summary.contents, 'Nothing');
    });

    test('counts read as English rather than as a tuple', () {
      expect(
        const ConfigBackupSummary(
          isServalBackup: true,
          cameras: 6,
          settings: 12,
          users: 3,
          preferences: 3,
        ).contents,
        '6 cameras, 12 settings, 3 accounts and 3 sets of preferences',
      );
      expect(
        const ConfigBackupSummary(isServalBackup: true, cameras: 1).contents,
        '1 camera',
      );
    });
  });
}
