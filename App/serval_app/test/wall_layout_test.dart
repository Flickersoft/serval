import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/models/wall_layout.dart';

/// The wall's drop rules as arithmetic. No widgets, no gestures, no pumping —
/// which is the whole reason the maths lives in `WallGrid` rather than in the
/// grid widget that calls it.
void main() {
  TileLayout tile(
    String id,
    int column,
    int row, {
    int columnSpan = 3,
    int rowSpan = 1,
  }) => TileLayout(
    cameraId: id,
    column: column,
    row: row,
    columnSpan: columnSpan,
    rowSpan: rowSpan,
  );

  TileLayout of(List<TileLayout> layout, String id) =>
      layout.firstWhere((t) => t.cameraId == id);

  group('readingOrder', () {
    test('is top to bottom, then left to right', () {
      final saved = [
        tile('garage', 18, 2),
        tile('driveway', 0, 0, columnSpan: 12, rowSpan: 4),
        tile('side-path', 0, 4),
        tile('kitchen', 18, 0),
        tile('front-door', 12, 0),
        tile('back-yard', 12, 2),
      ];

      expect(WallGrid.readingOrder(saved).map((t) => t.cameraId), [
        'driveway',
        'front-door',
        'kitchen',
        'back-yard',
        'garage',
        'side-path',
      ]);
    });

    test('leaves the arrangement it was given alone', () {
      final saved = [tile('b', 6, 0), tile('a', 0, 0)];
      WallGrid.readingOrder(saved);

      expect(saved.map((t) => t.cameraId), ['b', 'a']);
    });
  });

  group('move', () {
    test('takes free space', () {
      final layout = [tile('a', 0, 0), tile('b', 3, 0)];
      final moved = WallGrid.move(layout, 'a', column: 6, row: 2)!;

      expect(of(moved, 'a'), tile('a', 6, 2));
      expect(of(moved, 'b'), tile('b', 3, 0));
    });

    test('clamps at the left and right edges', () {
      final layout = [tile('a', 3, 0)];

      expect(WallGrid.move(layout, 'a', column: -4, row: 0), [tile('a', 0, 0)]);
      expect(WallGrid.move(layout, 'a', column: 40, row: 0), [
        tile('a', WallGrid.columns - 3, 0),
      ]);
    });

    test('clamps a wide tile by its own span, not by the last column', () {
      final layout = [tile('a', 0, 0, columnSpan: 6, rowSpan: 2)];

      // Dropped past the right-hand edge, so its left edge comes to rest a full
      // span short of it rather than on the last column.
      expect(WallGrid.move(layout, 'a', column: WallGrid.columns + 4, row: 0), [
        tile('a', WallGrid.columns - 6, 0, columnSpan: 6, rowSpan: 2),
      ]);
    });

    test('clamps rows at the top but not at the bottom', () {
      final layout = [tile('a', 0, 4)];

      expect(WallGrid.move(layout, 'a', column: 0, row: -3), [tile('a', 0, 0)]);
      // Down is where the wall grows; nothing here bounds it.
      expect(WallGrid.move(layout, 'a', column: 0, row: 99), [
        tile('a', 0, 99),
      ]);
    });

    test('refuses a move that goes nowhere', () {
      final layout = [tile('a', 3, 1)];
      expect(WallGrid.move(layout, 'a', column: 3, row: 1), isNull);
    });

    test('refuses a camera that is not on the wall', () {
      expect(WallGrid.move([tile('a', 0, 0)], 'b', column: 3, row: 0), isNull);
    });

    test('swaps with the one tile it lands on', () {
      final layout = [tile('a', 0, 0), tile('b', 3, 0)];
      final moved = WallGrid.move(layout, 'a', column: 3, row: 0)!;

      expect(of(moved, 'a'), tile('a', 3, 0));
      expect(of(moved, 'b'), tile('b', 0, 0));
    });

    test('swaps tiles of different spans when the result still fits', () {
      final layout = [
        tile('hero', 0, 0, columnSpan: 6, rowSpan: 2),
        tile('a', 6, 0),
      ];
      final moved = WallGrid.move(layout, 'hero', column: 6, row: 0)!;

      expect(of(moved, 'hero'), tile('hero', 6, 0, columnSpan: 6, rowSpan: 2));
      expect(of(moved, 'a'), tile('a', 0, 0));
    });

    test('re-homes a tile that cannot simply mirror into the space left', () {
      // Mirroring would send the hero to column 9, where it needs columns 9..14
      // and there are only twelve. It is re-placed instead of the drop being
      // refused — and it keeps its size; only where it sits is chosen for it.
      final layout = [
        tile('hero', 0, 0, columnSpan: 6, rowSpan: 2),
        tile('a', 9, 0),
      ];

      final moved = WallGrid.move(layout, 'a', column: 0, row: 0)!;

      expect(of(moved, 'a'), tile('a', 0, 0), reason: 'the drop is taken');
      expect(of(moved, 'hero').columnSpan, 6);
      expect(of(moved, 'hero').rowSpan, 2);
      expect(WallGrid.overlaps(of(moved, 'a'), of(moved, 'hero')), isFalse);
      expect(of(moved, 'hero').column + 6, lessThanOrEqualTo(WallGrid.columns));
    });

    test('trades places with every tile it lands on, not just one', () {
      final layout = [
        tile('a', 0, 0, columnSpan: 6),
        tile('b', 6, 0),
        tile('c', 9, 0),
      ];

      // The wide tile takes the two narrow ones' space; they take its.
      expect(WallGrid.move(layout, 'a', column: 6, row: 0), [
        tile('a', 6, 0, columnSpan: 6),
        tile('b', 0, 0),
        tile('c', 3, 0),
      ]);
    });

    test('a hero can swap down onto the row of small tiles beneath it', () {
      // Two heroes across the top and eight small tiles under them — the shape
      // a wall of fifteen cameras actually takes. Dropping a hero one row down
      // lands it on four small tiles at once, and all four have to come up into
      // the space it left or the hero can never be moved down at all.
      final layout = [
        tile('hero1', 0, 0, columnSpan: 6, rowSpan: 2),
        tile('hero2', 6, 0, columnSpan: 6, rowSpan: 2),
        tile('a', 0, 2),
        tile('b', 3, 2),
        tile('c', 6, 2),
        tile('d', 9, 2),
        tile('e', 0, 3),
        tile('f', 3, 3),
        tile('g', 6, 3),
        tile('h', 9, 3),
      ];

      final moved = WallGrid.move(layout, 'hero1', column: 0, row: 2)!;

      expect(
        of(moved, 'hero1'),
        tile('hero1', 0, 2, columnSpan: 6, rowSpan: 2),
      );
      expect(of(moved, 'a'), tile('a', 0, 0));
      expect(of(moved, 'b'), tile('b', 3, 0));
      expect(of(moved, 'e'), tile('e', 0, 1));
      expect(of(moved, 'f'), tile('f', 3, 1));

      // Everything the hero did not land on is exactly where it was.
      for (final id in ['hero2', 'c', 'd', 'g', 'h']) {
        expect(of(moved, id), of(layout, id), reason: '$id should not move');
      }
    });

    test('re-places a displaced tile that would mirror onto the mover', () {
      final layout = [
        tile('a', 0, 0, columnSpan: 6),
        tile('b', 6, 0),
        tile('c', 9, 0),
      ];

      // Three columns across only, so mirroring would slide b back into the
      // very space a is taking. It goes to the gap a left instead.
      expect(WallGrid.move(layout, 'a', column: 3, row: 0), [
        tile('a', 3, 0, columnSpan: 6),
        tile('b', 0, 0),
        tile('c', 9, 0),
      ]);
    });

    test('a move is refused only when there is nothing to do', () {
      final layout = [tile('a', 0, 0), tile('b', 3, 0)];

      expect(WallGrid.move(layout, 'a', column: 0, row: 0), isNull);
      expect(WallGrid.move(layout, 'nobody', column: 6, row: 0), isNull);
    });

    test('never returns an arrangement that overlaps or leaves the grid', () {
      // A crowded wall of mixed spans, dropped every which way. Whatever route
      // `move` takes — free space, a rigid trade, or a repack — the wall it
      // hands back has to be one that could have been drawn.
      final layout = [
        tile('hero', 0, 0, columnSpan: 6, rowSpan: 2),
        tile('wide', 6, 0, columnSpan: 6),
        tile('a', 6, 1),
        tile('b', 9, 1),
        tile('tall', 0, 2, columnSpan: 4, rowSpan: 3),
        tile('c', 4, 2),
        tile('d', 7, 2, columnSpan: 5),
      ];

      for (final id in ['hero', 'wide', 'a', 'b', 'tall', 'c', 'd']) {
        for (var column = 0; column < WallGrid.columns; column++) {
          for (var row = 0; row < 6; row++) {
            final moved = WallGrid.move(layout, id, column: column, row: row);
            if (moved == null) continue;

            expect(moved, hasLength(layout.length));
            expect(
              moved.map((t) => t.cameraId).toSet(),
              layout.map((t) => t.cameraId).toSet(),
            );
            for (var i = 0; i < moved.length; i++) {
              expect(moved[i].column, greaterThanOrEqualTo(0));
              expect(moved[i].row, greaterThanOrEqualTo(0));
              expect(
                moved[i].column + moved[i].columnSpan,
                lessThanOrEqualTo(WallGrid.columns),
                reason: '$id to $column,$row put ${moved[i]} off the grid',
              );
              for (var j = i + 1; j < moved.length; j++) {
                expect(
                  WallGrid.overlaps(moved[i], moved[j]),
                  isFalse,
                  reason:
                      '$id to $column,$row: ${moved[i]} overlaps ${moved[j]}',
                );
              }
            }
          }
        }
      }
    });
  });

  group('resize', () {
    test('grows into free space', () {
      final layout = [tile('a', 0, 0), tile('b', 6, 0)];

      expect(
        WallGrid.resize(layout, 'a', columnSpan: 6, rowSpan: 2),
        contains(tile('a', 0, 0, columnSpan: 6, rowSpan: 2)),
      );
    });

    test('refuses a growth that would overlap a neighbour', () {
      final layout = [tile('a', 0, 0), tile('b', 3, 0)];
      expect(WallGrid.resize(layout, 'a', columnSpan: 6, rowSpan: 1), isNull);
    });

    test('clamps the span at the right-hand edge', () {
      // Already flush against the edge, so there is no width left to grow into
      // and asking for more is a resize that changes nothing.
      final flush = WallGrid.columns - 3;
      final layout = [tile('a', flush, 0)];

      expect(WallGrid.resize(layout, 'a', columnSpan: 99, rowSpan: 1), isNull);
      expect(WallGrid.resize(layout, 'a', columnSpan: 99, rowSpan: 2), [
        tile('a', flush, 0, rowSpan: 2),
      ]);
    });

    test('never goes below one cell', () {
      final layout = [tile('a', 0, 0, columnSpan: 6, rowSpan: 2)];

      expect(WallGrid.resize(layout, 'a', columnSpan: -4, rowSpan: 0), [
        tile('a', 0, 0, columnSpan: 1, rowSpan: 1),
      ]);
    });

    test('grows downward without bound', () {
      final layout = [tile('a', 0, 0)];

      expect(WallGrid.resize(layout, 'a', columnSpan: 3, rowSpan: 40), [
        tile('a', 0, 0, rowSpan: 40),
      ]);
    });

    test('always accepts a shrink, however crowded the wall', () {
      final layout = [
        tile('a', 0, 0, columnSpan: 6, rowSpan: 2),
        tile('b', 6, 0),
        tile('c', 6, 1),
      ];

      expect(
        WallGrid.resize(layout, 'a', columnSpan: 3, rowSpan: 1),
        contains(tile('a', 0, 0)),
      );
    });
  });

  group('geometry', () {
    test('a standard tile is 16:9', () {
      const cellWidth = 100.0;
      const gap = 12.0;
      const span = WallGrid.smallSpan;
      final rowHeight = WallGrid.rowHeightFor(cellWidth, gap);
      final width = span.columnSpan * cellWidth + (span.columnSpan - 1) * gap;
      final height = span.rowSpan * rowHeight + (span.rowSpan - 1) * gap;

      // Exactly, gaps included — a standard tile spanning more than one row is
      // what [rowHeightFor] solves for, so nothing is left over here.
      expect(width / height, closeTo(16 / 9, 0.0001));
    });

    test('a hero is within a gap of the same shape', () {
      const cellWidth = 100.0;
      const gap = 12.0;
      const span = WallGrid.heroSpan;
      final rowHeight = WallGrid.rowHeightFor(cellWidth, gap);
      final width = span.columnSpan * cellWidth + (span.columnSpan - 1) * gap;
      final height = span.rowSpan * rowHeight + (span.rowSpan - 1) * gap;

      // The gaps are the one thing that does not scale with the span, so the
      // ratio drifts by a fraction of a gap rather than landing exactly.
      expect(width / height, closeTo(16 / 9, 0.1));
    });

    test('lastDropRow is the first empty row, whatever a tile is tall', () {
      // Two heroes side by side: rows 0 and 1 are full, row 2 is the first
      // empty one. A two-row tile has to be able to reach it — bounding by the
      // bottom edge instead stops it at row 1 and makes stacking the two
      // impossible.
      final heroes = [
        tile('a', 0, 0, columnSpan: 6, rowSpan: 2),
        tile('b', 6, 0, columnSpan: 6, rowSpan: 2),
      ];

      expect(WallGrid.lastDropRow(heroes), 2);
      expect(WallGrid.move(heroes, 'b', column: 0, row: 2), [
        tile('a', 0, 0, columnSpan: 6, rowSpan: 2),
        tile('b', 0, 2, columnSpan: 6, rowSpan: 2),
      ]);
    });

    test('rowsFor is the lowest edge plus a spare row to drop into', () {
      expect(WallGrid.rowsFor(const []), 1);
      expect(WallGrid.rowsFor([tile('a', 0, 0)]), 2);
      expect(WallGrid.rowsFor([tile('a', 0, 0), tile('b', 3, 3)]), 5);
      expect(WallGrid.rowsFor([tile('a', 0, 2, rowSpan: 3)]), 6);
    });
  });

  group('packDefault', () {
    test('places every camera, however many there are', () {
      // The grid has no row bound, so a wall of forty cameras draws forty tiles
      // rather than as many as a fixed grid had room for.
      final ids = [for (var i = 0; i < 40; i++) 'cam$i'];
      final layout = WallGrid.packDefault(ids, occupied: const []);

      expect(layout, hasLength(40));
      expect(
        layout.map((t) => t.cameraId).toSet(),
        ids.toSet(),
        reason: 'every camera handed in gets a tile',
      );

      for (final t in layout) {
        expect(t.column, greaterThanOrEqualTo(0));
        expect(t.column + t.columnSpan, lessThanOrEqualTo(WallGrid.columns));
      }
      for (var i = 0; i < layout.length; i++) {
        for (var j = i + 1; j < layout.length; j++) {
          expect(
            WallGrid.overlaps(layout[i], layout[j]),
            isFalse,
            reason: '${layout[i]} overlaps ${layout[j]}',
          );
        }
      }
    });

    test('the first camera on an empty wall is the hero', () {
      final layout = WallGrid.packDefault(['a', 'b'], occupied: const []);

      expect(
        layout.first,
        tile(
          'a',
          0,
          0,
          columnSpan: WallGrid.heroSpan.columnSpan,
          rowSpan: WallGrid.heroSpan.rowSpan,
        ),
      );
      expect(layout[1].columnSpan, WallGrid.smallSpan.columnSpan);
    });

    test('no hero when the wall already has tiles on it', () {
      final layout = WallGrid.packDefault(['b'], occupied: [tile('a', 0, 0)]);

      expect(layout.single.columnSpan, WallGrid.smallSpan.columnSpan);
    });
  });

  group('reconcile', () {
    test('drops a camera that is gone and appends one that is new', () {
      final saved = [tile('a', 0, 0), tile('gone', 3, 0)];
      final layout = WallGrid.reconcile(saved, ['a', 'new']);

      expect(layout.map((t) => t.cameraId), ['a', 'new']);
      expect(of(layout, 'a'), tile('a', 0, 0), reason: 'a keeps its place');
    });

    test('an empty arrangement is just the default packing', () {
      final ids = ['a', 'b', 'c', 'd', 'e'];

      expect(
        WallGrid.reconcile(const [], ids),
        WallGrid.packDefault(ids, occupied: const []),
      );
    });

    test('keeps an arrangement whole when nothing has changed', () {
      final saved = [tile('a', 0, 0), tile('b', 6, 3)];
      expect(WallGrid.reconcile(saved, ['a', 'b']), saved);
    });
  });
}
