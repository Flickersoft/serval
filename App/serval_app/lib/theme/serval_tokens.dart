import 'package:flutter/widgets.dart';

import '../models/activity.dart';
import 'nocturne.dart';

/// The semantic colors the Serval design introduces on top of [Nocturne].
///
/// Nocturne is a mono scheme — one accent, no second hue — but a camera wall
/// has to say *something is happening here*, *this is being recorded* and
/// *this one is fine* in a glance, and none of those readings can borrow the
/// accent without competing with selection and focus. So the design adds three
/// chromatic roles, used the way Nocturne uses its accent: as a line, a dot and
/// a tint, never a flood. Everything else is a ground the system does not name.
abstract final class Serval {
  // ── grounds ──────────────────────────────────────────────────────────────

  /// The page behind the app frame, one step below [Nocturne.bg].
  static const canvas = Color(0xFF101220);

  /// The app frame itself. Same value as [Nocturne.bg], named for intent.
  static const panel = Nocturne.bg;

  /// The icon rail and the side panels — a hair bluer than the panel, which is
  /// what separates them without a border doing the work.
  static const rail = Color(0xFF13152A);

  /// Behind video, below every other ground so the image reads as inset.
  static const tile = Color(0xFF0F111C);

  /// A card floating over a panel — the activity column's filter overlay. One
  /// step above [rail] rather than below it, because a surface that lifts has
  /// to read as nearer than the thing it covers, and a shadow alone cannot say
  /// so against a ground this dark.
  static const overlay = Color(0xFF1B1D33);

  // ── attention ────────────────────────────────────────────────────────────

  /// Something needs you: the alert card, an alert detection's box, the alert
  /// marks on the timeline.
  ///
  /// Warm is the claim that someone should look, and only two hues are warm —
  /// this and [alertSound], which is the same claim about something heard
  /// rather than seen. Within warm, hue says *which*; nothing outside warm may
  /// borrow either, because a hue that also marked the wall's focus or every
  /// object the detector could name would be a claim about nothing. Focus is
  /// [Nocturne.accent]'s job; an object the operator did not ask to be told
  /// about is drawn nowhere at all.
  static const alert = Color(0xFFE0955F);

  /// An alert the camera *heard* — a sound with `is_alert`, on the timeline.
  ///
  /// Warm, so it still reads as a summons at a glance, and lighter and yellower
  /// than [alert], so the summons also says whether to expect a person in the
  /// picture or something off-frame. The two sit closer together than any other
  /// pair the design names: that is deliberate — they are one family and the
  /// distinction inside it is the smaller of the two readings.
  static const alertSound = Color(0xFFE8C85A);

  /// Alert text on an alert tint — the light step of the same hue, for the
  /// same reason Nocturne reads body copy off `accent300` rather than the
  /// accent itself.
  static const alertText = Color(0xFFF0B49A);

  /// Recording. Only ever a dot, a hairline or a 2px marker.
  static const recording = Color(0xFFE05A4F);

  /// Recording text on a recording tint.
  static const recordingText = Color(0xFFF0B49A);

  /// Nothing is wrong: the settings screen's *Healthy* pill and the green dot
  /// beside a camera in the registry list. Green is the one reading the other
  /// two roles cannot carry — [alert] and [recording] are both warm, so a
  /// working camera would otherwise have to be signalled by the absence of
  /// something, which is not a signal.
  static const healthy = Color(0xFF7FBF9A);

  /// Healthy text on a healthy tint, the same light-step rule as [alertText].
  static const healthyText = Color(0xFFA8DCC0);

  // ── how a voice sounded ──────────────────────────────────────────────────
  //
  // These are a *categorical* scale, not a fourth status role, and the
  // distinction is what keeps the three-role rule above intact. A role answers
  // "does this need me?" — there are three because there are three answers. A
  // category answers "which of these is it?", where the hue is an identifier
  // and carries no urgency at all: `emotionAngry` is not a louder claim than
  // `emotionSad`, it is a different one.
  //
  // Drawn monochrome first, and that was wrong. These glyphs are a filled disc
  // whose entire meaning is a few knocked-out pixels of eye and mouth — unlike
  // a letterform, whose shape a reader already knows — so at a glyph's size the
  // angry face and the fearful one were the same grey blob. Measured on a real
  // feed, not guessed. Hue is what makes six of them tell apart at a glance;
  // the glyph then confirms which.
  //
  // One lightness band and one chroma band across all six, so no emotion shouts
  // over its neighbours and a row full of them still reads as one row. Light
  // steps rather than base hues for the same reason [alertText] is: they sit on
  // a dark ground at a glyph's size, where a base hue goes muddy.
  //
  // Deliberately *not* [alert] or [recording]. Both are spoken for, and an
  // angry voice borrowing the hue that means "someone should look" would turn a
  // description of a conversation into a summons.
  static const emotionAngry = Color(0xFFF0938A);
  static const emotionSad = Color(0xFF8FB6E0);
  static const emotionFearful = Color(0xFFC9AEF0);
  static const emotionDisgusted = Color(0xFFB6CE8E);
  static const emotionSurprised = Color(0xFFF0CE8A);

  /// Happy borrows [healthyText] outright rather than adding a seventh value:
  /// it is the same green at the same step, and two greens a shade apart would
  /// read as a mistake rather than as a distinction.
  static const emotionHappy = healthyText;

  // ── what happened ────────────────────────────────────────────────────────
  //
  // The timeline's four bands, one per [ActivityKind]. A categorical scale for
  // the same reason the emotions above are one: the hue is an identifier and
  // carries no urgency — a sound is not a louder claim than a scene, it is a
  // different one. Urgency on the track is still [alert] and [alertSound],
  // which are drawn as their own layers over these.
  //
  // Cool, all four, so the warm pair keeps the whole of "someone should look"
  // to itself. Evenly spaced across the cool half — teal, purple, blue, and a
  // slate that is barely a hue at all — because the bar draws them touching,
  // and two neighbours a few degrees apart would read as one smeared band. One
  // lightness and chroma band across the set, so no kind shouts over another on
  // a track that is often all four at once.
  //
  // These fill at 55% against a ground that is 5%, the same weight the single
  // band always had. Read them through [markHue] rather than by name, so the
  // scrubber, the trim track and the clip strip cannot disagree about what a
  // colour means.

  /// Something the camera heard that was not speech.
  ///
  /// Teal rather than the green of [healthy], which it is closest to: that
  /// green is a status dot in the registry and the settings screen and never
  /// appears on a timeline, so the two never share a surface.
  static const markSound = Color(0xFF56C7B4);

  /// An object the detector named.
  ///
  /// The accent, unchanged — this is what the single band was, and objects are
  /// the kind an operator scans the track for most.
  static const markObject = Nocturne.accent;

  /// Someone talking: an utterance, or the transcript that settles it.
  static const markSpeech = Color(0xFF6BA8E8);

  /// What the vision model was asked to say about a frame.
  ///
  /// Desaturated on purpose. A scene is a description *of* something else — it
  /// nearly always shares its instant with the detection that triggered it —
  /// so it is the one kind whose job is to be the ground the others sit on.
  static const markScene = Color(0xFF7C819E);

  /// The hue for a mark of [of], severe or not.
  ///
  /// The whole mapping in one place: six colours, and the two questions that
  /// pick between them. Alerts collapse to two because only sounds and
  /// detections carry `is_alert` — a scene's trigger and an utterance's words
  /// describe what happened, never that it mattered — so an alert is either
  /// heard or seen and there is no third warm hue to want.
  static Color markHue(ActivityKind of, {required bool alert}) {
    if (alert) return of == ActivityKind.sounds ? alertSound : Serval.alert;

    return switch (of) {
      ActivityKind.sounds => markSound,
      ActivityKind.objects => markObject,
      ActivityKind.speech => markSpeech,
      ActivityKind.scenes => markScene,
    };
  }

  // ── the frame ────────────────────────────────────────────────────────────

  /// The design is drawn at 1440x900. The rail, the activity column and the
  /// right panel hold these widths; everything else flexes.
  static const designWidth = 1440.0;
  static const designHeight = 900.0;

  static const railWidth = 64.0;
  static const activityColumnWidth = 376.0;
  static const detailPanelWidth = 372.0;

  /// What either of those two shrinks to when collapsed — wide enough for the
  /// chevron that brings it back and nothing else, so the panel can give up its
  /// room without giving up the way back to it.
  static const activityRailWidth = 40.0;

  /// The sheet's resting height on the wall: the search field reachable, one
  /// event under it, and the wall still the subject.
  ///
  /// Also the most the wall's bottom padding is ever set to, so the last tile
  /// can be scrolled clear of a resting sheet. The padding follows the sheet
  /// *below* this — a stowed sheet gives the room back — but never above it,
  /// and never the live drag: content that resized as the sheet moved would
  /// fight the finger.
  static const activitySheetResting = 236.0;

  /// What the tray keeps when it is pushed all the way down: the handle, and
  /// one line naming what is behind it.
  ///
  /// Everything else — the camera's controls, the search field, the feed — goes
  /// off the bottom of the screen. That is the height a portrait camera wants,
  /// where a 9:16 scene letterboxed into the room a peek leaves is still a
  /// fraction of what the phone could show it at.
  static const activitySheetStowed = 52.0;

  /// How much of the row behind the raised sheet stays visible.
  ///
  /// The raised detent is derived from this rather than pinned: one whole tile
  /// row above the sheet and the next one peeking is what says the wall is still
  /// there, and it has to stay true at two columns and in landscape.
  static const activitySheetPeek = 52.0;

  /// The least of the screen the raised sheet ever takes, whatever the tile
  /// rhythm above it works out to.
  ///
  /// The design's own proportion, and a floor rather than the figure: a portrait
  /// camera at the top of the wall is taller than the room above the resting
  /// sheet, so following the tiles alone would leave *raised* barely higher than
  /// *resting* and the gesture would do nothing.
  static const activitySheetRaisedShare = 0.58;

  /// Where the filter sheet stops. Near full height, and deliberately short of
  /// it — a sheet that covered the wall would be a screen, and the wall going
  /// away quietly is the one thing a security app should not do.
  static const activitySheetFilterTop = 96.0;

  /// What a tray always leaves behind it, below which the wall stops being a
  /// wall and the tray stops being over anything.
  ///
  /// Zero on the single-camera screen, which is the one place a tray may take
  /// the room outright: you asked for that camera, the picture is still behind
  /// the tray, and three separate gestures bring it straight back.
  static const activitySheetFloor = 96.0;

  /// The picture's share of a phone. The camera sends 16:9 and a portrait phone
  /// is 9:19.5, so the frame can only ever be a band across the top and the rest
  /// of the screen is the real design.
  static const pictureAspect = 16 / 9;

  /// The width below which the App drills down — settings index, then a list,
  /// then one record — instead of laying those columns side by side.
  ///
  /// Design 7b, and the figure is what the columns actually cost: the rail, the
  /// settings sidebar and the camera list are 572px between them, and an editor
  /// needs [kPairedMinWidth]'s neighbourhood again before it holds a form. A
  /// window that cannot give all four their room has to give one of them the
  /// screen instead, and this is where that starts.
  static const compactWidth = 950.0;

  /// The height below which a window is a phone lying on its side rather than a
  /// short desktop one. A rotated phone is 412 tall; a desktop window somebody
  /// has squashed is not, and must not lose its chrome to a full-bleed picture.
  static const compactHeight = 560.0;

  /// Panel borders and the hairlines inside them, both derived from the text
  /// color the way the stylesheet derives `--color-divider`.
  static Color get panelBorder => Nocturne.mix(Nocturne.text, 10);
  static Color get hairline => Nocturne.mix(Nocturne.text, 7);
}

/// Whether this build of the App is drawing the drill-down rather than the
/// columns.
///
/// The *window's* width, not the pane's: a pane is narrow because the window
/// is, and a screen that measured itself would answer differently inside the
/// shell than outside it. One figure, read one way, so every screen swaps at
/// the same moment — the same reason `kPairedMinWidth` is a single constant.
bool isCompact(BuildContext context) =>
    MediaQuery.sizeOf(context).width < Serval.compactWidth;

/// Whether the window is a phone that has been turned on its side.
///
/// Landscape is a real mode rather than a reflow: the camera is horizontal, so
/// turning the phone takes the frame from 412x232 to 876x396 — nearly four times
/// the picture — and the picture is then worth the whole screen. Both dimensions
/// are tested because a squashed desktop window is also wider than it is tall,
/// and it has chrome that must survive.
bool isPhoneLandscape(BuildContext context) {
  final size = MediaQuery.sizeOf(context);
  return size.width < Serval.compactWidth &&
      size.height < Serval.compactHeight &&
      size.width > size.height;
}
