import 'package:flutter/painting.dart' show Color;
import 'package:phosphor_icons/phosphor_icons.dart';

import '../models/activity.dart';
import '../theme/serval_tokens.dart';

/// The Phosphor glyph an activity row wears.
///
/// Its own file because two things draw it and neither owns it: the 26px
/// square leading a row in the feed, and the chips in the filter panel's *What
/// was seen or heard*. A person on a row and a person on a chip that were not
/// the same glyph would read as two different things being counted.
PhosphorIconData activityGlyph(ActivityIcon icon) => switch (icon) {
  ActivityIcon.person => PhosphorIconsFill.user,
  ActivityIcon.speech => PhosphorIconsFill.waveform,
  ActivityIcon.car => PhosphorIconsFill.car,
  ActivityIcon.cat => PhosphorIconsFill.cat,
  ActivityIcon.connectionLost => PhosphorIconsRegular.wifiSlash,
  ActivityIcon.garage => PhosphorIconsFill.garage,
  ActivityIcon.scene => PhosphorIconsFill.sparkle,
  ActivityIcon.dog => PhosphorIconsFill.dog,
  ActivityIcon.alarm => PhosphorIconsFill.bellRinging,
};

/// The face a speech row wears, where the audio model committed to one.
///
/// Here rather than in its own file for the same reason [activityGlyph] is here
/// at all: `models/` may not import Phosphor, and a second file holding one
/// switch would have to invent a weaker justification than this one's.
///
/// Fill throughout, matching [activityGlyph] — and load-bearing beyond taste,
/// since `golden_capture_test.dart` registers only the Regular and Fill faces
/// and anything else renders as tofu.
///
/// **Two of these are compromises, not matches.** Phosphor 3.0.1 has no
/// surprised face and no disgusted one, so `surprised` borrows the shocked
/// X-eyes and `disgusted` borrows the melting face. The alternatives —
/// `smileyBlank` and `smileyMeh` — both read as neutral, which is the one thing
/// deliberately not drawn. If either reads as the wrong feeling on screen, the
/// honest fix is to drop that member from [ActivityEmotion] and let it join
/// neutral in drawing nothing: a missing face costs a reader nothing, and a
/// face meaning the wrong thing costs them a wrong belief.
PhosphorIconData emotionGlyph(ActivityEmotion emotion) => switch (emotion) {
  ActivityEmotion.happy => PhosphorIconsFill.smiley,
  ActivityEmotion.sad => PhosphorIconsFill.smileySad,
  ActivityEmotion.angry => PhosphorIconsFill.smileyAngry,
  ActivityEmotion.fearful => PhosphorIconsFill.smileyNervous,
  ActivityEmotion.disgusted => PhosphorIconsFill.smileyMelting,
  ActivityEmotion.surprised => PhosphorIconsFill.smileyXEyes,
};

/// The one flat colour that face is drawn in.
///
/// A single colour per glyph, never a gradient or a two-tone: the hue is an
/// identifier, and the moment one face carries two of them it starts to look
/// like it is saying something the audio did not.
///
/// The hue does the work the glyph could not. These are filled discs whose
/// whole meaning is a few knocked-out pixels — at a glyph's size, monochrome,
/// the angry and the fearful face were indistinguishable on a real feed. Colour
/// separates six of them at a glance and the face then confirms which, which is
/// also what keeps the encoding usable for anyone who cannot separate two of the
/// hues: the glyph is still there and still different.
///
/// See [Serval.emotionAngry] and its neighbours for why this is a categorical
/// scale rather than a fourth status role.
Color emotionColor(ActivityEmotion emotion) => switch (emotion) {
  ActivityEmotion.happy => Serval.emotionHappy,
  ActivityEmotion.sad => Serval.emotionSad,
  ActivityEmotion.angry => Serval.emotionAngry,
  ActivityEmotion.fearful => Serval.emotionFearful,
  ActivityEmotion.disgusted => Serval.emotionDisgusted,
  ActivityEmotion.surprised => Serval.emotionSurprised,
};
