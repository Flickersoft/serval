import 'camera.dart';

/// The wall's grid, and every rule about what may sit where on it.
///
/// Twenty-four columns, and as many rows as there are cameras to hold. Columns
/// are the granularity of a tile's *size*, not a count of cameras: the standard
/// tile is six columns wide — four across — a hero is twelve, and a full-width
/// tile is twenty-four. Twenty-four divides by 2, 3, 4, 6, 8 and 12, so mixed
/// sizes line up instead of leaving slivers.
///
/// The grid is deliberately finer than the sizes anyone reaches for. A cell is
/// half a step of the standard tile on each axis, which is what lets a tile be
/// sized to a camera's own shape — a 4:3 tile among 16:9 ones — rather than only
/// to whole multiples of the default.
///
/// Rows are deliberately unbounded. A fixed row count means a camera past the
/// last cell has nowhere to go and is simply never drawn — no error, no gap,
/// just a camera that does not exist as far as the wall is concerned. Growing
/// downward and scrolling is what makes the wall hold whatever the Server has.
///
/// All of it is pure: no widgets, no pixels except [rowHeightFor], nothing to
/// pump. That is what lets the drop rules be tested as arithmetic rather than
/// as gestures.
abstract final class WallGrid {
  static const columns = 24;

  /// The standard tile — four across, and the shape [rowHeightFor] is built
  /// around. Its proportional multiples (12x4, 18x6, 24x8) come out within a gap
  /// of the same 16:9, the gaps being the only thing that does not scale.
  static const smallSpan = (columnSpan: 6, rowSpan: 2);

  /// The design's opening statement: the first camera on an empty wall is worth
  /// half the width and two standard tiles' height — a quarter of the visible
  /// wall.
  static const heroSpan = (columnSpan: 12, rowSpan: 4);

  /// How many rows the wall needs to draw [layout] — the lowest tile edge, plus
  /// one spare row.
  ///
  /// The spare row is not padding. It is the guarantee that there is always
  /// somewhere free to drop a tile, which is what keeps [move] from degrading
  /// into swap-only on a wall that happens to pack exactly.
  static int rowsFor(List<TileLayout> layout) {
    var lowest = 0;
    for (final tile in layout) {
      final edge = tile.row + tile.rowSpan;
      if (edge > lowest) lowest = edge;
    }
    return lowest + 1;
  }

  /// The lowest row a dropped tile's *top* may occupy: the first empty row.
  ///
  /// Deliberately about the top edge rather than the bottom. Bounding the
  /// bottom instead — `rowsFor - rowSpan` — stops a two-row tile a whole row
  /// short of where a one-row tile may go, which makes it impossible to stack
  /// two heroes: the row directly beneath them is the first empty row, and that
  /// is precisely the row a bottom-bound forbids.
  static int lastDropRow(List<TileLayout> layout) => rowsFor(layout) - 1;

  /// A row's height in pixels, derived from the cell width rather than from the
  /// viewport.
  ///
  /// Picked so a [smallSpan] tile is exactly 16:9. Deriving it from the width is also
  /// what makes the wall scale with the window instead of re-packing: every
  /// tile keeps its proportions and its place, and only the pixels change.
  ///
  /// A standard tile spans more than one row, so its height is that many rows
  /// *plus the gaps between them* — the gaps come out of the 16:9 budget before
  /// it is divided, or the tile ends up taller than the shape it is meant to be
  /// by a gap per row.
  static double rowHeightFor(double cellWidth, double gap) {
    final width =
        smallSpan.columnSpan * cellWidth + (smallSpan.columnSpan - 1) * gap;
    return (width * 9 / 16 - (smallSpan.rowSpan - 1) * gap) / smallSpan.rowSpan;
  }

  /// The arrangement read the way it is looked at: top to bottom, then left to
  /// right.
  ///
  /// What a phone keeps of the wall. A single stack of uniform tiles has no use
  /// for a span — six columns and twelve mean the same full width there — but
  /// the *sequence* the arrangement encodes is still the answer to "which camera
  /// do I want first", and it is the only thing on that screen that remembers a
  /// decision made on a desktop.
  static List<TileLayout> readingOrder(List<TileLayout> layout) =>
      List.of(layout)..sort((a, b) {
        final rows = a.row.compareTo(b.row);
        return rows != 0 ? rows : a.column.compareTo(b.column);
      });

  /// Whether two tiles want any of the same cells.
  static bool overlaps(TileLayout a, TileLayout b) =>
      a.column < b.column + b.columnSpan &&
      b.column < a.column + a.columnSpan &&
      a.row < b.row + b.rowSpan &&
      b.row < a.row + a.rowSpan;

  /// Pulls a candidate origin back onto the grid.
  ///
  /// Columns are bounded on both sides; rows only at the top, because down is
  /// where the wall grows. This is the whole of the "dragged past the edge"
  /// story — there is no separate case for it anywhere else.
  static (int column, int row) clamp(
    int column,
    int row,
    int columnSpan,
    int rowSpan,
  ) => (column.clamp(0, columns - columnSpan), row < 0 ? 0 : row);

  /// Whether every tile is on the grid and none of them overlap.
  static bool _valid(List<TileLayout> layout) {
    for (var i = 0; i < layout.length; i++) {
      final tile = layout[i];
      if (tile.column < 0 || tile.row < 0) return false;
      if (tile.column + tile.columnSpan > columns) return false;
      if (tile.columnSpan < 1 || tile.rowSpan < 1) return false;
      for (var j = i + 1; j < layout.length; j++) {
        if (overlaps(tile, layout[j])) return false;
      }
    }
    return true;
  }

  /// Moves [cameraId] to ([column], [row]). Null when the drop is refused.
  ///
  /// Free space wins outright. Otherwise the two regions **trade places**:
  /// every tile the dropped tile lands on slides by the same distance the drop
  /// travelled, in the opposite direction — which is to say, into the space the
  /// dropped tile has just vacated. A hero dropped onto the four small tiles
  /// below it therefore swaps with all four at once, rather than being refused
  /// for landing on more than one thing.
  ///
  /// This is one rule, not two: a single tile trading places is the same
  /// translation with one displaced tile, which is why it lands exactly on the
  /// dropped tile's old origin.
  ///
  /// A rigid trade does not always fit — a displaced tile can be wider than the
  /// hole it is being mirrored into, or straddle its edge. Rather than refuse
  /// the drop, the displaced tiles are then re-placed by [placeAll] into
  /// whatever the move frees up. They keep their sizes and only their positions
  /// are chosen for them, and nothing the drop did not land on is touched.
  ///
  /// So a move is refused only when there is nothing to do: an unknown camera,
  /// or a drop back exactly where the tile already was.
  ///
  /// Deliberately still never pushes or reflows tiles the drop did not land on:
  /// with mixed spans there is no order to cascade along that does not either
  /// run away or silently resize something.
  static List<TileLayout>? move(
    List<TileLayout> layout,
    String cameraId, {
    required int column,
    required int row,
  }) {
    final index = layout.indexWhere((t) => t.cameraId == cameraId);
    if (index < 0) return null;

    final tile = layout[index];
    final (c, r) = clamp(column, row, tile.columnSpan, tile.rowSpan);
    if (c == tile.column && r == tile.row) return null;

    final moved = tile.copyWith(column: c, row: r);
    final hit = [
      for (var i = 0; i < layout.length; i++)
        if (i != index && overlaps(layout[i], moved)) i,
    ];

    final next = [...layout];
    next[index] = moved;
    if (hit.isEmpty) return next;

    // The rigid trade first: it keeps the displaced tiles' arrangement intact,
    // which is what a swap ought to look like whenever it can.
    final byColumn = c - tile.column;
    final byRow = r - tile.row;
    for (final i in hit) {
      final displaced = layout[i];
      next[i] = displaced.copyWith(
        column: displaced.column - byColumn,
        row: displaced.row - byRow,
      );
    }
    if (_valid(next)) return next;

    // It did not fit, so re-home just those tiles around everything that stayed
    // put. `placeAll` always succeeds, so this cannot fail to produce a wall.
    final repacked = placeAll(
      [for (final i in hit) layout[i]],
      occupied: [
        moved,
        for (var i = 0; i < layout.length; i++)
          if (i != index && !hit.contains(i)) layout[i],
      ],
    );
    for (var k = 0; k < hit.length; k++) {
      next[hit[k]] = repacked[k];
    }

    return _valid(next) ? next : null;
  }

  /// Resizes [cameraId] from its own top-left. Null when the resize is refused.
  ///
  /// Spans clamp to at least one cell, and to the right-hand edge; downward is
  /// unbounded like everything else. A shrink is therefore always accepted — a
  /// smaller rect at the same origin cannot collide with anything new.
  static List<TileLayout>? resize(
    List<TileLayout> layout,
    String cameraId, {
    required int columnSpan,
    required int rowSpan,
  }) {
    final index = layout.indexWhere((t) => t.cameraId == cameraId);
    if (index < 0) return null;

    final tile = layout[index];
    final cs = columnSpan.clamp(1, columns - tile.column);
    final rs = rowSpan < 1 ? 1 : rowSpan;
    if (cs == tile.columnSpan && rs == tile.rowSpan) return null;

    final sized = tile.copyWith(columnSpan: cs, rowSpan: rs);
    for (var i = 0; i < layout.length; i++) {
      if (i != index && overlaps(layout[i], sized)) return null;
    }

    final next = [...layout];
    next[index] = sized;
    return next;
  }

  /// A saved arrangement read against the cameras that exist now.
  ///
  /// Tiles for cameras that are gone are dropped, and cameras the layout does
  /// not mention are appended — so adding a camera on the Server makes it
  /// appear on the wall rather than being invisible until the layout is reset.
  static List<TileLayout> reconcile(
    List<TileLayout> layout,
    List<String> cameraIds,
  ) {
    final known = cameraIds.toSet();
    final kept = [
      for (final tile in layout)
        if (known.contains(tile.cameraId)) tile,
    ];

    final placed = {for (final tile in kept) tile.cameraId};
    final missing = [
      for (final id in cameraIds)
        if (!placed.contains(id)) id,
    ];
    if (missing.isEmpty) return kept;

    return [...kept, ...packDefault(missing, occupied: kept)];
  }

  /// The design's shape: the first camera on an empty wall is a hero, the rest
  /// a standard tile each, filled left to right and then downward.
  ///
  /// Every camera handed in gets a tile: the row scan has no upper bound, so
  /// the wall never runs out of places to put one.
  static List<TileLayout> packDefault(
    List<String> cameraIds, {
    required List<TileLayout> occupied,
  }) => placeAll([
    for (var i = 0; i < cameraIds.length; i++)
      if (occupied.isEmpty && i == 0)
        TileLayout(
          cameraId: cameraIds[i],
          column: 0,
          row: 0,
          columnSpan: heroSpan.columnSpan,
          rowSpan: heroSpan.rowSpan,
        )
      else
        TileLayout(
          cameraId: cameraIds[i],
          column: 0,
          row: 0,
          columnSpan: smallSpan.columnSpan,
          rowSpan: smallSpan.rowSpan,
        ),
  ], occupied: occupied);

  /// Drops [tiles] into the first space that fits each, scanning left to right
  /// and then downward, around whatever [occupied] already holds.
  ///
  /// Each tile keeps its own span and only its position is chosen. The row scan
  /// has no upper bound and no tile can be wider than the grid, so this always
  /// places everything — which is what lets a move re-home the tiles it
  /// displaced rather than refusing the drop.
  ///
  /// Biggest first, because a hero left until last would find its corner
  /// already taken by a tile a quarter its size. Ties keep the order they were
  /// handed in, so the same wall always packs the same way.
  static List<TileLayout> placeAll(
    List<TileLayout> tiles, {
    required List<TileLayout> occupied,
  }) {
    final taken = <(int, int)>{};
    void take(TileLayout tile) {
      for (var c = tile.column; c < tile.column + tile.columnSpan; c++) {
        for (var r = tile.row; r < tile.row + tile.rowSpan; r++) {
          taken.add((c, r));
        }
      }
    }

    for (final tile in occupied) {
      take(tile);
    }

    bool free(int column, int row, int columnSpan, int rowSpan) {
      for (var c = column; c < column + columnSpan; c++) {
        for (var r = row; r < row + rowSpan; r++) {
          if (taken.contains((c, r))) return false;
        }
      }
      return true;
    }

    final order = [for (var i = 0; i < tiles.length; i++) i]
      ..sort((a, b) {
        final area =
            (tiles[b].columnSpan * tiles[b].rowSpan) -
            (tiles[a].columnSpan * tiles[a].rowSpan);
        return area != 0 ? area : a - b;
      });

    final placed = List<TileLayout?>.filled(tiles.length, null);
    for (final i in order) {
      final tile = tiles[i];
      for (var row = 0; ; row++) {
        var done = false;
        for (var column = 0; column + tile.columnSpan <= columns; column++) {
          if (!free(column, row, tile.columnSpan, tile.rowSpan)) continue;

          final at = tile.copyWith(column: column, row: row);
          placed[i] = at;
          take(at);
          done = true;
          break;
        }
        if (done) break;
      }
    }

    // Back in the order they were handed in, so a caller can line the results
    // up with what it asked for.
    return [for (final tile in placed) tile!];
  }
}
