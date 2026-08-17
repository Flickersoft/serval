import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart'
    show PopupMenuButton, PopupMenuItem, PopupMenuPosition;
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/audio_levels_socket.dart';
import '../data/byte_labels.dart';
import '../data/camera_record.dart';
import '../models/ptz.dart';
import '../playback/playback_volume.dart';
import '../models/server_camera_defaults.dart';
import '../models/system_stats.dart';
import '../screens/cameras_screen.dart' show SaveFailureNote;
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'audio_level_meter.dart';
import 'nocturne_button.dart';
import 'camera_tuning_sections.dart';
import 'compact_app_bar.dart';
import 'dashed_border.dart';
import 'link_text.dart';
import 'mask_preview.dart';
import 'nocturne_field.dart';
import 'nocturne_select.dart';
import 'nocturne_slider.dart';
import 'nocturne_toggle.dart';
import 'paired_rows.dart';
import 'role_chip.dart';
import 'settings_cards.dart';
import 'status_indicators.dart';

/// The camera editor's sections, in the order the index lists them.
///
/// **A section that overrides a Server group carries that group's name, exactly.** Six do —
/// *Streams*, *Recording*, *Objects & alerts*, *Motion detection*, *Speech & transcription* and
/// *Sound recognition* — so somebody moving between this page and *Server settings* is reading one
/// vocabulary rather than two, and `settingsGroupIcon` can key both off the one name. Renaming
/// either side without the other is what undoes it; `SettingsCatalog.Groups` says the same thing
/// from the other end.
///
/// Three sections have no Server counterpart and are named for themselves: *General*,
/// *Masks & zones* (polygons are per-camera and have no catalogue entry at all) and *Playback*.
///
/// *Camera control* is the one deliberate mismatch. The Server's *Pan, tilt & zoom* group holds PTZ
/// timeouts; this section holds the ONVIF connection **and** two-way audio, which rides the WebRTC
/// session and is not PTZ at all — so borrowing that name would misname the talk-back switch.
enum CameraSection {
  general('General', 'What this camera is called and where it lives.'),
  streams(
    'Streams',
    'Most cameras send a sharp stream and a smaller one. Tell Serval what each is for. A stream '
        'with no jobs is kept and simply not pulled, so a spare address can sit here unused.',
  ),
  recording(
    'Recording',
    'Whether anything is written to disk, how far back you can go, and whether sound is saved '
        'with the picture.',
  ),
  cameraControl(
    'Camera control',
    'What you can do to this camera from its own feed. Talking back rides the video stream; '
        'moving it needs the separate control connection.',
  ),
  masks(
    'Masks & zones',
    'Areas of this camera’s view Serval ignores. Recording is untouched.',
  ),
  analysis(
    'Analysis',
    'Which of the Server’s two analysers run on this camera. Each one’s own settings are in the '
        'section named after it.',
  ),
  objects(
    'Objects & alerts',
    'Which things this camera writes down, which are worth a description, and which are worth an '
        'alert.',
  ),
  motion(
    'Motion detection',
    'Used when this Server is not looking for objects — it compares each frame to the last '
        'instead. A camera facing a tree needs a higher setting than one facing a hallway.',
  ),
  speech(
    'Speech & transcription',
    'How loud something has to be before this camera transcribes it, and how sure Serval must be '
        'that it is a voice. Set them against what the meter actually shows, not against a guess.',
  ),
  sound(
    'Sound recognition',
    'A drive wants vehicles and breaking glass. A nursery wants crying and the smoke alarm, and '
        'emphatically not every car that passes. One list for the whole house serves both badly.',
  ),
  playback(
    'Playback',
    'How this camera sounds when you listen back to it. Nothing here changes what is recorded or '
        'what Serval notices.',
  );

  const CameraSection(this.title, this.blurb);

  final String title;

  /// What the section is for, in a sentence, under its name.
  ///
  /// The Server settings page has no equivalent and needs none — its catalogue explains every field
  /// individually. Half of these sections are not made of catalogue fields at all, so the sentence
  /// is the only thing that says what a stream or a control address is doing here.
  final String blurb;
}

/// The right-hand two-thirds of design 2a: one camera's whole record, editable.
///
/// The form works on a **copy** of the camera and sends the copy whole.
/// `PUT /api/cameras/{id}` replaces rather than merges — "send every field you want kept" — so
/// there is no patch to assemble, and the ONVIF password read back by `GET` has to be carried
/// along untouched or saving a change of name would delete the camera's credentials.
class CameraSettingsForm extends StatefulWidget {
  const CameraSettingsForm({
    super.key,
    required this.record,
    required this.creating,
    required this.health,
    required this.knownLocations,
    required this.existingIds,
    required this.onSave,
    this.onDelete,
    this.onDiscard,
    this.onBack,
    this.watchAudioLevels,
    this.deviceInformation,
    this.diskUsage,
    this.defaults = ServerCameraDefaults.unknown,
    this.onEditMasks,
    this.onOpenServerSettings,
    this.frameFor,
    this.readOnly = false,
  });

  final CameraRecord record;

  /// Adding rather than editing: the id becomes editable and *Save* creates.
  final bool creating;

  /// Show the camera's settings without offering to change any of them.
  ///
  /// True for every role below Admin, because every camera write — create, update, delete — is
  /// Admin-only on the Server. Without this the page reads as editable and each attempt comes back
  /// 403, which is the Server doing its job and the screen failing to do its own.
  ///
  /// There is a second reason, particular to this form. It carries the whole record through a save,
  /// including an ONVIF password it cannot read back (see the class docs above) — and a Viewer is
  /// sent that password redacted. So a save from a Viewer would post a blank credential over a real
  /// one. The Server's 403 stops that today; this stops the form from ever assembling it.
  final bool readOnly;

  final CameraHealth health;

  /// Locations already in use, offered under *Where it is*.
  final List<String> knownLocations;

  /// So a new camera cannot claim an id the registry already has — the Server would reject it,
  /// but saying so here is cheaper than a round trip.
  final Set<String> existingIds;

  final Future<void> Function(CameraRecord) onSave;
  final VoidCallback? onDelete;

  /// Abandons a camera being added. Null while editing, where *Discard* reverts instead.
  final VoidCallback? onDiscard;

  /// Leaves this record for the list it came from — the drill-down's back arrow. Only ever drawn
  /// below [Serval.compactWidth], where the form is the whole screen rather than a pane beside the
  /// list it would return to.
  final VoidCallback? onBack;

  /// Opens a live input-level feed for a camera, or null where there is no Server to open one
  /// against.
  ///
  /// A callback rather than the repository itself, so this form keeps depending on nothing but
  /// its record. The form owns what it opens: the feed's lifetime is this panel's, because the
  /// Server measures a camera's level only while somebody is subscribed.
  final AudioLevelFeed? Function(String cameraId)? watchAudioLevels;

  /// What the camera says it is, or null while unread — and for a camera with no ONVIF endpoint,
  /// which is the only thing there is to ask. A value rather than a callback because it is read
  /// once for the subtitle rather than owned for a session.
  final DeviceInformation? deviceInformation;

  /// What this camera is holding on disk, or null on a Server that publishes no vitals or has the
  /// per-camera walk switched off. Read under *Keeping footage*, beside the slider that decides
  /// it — see [_retention] for why that is the only place it belongs.
  final CameraDiskUsage? diskUsage;

  /// What this camera falls back on while it overrides nothing: the Server's catalogue, which also
  /// supplies every tuning card's label, sentence and bounds.
  /// [ServerCameraDefaults.unknown] where the Server's settings could not be read — the form still
  /// works, it simply cannot name what a field falls back to.
  final ServerCameraDefaults defaults;

  /// Opens the mask editor on this camera. Null where there is nowhere to open it — a form pumped
  /// outside a router, or a camera being added, which has no frame to draw on yet.
  final VoidCallback? onEditMasks;

  /// Follows the *Server settings* link under the index. Null leaves it as plain text rather than
  /// a link to nowhere.
  final VoidCallback? onOpenServerSettings;

  /// The latest frame from a camera, for the mask preview. A callback rather than the repository,
  /// so this form still depends on nothing but its record.
  final Uint8List? Function(String cameraId)? frameFor;

  /// Everything that differs between the record as loaded and the record as edited, named the way
  /// the footer says it.
  ///
  /// **Every editable field needs a line here, and the cost of forgetting one is not a wrong
  /// message.** An empty list is what disables *Save camera*, so a field missing from this list
  /// can be edited, holds its value, and then cannot be saved at all — behind a footer claiming
  /// everything already matches the Server. The detection, sound and movement sections shipped
  /// that way for exactly as long as it took to open the real app.
  ///
  /// On the widget rather than the state so it can be exercised directly; it is a pure comparison
  /// and needs nothing pumped.
  ///
  /// Keyed by section so the index can mark which one an unsaved change is in without a second
  /// list to keep in step with this one. [changesBetween] is this, flattened.
  static Map<CameraSection, List<String>> changesBySection(
    CameraRecord before,
    CameraRecord after,
  ) => {
    CameraSection.general: [
      if (before.name != after.name) 'the name',
      if (before.location != after.location) 'where it is',
      if (before.enabled != after.enabled)
        after.enabled ? 'it was switched on' : 'it was switched off',
    ],
    CameraSection.streams: [if (_streamsChanged(before, after)) 'the streams'],
    CameraSection.recording: [
      if (before.recording != after.recording)
        after.recording
            ? 'recording was switched on'
            : 'recording was switched off',
      if (before.retentionDays != after.retentionDays)
        'how long footage is kept',
      if (before.recordAudio != after.recordAudio) 'recording audio',
    ],
    CameraSection.cameraControl: [
      if (before.twoWayAudio != after.twoWayAudio) 'two-way audio',
      if (_onvifChanged(before, after)) 'the pan and tilt connection',
    ],
    // Masks live inside `detectionTuning` but are edited on their own screen, so they are counted
    // apart from it — otherwise drawing a polygon would light up *Objects & alerts*.
    CameraSection.masks: [if (_masksChanged(before, after)) 'the masks'],
    CameraSection.analysis: [
      if (before.aiVision != after.aiVision) 'scene descriptions',
      if (before.aiAudio != after.aiAudio) 'audio analysis',
    ],
    CameraSection.objects: [
      if (_withoutMasks(before.detectionTuning) !=
          _withoutMasks(after.detectionTuning))
        'what it looks for',
    ],
    CameraSection.motion: [
      if (before.motionTuning != after.motionTuning) 'motion detection',
    ],
    // The audio tuning bag carries all three thresholds, but they are edited in two different
    // sections now, so it is compared a field at a time. Comparing the bag whole would light up
    // *Speech & transcription* for a change made in *Sound recognition*.
    CameraSection.speech: [
      if (before.audioTuning?.speechGateRmsThreshold !=
              after.audioTuning?.speechGateRmsThreshold ||
          before.audioTuning?.vadThreshold != after.audioTuning?.vadThreshold)
        'how it listens for speech',
    ],
    CameraSection.sound: [
      if (before.audioTuning?.soundGateRmsThreshold !=
          after.audioTuning?.soundGateRmsThreshold)
        'how it listens for sounds',
      if (before.soundTuning != after.soundTuning) 'which sounds matter',
    ],
    // Counted apart from the thresholds that used to sit beside them, because they are a different
    // claim: those change what the detector notices, these change what you hear.
    CameraSection.playback: [
      if (before.playbackGainDb != after.playbackGainDb) 'the starting volume',
      if (before.playbackGateRms != after.playbackGateRms)
        'what counts as silence when you listen',
    ],
  };

  static List<String> changesBetween(CameraRecord before, CameraRecord after) =>
      [for (final named in changesBySection(before, after).values) ...named];

  /// The tuning with its masks taken out, collapsed to null when nothing else is set — so a camera
  /// whose only override is a mask compares equal to one with no overrides at all.
  static DetectionTuningSettings? _withoutMasks(DetectionTuningSettings? it) {
    if (it == null) return null;
    final stripped = it.copyWith(masks: null);
    return stripped.isEmpty ? null : stripped;
  }

  static bool _masksChanged(CameraRecord before, CameraRecord after) {
    final a = before.detectionTuning?.masks ?? const [];
    final b = after.detectionTuning?.masks ?? const [];
    if (a.length != b.length) return true;
    for (var i = 0; i < a.length; i++) {
      if (a[i] != b[i]) return true;
    }
    return false;
  }

  static bool _streamsChanged(CameraRecord before, CameraRecord after) {
    if (before.streams.length != after.streams.length) return true;
    for (var i = 0; i < before.streams.length; i++) {
      final a = before.streams[i];
      final b = after.streams[i];
      if (a.name != b.name ||
          a.url != b.url ||
          a.transcode?.codec != b.transcode?.codec ||
          a.transcode?.bitrate != b.transcode?.bitrate ||
          a.roles.length != b.roles.length ||
          !a.roles.every(b.roles.contains)) {
        return true;
      }
    }
    return false;
  }

  static bool _onvifChanged(CameraRecord before, CameraRecord after) =>
      before.onvifUrl != after.onvifUrl ||
      before.onvifUsername != after.onvifUsername ||
      before.onvifPassword != after.onvifPassword ||
      before.onvifProfileToken != after.onvifProfileToken;

  @override
  State<CameraSettingsForm> createState() => _CameraSettingsFormState();
}

class _CameraSettingsFormState extends State<CameraSettingsForm> {
  late CameraRecord _edited;

  late final TextEditingController _id;
  late final TextEditingController _name;
  late final TextEditingController _location;
  late final TextEditingController _onvifUrl;
  late final TextEditingController _onvifUsername;
  late final TextEditingController _onvifPassword;
  late final TextEditingController _onvifProfile;

  /// True once the password field has been typed into. Until then the stored value is re-sent
  /// verbatim, which is the whole of the "masked, replace-only" behaviour.
  bool _passwordReplaced = false;

  final _search = TextEditingController();

  /// Which section was clicked, not which one is on screen — see [_openSection].
  CameraSection? _clickedSection;

  bool _saving = false;
  Object? _failure;

  /// The Server's default when a camera sets none. Only used to place the slider — a camera that
  /// has not overridden retention keeps `null` and inherits whatever the Server is configured
  /// for, which is not something this form should silently freeze into a number.
  static const _defaultRetentionDays = 7;
  static const _maxRetentionDays = 90;

  /// Where the meter draws its amber line for a camera that overrides neither gate.
  ///
  /// The Server's own defaults, and only ever a starting position — the cards themselves read the
  /// real value out of the catalogue, so these place a line on a picture and never a claim about
  /// what is in force.
  static const _defaultSpeechGate = 0.01;
  static const _defaultSoundGate = 0.01;
  static const _defaultVadThreshold = 0.5;

  /// Where the playback gate's slider sits before one is set — about -64 dBFS.
  ///
  /// Unlike the three above this is not a Server default, because the Server has none: an unset gate
  /// gates nothing. It is a starting position picked to land a little above the resting level of a
  /// typical camera, so the first drag is a nudge rather than a hunt across a log track.
  static const _defaultPlaybackGate = 0.0006;

  /// Opened while this panel is on screen and closed with it. Null when there is no Server.
  AudioLevelFeed? _levels;

  @override
  void initState() {
    super.initState();
    _edited = widget.record;

    // Only for an existing camera: one being added has no id to subscribe to yet.
    if (!widget.creating) {
      _levels = widget.watchAudioLevels?.call(widget.record.id);
    }

    _id = TextEditingController(text: _edited.id);
    _name = TextEditingController(text: _edited.name);
    _location = TextEditingController(text: _edited.location ?? '');
    _onvifUrl = TextEditingController(text: _edited.onvifUrl ?? '');
    _onvifUsername = TextEditingController(text: _edited.onvifUsername ?? '');
    // Never the real value. It is carried in `_edited` and only replaced if typed over.
    _onvifPassword = TextEditingController(
      text: (_edited.onvifPassword ?? '').isEmpty ? '' : '••••••••',
    );
    _onvifProfile = TextEditingController(
      text: _edited.onvifProfileToken ?? '',
    );
  }

  @override
  void dispose() {
    // Closing is what tells the Server to stop measuring this camera. Leaving it open would keep
    // an RMS pass and a ten-per-second publish running for a panel nobody is looking at.
    _levels?.close();

    for (final controller in [
      _search,
      _id,
      _name,
      _location,
      _onvifUrl,
      _onvifUsername,
      _onvifPassword,
      _onvifProfile,
    ]) {
      controller.dispose();
    }
    super.dispose();
  }

  void _update(CameraRecord Function(CameraRecord) change) =>
      setState(() => _edited = change(_edited));

  // ------------------------------------------------------------------ saving

  /// The record as it would go on the wire, with the text fields folded in.
  CameraRecord get _assembled {
    final location = _location.text.trim();
    final onvifUrl = _onvifUrl.text.trim();
    final username = _onvifUsername.text.trim();
    final profile = _onvifProfile.text.trim();

    return _edited.copyWith(
      id: widget.creating ? _id.text.trim() : _edited.id,
      name: _name.text.trim(),
      location: location.isEmpty ? null : location,
      clearLocation: location.isEmpty,
      onvifUrl: onvifUrl.isEmpty ? null : onvifUrl,
      onvifUsername: username.isEmpty ? null : username,
      onvifPassword: _passwordReplaced
          ? (_onvifPassword.text.isEmpty ? null : _onvifPassword.text)
          : _edited.onvifPassword,
      onvifProfileToken: profile.isEmpty ? null : profile,
      // Collapsed the way the Server collapses it on save, so an object whose last threshold was
      // cleared does not read as an unsaved change that would not survive the round trip.
      clearAudioTuning: _edited.audioTuning?.isEmpty ?? false,
    );
  }

  /// Why saving is not possible yet, or null.
  ///
  /// The role rules are the Server's, mirrored — see [CameraRecord.roleProblem]. Checking them
  /// here is what lets *Save camera* be disabled with the reason on screen instead of posting a
  /// request that comes back 400.
  String? get _problem {
    final assembled = _assembled;

    if (widget.creating) {
      final id = assembled.id;
      if (id.isEmpty) return 'Give the camera an id.';
      if (!RegExp(r'^[A-Za-z0-9_-]+$').hasMatch(id)) {
        // It becomes a directory name and a URL segment, which is why the Server constrains it.
        return 'An id can only use letters, digits, “-” and “_”.';
      }
      if (widget.existingIds.contains(id)) {
        return 'A camera with the id “$id” already exists.';
      }
    }

    if (assembled.name.isEmpty) return 'Give the camera a name.';

    return assembled.roleProblem;
  }

  /// What the save bar names as unsaved, and what the index marks with a dot.
  Map<CameraSection, List<String>> get _changesBySection =>
      CameraSettingsForm.changesBySection(widget.record, _assembled);

  List<String> get _changes =>
      CameraSettingsForm.changesBetween(widget.record, _assembled);

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _failure = null;
    });

    try {
      await widget.onSave(_assembled);
    } on Object catch (error) {
      if (mounted) setState(() => _failure = error);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  // ------------------------------------------------------------------- build

  @override
  Widget build(BuildContext context) {
    final compact = isCompact(context);
    final gutter = compact ? 18.0 : 24.0;
    final changes = _changesBySection;
    final open = _openSection(compact);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (compact) ..._compactHeader(open) else _header,
        Expanded(child: _body(compact, gutter, open, changes)),
        if (widget.readOnly)
          SettingsSaveBar.viewOnly(what: 'A camera', compact: compact)
        else
          _saveBar(compact, open),
      ],
    );
  }

  /// The index beside the section it opens — the Server settings page's shape, reached through the
  /// same widgets.
  ///
  /// Navigated rather than one scroll of nine sections: a form you scroll cannot gain *Masks &
  /// zones* without getting longer, where a form you navigate can. It also keeps the camera page
  /// and the Server settings page on one idiom.
  Widget _body(
    bool compact,
    double gutter,
    CameraSection? open,
    Map<CameraSection, List<String>> changes,
  ) {
    final sections = _matchingSections;

    final index = SettingsGroupList(
      groups: [for (final section in sections) section.title],
      counts: {
        for (final section in sections)
          section.title: _labelsIn(section).length,
      },
      changed: {
        for (final section in sections)
          if (changes[section]?.isNotEmpty ?? false) section.title,
      },
      // A camera has nothing that waits for a restart: saving one restarts its own session
      // outright, so there is no state between stored and in use to mark.
      restartPending: const {},
      selected: open?.title,
      search: _search,
      searchPlaceholder: 'Search this camera',
      onSearchChanged: () => setState(() {}),
      onSelect: (title) => setState(
        () => _clickedSection = sections.firstWhere((s) => s.title == title),
      ),
      compact: compact,
    );

    // Said once, under the index, rather than at the head of every section: it is one fact about
    // the whole page, and repeating it per section is how a form teaches people to stop reading.
    //
    // The width is this column's rather than the list's: a stretched Column inside a Row is handed
    // an unbounded cross axis, and `SettingsGroupList`'s own 252px is inside it rather than around
    // it.
    final indexColumn = SizedBox(
      width: compact ? null : 252,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Expanded(child: index),
          if (!compact) ...[
            const SizedBox(height: 10),
            _InheritanceNote(onOpenServerSettings: widget.onOpenServerSettings),
          ],
        ],
      ),
    );

    // Only the pane goes inert. The index beside it is navigation, not editing — a Viewer reading
    // why a camera behaves the way it does still has to be able to walk its sections.
    final pane = ReadOnlyPane(
      readOnly: widget.readOnly,
      child: Padding(
        padding: EdgeInsets.fromLTRB(gutter, 16, gutter, 0),
        child: open == null
            ? SettingsNoMatches(
                query: _search.text.trim(),
                empty: 'This camera lists no settings.',
              )
            : _SectionPane(
                section: open,
                count: _labelsIn(open).length,
                changed: changes[open]?.length ?? 0,
                showTitle: !compact,
                builder: (paneWidth) => _sectionBody(open, paneWidth),
              ),
      ),
    );

    if (compact) {
      return open == null
          ? Padding(
              padding: const EdgeInsets.fromLTRB(18, 14, 18, 8),
              child: indexColumn,
            )
          : pane;
    }

    return Row(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 16, 0, 12),
          child: indexColumn,
        ),
        Expanded(child: pane),
      ],
    );
  }

  /// The sections a search leaves. Matched on the section's own name and on the labels of the
  /// settings inside it, so both the person looking for *Masks* and the one looking for
  /// *Confidence floor* land somewhere.
  List<CameraSection> get _matchingSections {
    final query = _search.text.trim().toLowerCase();
    if (query.isEmpty) return CameraSection.values;

    return [
      for (final section in CameraSection.values)
        if (section.title.toLowerCase().contains(query) ||
            _searchTermsIn(section).any((l) => l.toLowerCase().contains(query)))
          section,
    ];
  }

  /// What a search matches on: the section's settings, plus words for the things that are not
  /// settings and would otherwise be unfindable.
  ///
  /// Separate from [_labelsIn] because that one is also the section's *count*, and a search keyword
  /// is not a setting — folding the two together made *Masks & zones* report four settings when it
  /// holds one list.
  List<String> _searchTermsIn(CameraSection section) => [
    ..._labelsIn(section),
    ...switch (section) {
      CameraSection.masks => const ['zones', 'ignore', 'polygon', 'area'],
      CameraSection.general => const ['name', 'location', 'id'],
      CameraSection.cameraControl => const [
        'onvif',
        'ptz',
        'pan',
        'tilt',
        'zoom',
      ],
      CameraSection.playback => const ['volume', 'listen', 'gain'],
      _ => const <String>[],
    },
  ];

  /// What a section calls its settings, for the search to match on, and what its index row counts.
  ///
  /// **Drawn from the catalogue wherever there is one**, so searching finds the Server's word for a
  /// thing rather than a copy of it that has since drifted. The audio sections used to be the
  /// exception and the reason this needed saying: they drew hand-written labels — *Hear speech
  /// above*, *Notice sounds above* — while listing the catalogue's here, so the one word on screen
  /// was the one word that found nothing. Both now draw catalogue cards like every other tuning
  /// section, and there is nothing left to keep in step.
  List<String> _labelsIn(CameraSection section) => switch (section) {
    CameraSection.general => const ['Name', 'Location', 'ID', 'Camera enabled'],
    CameraSection.streams => const [
      'Stream name',
      'Address',
      'Used for',
      'Re-encode',
      'Codec',
    ],
    CameraSection.recording => [
      'Record this camera',
      widget.defaults[CameraSetting.retentionDays].label,
      'Record audio',
    ],
    CameraSection.cameraControl => const [
      'Two-way audio',
      'Control address',
      'Username',
      'Password',
      'Control profile',
    ],
    CameraSection.masks => const ['Masks'],
    CameraSection.analysis => const ['Scene descriptions', 'Audio analysis'],
    CameraSection.objects => _labelsFor(_objectFields),
    CameraSection.motion => _labelsFor(_motionFields),
    CameraSection.speech => _labelsFor(_speechFields),
    CameraSection.sound => _labelsFor(_soundFields),
    CameraSection.playback => const [
      'Starting volume',
      'Playback silence gate',
    ],
  };

  static const _objectFields = [
    CameraSetting.detectionClasses,
    CameraSetting.describeClasses,
    CameraSetting.alertClasses,
    CameraSetting.scoreThreshold,
    CameraSetting.alertMinConfidence,
    CameraSetting.minObjectFraction,
    CameraSetting.trackConfirmSeconds,
    CameraSetting.trackCoastSeconds,
    CameraSetting.maxFps,
    CameraSetting.minMovementFraction,
    CameraSetting.absenceSeconds,
    CameraSetting.noveltySeconds,
  ];

  static const _motionFields = [
    CameraSetting.motionMinChangedFraction,
    CameraSetting.motionMaxChangedFraction,
    CameraSetting.motionPixelDelta,
  ];

  static const _speechFields = [
    CameraSetting.speechGateRmsThreshold,
    CameraSetting.vadThreshold,
  ];

  static const _soundFields = [
    CameraSetting.soundGateRmsThreshold,
    CameraSetting.soundAlertLabels,
    CameraSetting.soundIgnoredLabels,
    CameraSetting.soundMinConfidence,
    CameraSetting.soundAlertMinConfidence,
    CameraSetting.soundCooldownSeconds,
    CameraSetting.soundAlertCooldownSeconds,
  ];

  List<String> _labelsFor(List<CameraSetting> fields) => [
    for (final field in fields) widget.defaults[field].label,
  ];

  /// Which section is on screen. Derived per build rather than stored, so a search that filters the
  /// clicked one away falls back to the first that survives — the Server page's own rule.
  ///
  /// Beside the index the pane is never empty when there is something to put in it; on a screen of
  /// its own it opens nothing nobody asked for.
  CameraSection? _openSection(bool compact) {
    final sections = _matchingSections;
    if (sections.contains(_clickedSection)) return _clickedSection;
    return compact ? null : sections.firstOrNull;
  }

  /// One section's contents. [paneWidth] decides whether cards pair, which only the pane knows.
  Widget _sectionBody(CameraSection section, double paneWidth) {
    final paired = paneWidth >= kPairedMinWidth;
    final narrow = (paired ? (paneWidth - 12) / 2 : paneWidth) < 300;

    return switch (section) {
      CameraSection.general => _basics,
      CameraSection.streams => _streams,
      CameraSection.recording => _keepingFootage,
      CameraSection.cameraControl => _reachingTheCamera,
      CameraSection.masks => _masksSection,
      CameraSection.analysis => _senses,
      CameraSection.objects => _cards(
        cameraDetectionCards(
          tuning: _edited.detectionTuning,
          defaults: widget.defaults,
          compact: narrow,
          onChanged: (tuning) =>
              _update((r) => r.copyWith(detectionTuning: _keepMasks(tuning))),
        ),
        paired: paired,
        notes: [
          if (!_edited.aiVision) const TuningNote(_needsSceneDescriptions),
        ],
      ),
      CameraSection.motion => _cards(
        cameraMotionCards(
          tuning: _edited.motionTuning,
          defaults: widget.defaults,
          compact: narrow,
          onChanged: (tuning) =>
              _update((r) => r.copyWith(motionTuning: tuning)),
        ),
        paired: paired,
        notes: [
          if (_edited.motionTuning?.problem case final problem?)
            TuningNote(problem, warning: true),
          if (!_edited.aiVision) const TuningNote(_needsSceneDescriptions),
        ],
      ),
      CameraSection.speech => _speech,
      CameraSection.sound => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _gateHeader(
            field: CameraSetting.soundGateRmsThreshold,
            value: _edited.audioTuning?.soundGateRmsThreshold,
            fallback: _defaultSoundGate,
            onChanged: (v) => _update(
              (r) => _withAudio(
                r,
                (r.audioTuning ?? const AudioTuningSettings()).copyWith(
                  soundGateRmsThreshold: v,
                ),
              ),
            ),
            onClear: () => _update(
              (r) => _withAudio(
                r,
                (r.audioTuning ?? const AudioTuningSettings()).copyWith(
                  clearSoundGate: true,
                ),
              ),
            ),
          ),
          const SizedBox(height: 18),
          _cards(
            cameraSoundCards(
              tuning: _edited.soundTuning,
              defaults: widget.defaults,
              compact: narrow,
              onChanged: (tuning) =>
                  _update((r) => r.copyWith(soundTuning: tuning)),
            ),
            paired: paired,
            notes: [
              if (!_edited.aiAudio) const TuningNote(_needsAudioAnalysis),
            ],
          ),
        ],
      ),
      CameraSection.playback => _playbackAudio,
    };
  }

  /// Said under the tuning sections whose settings the Server never reads while the capability
  /// above them is off. Quoting the switch by its own name, so turning it on is a matter of
  /// following the sentence rather than guessing which of two toggles it means.
  static const _needsSceneDescriptions =
      'Nothing reads these until “Scene descriptions” is on in Analysis. They are kept either way.';

  /// The audio equivalent — and it names *Audio analysis* rather than speech, because that one
  /// switch gates the sound recogniser too. It was called “Write down speech” while doing both,
  /// which made turning it off look like it only stopped transcripts.
  static const _needsAudioAnalysis =
      'Nothing reads these until “Audio analysis” is on in Analysis. They are kept either way.';

  /// The masks this camera already has, put back onto a detection tuning the cards rebuilt without
  /// them. The cards never see the masks, so without this every edit in *What it looks for* would
  /// quietly delete them — which is the same trap `PUT` replacing rather than merging sets.
  DetectionTuningSettings? _keepMasks(DetectionTuningSettings? tuning) {
    final masks = _edited.detectionTuning?.masks;
    if (masks == null || masks.isEmpty) return tuning;
    return (tuning ?? const DetectionTuningSettings()).copyWith(masks: masks);
  }

  Widget _cards(
    List<PairedItem> items, {
    required bool paired,
    List<Widget> notes = const [],
  }) {
    // The wide ones lead so the pairs below them close up instead of leaving a hole in the middle
    // of the grid — the Server page's rule, and for the same reason.
    final ordered = paired
        ? [...items.where((i) => i.wide), ...items.where((i) => !i.wide)]
        : items;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        ...settingCardRows(items: ordered, paired: paired),
        for (final note in notes) ...[note, const SizedBox(height: 12)],
      ],
    );
  }

  Widget get _header => Container(
    padding: const EdgeInsets.fromLTRB(24, 18, 24, 16),
    decoration: BoxDecoration(
      border: Border(bottom: BorderSide(color: Serval.hairline)),
    ),
    // The actions drop under the name when the pane is too narrow to hold both beside it. Two
    // buttons this wide beat the title down to nothing long before they run out of room
    // themselves, and past that they simply leave the header — which is what the page did until
    // the rest of it learned to fold.
    child: LayoutBuilder(
      builder: (context, constraints) {
        final beside = constraints.maxWidth >= 520;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(child: _headerTitle),
                if (beside && !widget.creating) ...[
                  const SizedBox(width: 14),
                  Wrap(spacing: 8, children: _headerActions),
                ],
              ],
            ),
            if (!beside && !widget.creating) ...[
              const SizedBox(height: 12),
              Wrap(spacing: 8, runSpacing: 8, children: _headerActions),
            ],
          ],
        );
      },
    ),
  );

  Widget get _headerTitle => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      // A Wrap rather than a Row: the name, the health pill and the id chip are all
      // worth reading whole, so when the header is too narrow to hold the three side
      // by side the badges drop to a second line instead of being clipped. The name
      // still truncates, but only once it has a full-width line to itself.
      LayoutBuilder(
        builder: (context, constraints) => Wrap(
          crossAxisAlignment: WrapCrossAlignment.center,
          spacing: 9,
          runSpacing: 6,
          children: [
            ConstrainedBox(
              constraints: BoxConstraints(maxWidth: constraints.maxWidth),
              child: Text(
                widget.creating
                    ? (_name.text.trim().isEmpty
                          ? 'New camera'
                          : _name.text.trim())
                    : widget.record.name,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(
                  fontFamily: Nocturne.fontHeading,
                  fontSize: 20,
                  fontWeight: Nocturne.headingWeight,
                  color: Nocturne.text,
                ),
              ),
            ),
            if (!widget.creating) ...[
              HealthPill(health: widget.health),
              IdChip(id: widget.record.id),
            ],
          ],
        ),
      ),
      const SizedBox(height: 5),
      Text(
        _subline,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 12.5,
          color: Nocturne.mix(Nocturne.text, 50),
        ),
      ),
    ],
  );

  List<Widget> get _headerActions => [
    if (!widget.creating && !widget.readOnly)
      NocturneButton(
        label: 'Remove camera',
        icon: PhosphorIconsRegular.trash,
        variant: NocturneButtonVariant.danger,
        onPressed: widget.onDelete,
      ),
  ];

  /// Design 7b's two bands: the bar carrying the name and the way out, and under it everything the
  /// wide header hangs below the name.
  ///
  /// *Remove camera* goes into the overflow rather than beside the title. It is destructive and
  /// does not want to be under a thumb, and a button that wide beats the name down to nothing long
  /// before it runs out of room itself.
  /// Narrow, a section is a screen: the bar names the one that is open and its arrow goes back to
  /// the index rather than out to the camera list — the Server page's drill-down, exactly.
  List<Widget> _compactHeader(CameraSection? open) {
    if (open != null) {
      return [
        CompactAppBar(
          title: open.title,
          onBack: () => setState(() => _clickedSection = null),
        ),
      ];
    }

    return [
      CompactAppBar(
        title: widget.creating
            ? (_name.text.trim().isEmpty ? 'New camera' : _name.text.trim())
            : (widget.record.name.isEmpty
                  ? widget.record.id
                  : widget.record.name),
        onBack: widget.onBack,
        actions: [
          if (!widget.creating && !widget.readOnly)
            _OverflowMenu(onDelete: widget.onDelete),
        ],
      ),
      CompactSubBar(
        children: [
          if (!widget.creating) ...[
            Wrap(
              crossAxisAlignment: WrapCrossAlignment.center,
              spacing: 8,
              runSpacing: 6,
              children: [
                HealthPill(health: widget.health),
                IdChip(id: widget.record.id),
              ],
            ),
            const SizedBox(height: 6),
          ],
          Text(
            _subline,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12,
              height: 1.45,
              color: Nocturne.mix(Nocturne.text, 50),
            ),
          ),
        ],
      ),
    ];
  }

  // ------------------------------------------------------------------ masks

  /// The way into the mask editor, and what this camera already has.
  ///
  /// The section exists because 9a gave masks somewhere to live; before it, a camera's masks were a
  /// sentence in *What it looks for* saying they had been "set outside the app", which was true and
  /// is no longer.
  Widget get _masksSection {
    final masks = _edited.detectionTuning?.masks ?? const [];

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        MaskPreviewCard(
          cameraId: widget.record.id,
          masks: masks,
          frame: widget.frameFor?.call(widget.record.id),
          // Nothing to draw on until the camera exists and has published a frame.
          onEdit: widget.creating ? null : widget.onEditMasks,
        ),
        if (masks.isNotEmpty) ...[
          const SizedBox(height: 12),
          for (var i = 0; i < masks.length; i++) ...[
            if (i > 0) const SizedBox(height: 8),
            MaskListRow(
              mask: masks[i],
              onDelete: () => _replaceMasks([
                for (var j = 0; j < masks.length; j++)
                  if (j != i) masks[j],
              ]),
            ),
          ],
          const SizedBox(height: 12),
          Row(
            children: [
              SettingsLinkText(
                'Remove all masks',
                onTap: () => _replaceMasks(const []),
              ),
            ],
          ),
        ],
      ],
    );
  }

  /// Writes a new mask list onto the record, collapsing an emptied detection tuning back to null
  /// the way every other section does — an all-null bag is no bag.
  void _replaceMasks(List<DetectionMaskSettings> masks) => _update((r) {
    final tuning = (r.detectionTuning ?? const DetectionTuningSettings())
        .copyWith(masks: masks.isEmpty ? null : masks);
    return r.copyWith(detectionTuning: tuning.isEmpty ? null : tuning);
  });

  /// The design shows a line like `192.168.1.50 · Reolink RLC-810A · firmware 3.1.0.956 · up 26 days`.
  ///
  /// All but the last of that is real: the host comes from the stream URL, and the make, model
  /// and firmware from `GET /api/cameras/{id}/device-information` — ONVIF `GetDeviceInformation`,
  /// asked of the camera itself.
  ///
  /// **Uptime is still missing, and stays missing.** ONVIF's Device service exposes the system
  /// clock, not how long the device has been running, so there is nothing to derive it from.
  ///
  /// Each part is dropped rather than guessed when absent: the line arrives a moment after the
  /// screen does, and a camera that will not answer simply has a shorter subtitle.
  String get _subline {
    if (widget.creating) {
      return 'Not registered yet — it starts recording as soon as you save.';
    }

    final device = widget.deviceInformation;
    final streams = widget.record.streams.length;
    final firmware = device?.firmwareVersion?.trim();

    final parts = [
      ?widget.record.host,
      ?device?.productLabel,
      if (firmware != null && firmware.isNotEmpty) 'firmware $firmware',
      '$streams stream${streams == 1 ? '' : 's'}',
    ];
    return parts.join(' · ');
  }

  // ---------------------------------------------------------------- sections

  /// *Name* beside *Where it is*, or stacked where half the width is not enough for either.
  List<Widget> get _nameAndPlace {
    final name = NocturneField(
      label: 'Name',
      controller: _name,
      onChanged: (_) => setState(() {}),
    );
    final place = NocturneCombo(
      label: 'Location',
      controller: _location,
      suggestions: widget.knownLocations,
      placeholder: 'Anywhere',
      onChanged: (_) => setState(() {}),
    );

    if (isCompact(context)) {
      return [name, const SizedBox(height: 12), place];
    }

    return [
      Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(child: name),
          const SizedBox(width: 12),
          Expanded(child: place),
        ],
      ),
    ];
  }

  Widget get _basics => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      // The design stacks these on a phone. Half of 376px is a field too narrow to read a
      // location out of, and both are one line of text that reads better with the width.
      ..._nameAndPlace,
      const SizedBox(height: 12),
      if (widget.creating)
        NocturneField(
          label: 'ID',
          controller: _id,
          mono: true,
          onChanged: (_) => setState(() {}),
        )
      else
        NocturneReadOnlyField(
          label: 'ID',
          value: widget.record.id,
          mono: true,
          note:
              'Fixed once a camera exists — it is also the folder its recordings '
              'live in and the address its telemetry is sent to.',
        ),
      const SizedBox(height: 14),
      ToggleRow(
        title: 'Camera enabled',
        description:
            'Turn it off to stop recording and hide it from the wall. '
            'Old footage is kept.',
        value: _edited.enabled,
        onChanged: (value) => _update((r) => r.copyWith(enabled: value)),
      ),
    ],
  );

  Widget get _streams => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      for (var i = 0; i < _edited.streams.length; i++) ...[
        _StreamCard(
          stream: _edited.streams[i],
          // Deleting the only stream leaves a camera the Server will not accept, so the option
          // is withheld rather than offered and then rejected.
          onDelete: _edited.streams.length > 1
              ? () => _update(
                  (r) => r.copyWith(
                    streams: [
                      for (var j = 0; j < r.streams.length; j++)
                        if (j != i) r.streams[j],
                    ],
                  ),
                )
              : null,
          onChanged: (updated) => _update(
            (r) => r.copyWith(
              streams: [
                for (var j = 0; j < r.streams.length; j++)
                  if (j == i) updated else r.streams[j],
              ],
            ),
          ),
        ),
        const SizedBox(height: 10),
      ],
      // A Row rather than an Align: this column stretches its children, and inside a stretched
      // box the button's own Row takes the full width. A Row hands its children an unbounded
      // main axis, so the button shrink-wraps to its label the way the design draws it.
      Row(
        children: [
          NocturneButton(
            label: 'Add a stream',
            icon: PhosphorIconsRegular.plus,
            variant: NocturneButtonVariant.primary,
            height: 32,
            fontSize: 12.5,
            onPressed: () => _update(
              (r) => r.copyWith(
                streams: [
                  ...r.streams,
                  // No jobs: every role is already taken by an existing stream, and the Server
                  // allows exactly one holder of each. Reassigning is usually the next thing you
                  // do — but it no longer has to be, since a stream with no jobs is a saveable
                  // end state rather than a step on the way to one.
                  CameraStreamRecord(
                    name: 'stream${r.streams.length + 1}',
                    url: '',
                    roles: const [],
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    ],
  );

  /// Everything here is about footage on disk, so a camera writing none has a section of inert
  /// controls below its first one.
  ///
  /// Shown greyed with the reason rather than hidden: a section that vanishes leaves someone
  /// hunting a retention setting they remember, and both of the lower two are stored on the camera
  /// and come back the moment recording does.
  ///
  /// The switch at the top is the section's own — it is what decides whether there is any footage
  /// to keep, which made its absence from a page about keeping footage the odd thing. It is not the
  /// only way to arrive at "keeps nothing", though, and the other one is not fixable from here: a
  /// camera with no stream set to Recording has nothing for this to switch, so it says so and
  /// points at *Streams* rather than offering a control that would have to guess which stream it
  /// meant.
  Widget get _keepingFootage {
    final assigned = _edited.recordStreamName;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        ToggleRow(
          title: 'Record this camera',
          description: switch (assigned) {
            null =>
              'No stream is set to Recording, so there is nothing to write. Pick one under '
                  'Streams.',
            final stream when _edited.recording =>
              'Writing “$stream” to disk. Turn this off to stop keeping footage without '
                  'giving up which stream does it.',
            final stream =>
              'Nothing is written. “$stream” stays set to Recording and starts again the '
                  'moment this goes back on.',
          },
          value: _edited.recording,
          onChanged: assigned == null
              ? null
              : (value) => _update((r) => r.copyWith(recording: value)),
        ),
        const SizedBox(height: 14),
        if (!_edited.records) ...[
          Text(
            assigned == null
                ? 'Nothing is written to disk, so the two below are inert. They are kept, and '
                      'apply again as soon as a stream is set to Recording.'
                : 'Nothing is written to disk, so saving sound is inert — it is kept, and '
                      'applies again as soon as recording does. Footage already on disk still '
                      'plays back, and still expires on the schedule below.',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              height: 1.45,
              color: Nocturne.mix(Nocturne.text, 42),
            ),
          ),
          const SizedBox(height: 14),
        ],
        _retention,
        const SizedBox(height: 14),
        ToggleRow(
          title: 'Record audio',
          description:
              'Needed if you want to read back what was said later. '
              'Check your local rules.',
          value: _edited.recordAudio,
          onChanged: _edited.records
              ? (value) => _update((r) => r.copyWith(recordAudio: value))
              : null,
        ),
      ],
    );
  }

  /// Talking back and moving: the two things you do *to* this camera, as opposed to the things it
  /// notices for you. They share a section but not a mechanism — talk-back rides the WebRTC session
  /// the live view already holds open, while moving needs the ONVIF connection set up below. The
  /// section's own description says so, because a switch sitting above a row of credential fields
  /// otherwise reads as depending on them.
  Widget get _reachingTheCamera => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      CapabilityCard(
        icon: PhosphorIconsFill.microphone,
        title: 'Two-way audio',
        description: 'Adds hold-to-talk on the single camera.',
        value: _edited.twoWayAudio,
        onChanged: (value) => _update((r) => r.copyWith(twoWayAudio: value)),
      ),
      const SizedBox(height: 18),
      _ptzState,
      const SizedBox(height: 14),
      NocturneField(
        label: 'Control address',
        controller: _onvifUrl,
        mono: true,
        onChanged: (_) => setState(() {}),
      ),
      const SizedBox(height: 12),
      Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: NocturneField(
              label: 'Username',
              controller: _onvifUsername,
              onChanged: (_) => setState(() {}),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: NocturneField(
              label: 'Password',
              controller: _onvifPassword,
              obscure: true,
              // Typing here is what replaces the stored secret; leaving it alone re-sends
              // what the Server gave us, so a PUT cannot blank it by omission.
              onChanged: (_) => setState(() => _passwordReplaced = true),
            ),
          ),
        ],
      ),
      const SizedBox(height: 12),
      NocturneField(
        label: 'Control profile',
        controller: _onvifProfile,
        mono: true,
        // A free field rather than the design's dropdown: ONVIF profiles are discovered on
        // the camera by the Server on its first PTZ command, and there is no endpoint that
        // lists them, so there is nothing to populate a menu with. Empty means "pick
        // automatically", which is what the Server does — and what the design's own
        // placeholder says.
        hint: 'Pick automatically',
        onChanged: (_) => setState(() {}),
      ),
      const SizedBox(height: 5),
      Text(
        'Leave empty to let Serval pick the first view that can pan and tilt.',
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 11.5,
          height: 1.4,
          color: Nocturne.mix(Nocturne.text, 42),
        ),
      ),
    ],
  );

  /// Whether the connection is set up, said above the fields rather than in the section header.
  ///
  /// The design's line is *"Connected — pan, tilt, 2.4x zoom, one saved position"*. Most of that is
  /// knowable — `GET /api/cameras/{id}/ptz/capabilities` reports pan/tilt, zoom and the preset list
  /// — but it is knowable only by asking the camera, which takes a round trip and can fail. This
  /// form says the one thing it can say without one: whether an endpoint is configured at all,
  /// which is what the Server derives `ptzConfigured` from. The live view, where the controls
  /// actually are, reads the probe — see `ServalRepository.ptzProbeFor`.
  ///
  /// Zoom stays a bare axis on both surfaces. ONVIF's generic zoom space is a fraction of the
  /// lens's travel and nothing publishes the optical range that would make `2.4x` of it.
  Widget get _ptzState {
    final configured = _assembled.ptzConfigured;
    return Text(
      configured
          ? 'Configured — pan, tilt and zoom appear on the live view.'
          : 'Not set up — the pan and tilt pad stays hidden.',
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 12,
        height: 1.45,
        color: configured
            ? Serval.healthyText
            : Nocturne.mix(Nocturne.text, 50),
      ),
    );
  }

  /// The two switches the tuning sections are a refinement of, each named after the Server group it
  /// turns on for this camera.
  ///
  /// **The second one was called *Write down speech*, and that was a lie by omission.** `aiAudio`
  /// gates the sound recogniser as well as the transcriber — they run on the same audio — so
  /// turning off what read as a transcript setting also stopped glass-break and smoke-alarm
  /// detection, with nothing on screen saying so. *Audio analysis* is what it actually is, and the
  /// description now names both halves.
  Widget get _senses => LayoutBuilder(
    builder: (context, constraints) {
      final tiles = [
        CapabilityCard(
          icon: PhosphorIconsFill.sparkle,
          title: 'Scene descriptions',
          description: 'Writes lines like “a silver car pulled in”.',
          value: _edited.aiVision,
          onChanged: (value) => _update((r) => r.copyWith(aiVision: value)),
        ),
        CapabilityCard(
          icon: PhosphorIconsFill.waveform,
          title: 'Audio analysis',
          description: 'Transcribes speech and recognises sounds.',
          value: _edited.aiAudio,
          onChanged: (value) => _update((r) => r.copyWith(aiAudio: value)),
        ),
      ];

      // A card narrower than about 140 reads one word to a line, which is worse than a stack —
      // so the cards only share a row when there is that much for each of them.
      if (constraints.maxWidth < 280) {
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            for (var index = 0; index < tiles.length; index++) ...[
              if (index > 0) const SizedBox(height: 10),
              tiles[index],
            ],
          ],
        );
      }

      // The cards share a height so they read as one group rather than a ragged row — their
      // descriptions are different lengths. `stretch` alone cannot do that here: this sits in a
      // scroll view, so the incoming height is unbounded and stretching would force an infinite
      // height onto each card. IntrinsicHeight measures them first, which is affordable for a
      // couple of small cards and nothing like a general-purpose habit.
      return IntrinsicHeight(
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            for (var index = 0; index < tiles.length; index++) ...[
              if (index > 0) const SizedBox(width: 10),
              Expanded(child: tiles[index]),
            ],
          ],
        ),
      );
    },
  );

  /// When this camera is worth transcribing, and how sure Serval must be that it heard a voice.
  ///
  /// **Both rows are hand-drawn rather than [CameraSettingCard]s, and both take their names from
  /// the catalogue.** Those two facts used to be one compromise: the rows were hand-drawn *and*
  /// hand-labelled, so the section listed *Hear speech above* while its own search index listed the
  /// catalogue's *Counts as silence below*, and neither word found the other. The label is now read
  /// from the same place the Server page reads it.
  ///
  /// The controls stay bespoke because the generic card cannot draw either of them. Its slider
  /// snaps to two decimal places, and a gate is an RMS level whose whole useful range on a real
  /// camera — measured from 0.0002 to 0.05 — lives below its first stop; a linear track would put
  /// every value worth setting in its leftmost few percent, which is why this one is log-scaled.
  Widget get _speech {
    final tuning = _edited.audioTuning;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _gateHeader(
          field: CameraSetting.speechGateRmsThreshold,
          value: tuning?.speechGateRmsThreshold,
          fallback: _defaultSpeechGate,
          onChanged: (v) => _update(
            (r) => _withAudio(
              r,
              (r.audioTuning ?? const AudioTuningSettings()).copyWith(
                speechGateRmsThreshold: v,
              ),
            ),
          ),
          onClear: () => _update(
            (r) => _withAudio(
              r,
              (r.audioTuning ?? const AudioTuningSettings()).copyWith(
                clearSpeechGate: true,
              ),
            ),
          ),
        ),
        const SizedBox(height: 14),
        _vadRow(tuning?.vadThreshold),
        if (!_edited.aiAudio) ...[
          const SizedBox(height: 10),
          const TuningNote(_needsAudioAnalysis),
        ],
      ],
    );
  }

  /// The live meter over one log-scaled gate, which is the pair both audio sections open with.
  ///
  /// The meter leads because a number in these units means nothing except against what this
  /// particular camera is actually sending — his six cameras span two decades of resting level.
  Widget _gateHeader({
    required CameraSetting field,
    required double? value,
    required double fallback,
    required ValueChanged<double> onChanged,
    required VoidCallback onClear,
  }) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      AudioLevelMeter(level: _levels?.level, threshold: value ?? fallback),
      const SizedBox(height: 16),
      _rmsRow(
        label: widget.defaults[field].label,
        value: value,
        fallback: fallback,
        onChanged: onChanged,
        onClear: onClear,
      ),
    ],
  );

  /// The speech-certainty threshold. Linear, unlike the gates: it is a probability, so its range is
  /// already the range a person reasons in. Named from the catalogue for the reason [_speech] gives.
  Widget _vadRow(double? vad) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Row(
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Expanded(
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Flexible(
                  child: Text(
                    widget.defaults[CameraSetting.vadThreshold].label,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 13,
                      color: Nocturne.mix(Nocturne.text, 72),
                    ),
                  ),
                ),
                const SizedBox(width: 9),
                Flexible(
                  child: Text(
                    vad == null
                        ? 'the Server’s default'
                        : '${(vad * 100).round()}%',
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 13.5,
                      fontWeight: Nocturne.headingWeight,
                      color: Nocturne.text,
                    ),
                  ),
                ),
              ],
            ),
          ),
          if (vad != null) ...[
            const SizedBox(width: 12),
            LinkText(
              'Use the default',
              onTap: () => _update(
                (r) => _withAudio(
                  r,
                  (r.audioTuning ?? const AudioTuningSettings()).copyWith(
                    clearVad: true,
                  ),
                ),
              ),
            ),
          ],
        ],
      ),
      const SizedBox(height: 9),
      NocturneSlider(
        value: vad ?? _defaultVadThreshold,
        // Never the endpoints: 0 makes every window speech and 1 makes none, so they are off the
        // track rather than a value somebody can pick and then wonder about. The catalogue's own
        // 0-to-1 is the range the Server *accepts*, which is a different question.
        min: 0.05,
        max: 0.95,
        // Snapped to the percent this row displays, so the number on screen is the number
        // stored. Without it the label reads "70%" while the record holds 0.7013, and the two
        // disagree in a way only an API client would ever see.
        onChanged: (value) => _update(
          (r) => _withAudio(
            r,
            (r.audioTuning ?? const AudioTuningSettings()).copyWith(
              vadThreshold: (value * 100).round() / 100,
            ),
          ),
        ),
      ),
    ],
  );

  /// What this camera sounds like when a person listens to it, as opposed to what the detector makes
  /// of it.
  ///
  /// **A section of its own, and it was not always.** These two sat under a kicker inside *How
  /// sensitive its ears are*, below three log-scaled RMS-ish thresholds that belong to the
  /// detectors — which is exactly the arrangement in which somebody turns down the wrong one and
  /// quietly stops getting alerts. The detector gates now live in the two sections named after the
  /// things they gate, and what you hear has a place of its own.
  ///
  /// Keeps the meter, because it reads the level this camera actually sends and that is the number
  /// both of these are chosen against. Note it does *not* move when they change — it shows what
  /// arrives, and these are applied afterwards, on the machine doing the listening.
  Widget get _playbackAudio => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      AudioLevelMeter(
        level: _levels?.level,
        threshold: _edited.playbackGateRms ?? _defaultPlaybackGate,
      ),
      const SizedBox(height: 16),
      NocturneSelect<double>(
        label: 'Starting volume',
        value: _nearestStartingStop(_edited.playbackGainDb),
        options: startingVolumeStops,
        // The same 0..100 the volume pill shows, so the two places a level is set quote the same
        // number. How much amplification a position past the mark asks for is not stated here for the
        // same reason it is not stated there.
        optionLabel: (position) => position <= unityTravel * 100
            ? '${position.round()}% — full volume, nothing added'
            : '${position.round()}%',
        onChanged: (position) => _update(
          (r) => r.copyWith(
            playbackGainDb: playbackFromTravel((position ?? 75) / 100).db,
          ),
        ),
      ),
      const SizedBox(height: 10),
      const TuningNote(
        'Where the volume slider sits the first time somebody opens this camera. After that each '
        'browser keeps its own position, so changing this does not move anyone who has already '
        'set one.',
      ),
      const SizedBox(height: 16),
      _rmsRow(
        label: 'Playback silence gate',
        value: _edited.playbackGateRms,
        fallback: _defaultPlaybackGate,
        nullLabel: 'not gated',
        clearLabel: 'Stop gating',
        onChanged: (v) => _update((r) => r.copyWith(playbackGateRms: v)),
        onClear: () => _update((r) => r.copyWith(clearPlaybackGate: true)),
      ),
      const SizedBox(height: 10),
      TuningNote(
        _edited.playbackGainDb > 0
            ? 'Set it just above the meter’s resting level. Without it, amplifying a camera this '
                  'far also amplifies the hiss it records when nothing is happening.'
            : 'Read once the camera is being amplified. On its own it only takes quiet sound away.',
      ),
    ],
  );

  /// The stop nearest a stored gain, as a position on the control.
  ///
  /// Snapped rather than matched, because the stored value is a dB and the stops are positions: the
  /// round trip through [playbackFromTravel] and [travelFor] does not land on the same double it
  /// started from, and a select whose value is absent from its own options has nothing to show.
  /// Snapping also covers a gain set outside these stops, whatever set it.
  static double _nearestStartingStop(double db) {
    final position = travelFor(volume: 1, db: db) * 100;
    return startingVolumeStops.reduce(
      (a, b) => (a - position).abs() <= (b - position).abs() ? a : b,
    );
  }

  /// Rounds a slider's output to four significant figures before it is stored.
  ///
  /// A log track maps a pixel to a full-precision double, so dragging produces values like
  /// `0.003162277660168379` — seventeen digits of which four are meaningful and the rest are an
  /// artefact of where the pointer landed. Four figures is finer than a hundredth of a decibel,
  /// far below anything audible or settable, and it keeps the stored record readable for anyone
  /// looking at the camera through the API rather than through this form.
  static double _quantize(double value, {int figures = 4}) {
    if (value <= 0 || !value.isFinite) return value;

    final scale = math
        .pow(10, figures - 1 - (math.log(value) / math.ln10).floor())
        .toDouble();
    return (value * scale).round() / scale;
  }

  /// A new audio tuning onto the record, where null means *this camera overrides nothing*.
  ///
  /// **`copyWith(audioTuning: null)` does not clear it.** Alone among the four tuning bags,
  /// `audioTuning` takes a `clear…` flag rather than the `_keep` sentinel the other three use — see
  /// the note on `_keep` in `camera_record.dart`, which explains why both spellings exist — so a
  /// bare null reads as "not passed" and leaves the old bag in place. Clearing the last override in
  /// *Speech & transcription* or *Sound recognition* would silently do nothing without this.
  ///
  /// Collapsing an emptied bag to null is the other half: the Server does the same on save, and a
  /// form that did not would report an unsaved change that never survives the trip.
  static CameraRecord _withAudio(
    CameraRecord record,
    AudioTuningSettings? tuning,
  ) => tuning == null || tuning.isEmpty
      ? record.copyWith(clearAudioTuning: true)
      : record.copyWith(audioTuning: tuning);

  /// One RMS threshold. Log-scaled for the reason the meter above it is: the useful range spans
  /// two decades, and a linear track would put every value worth setting in its leftmost few
  /// percent.
  /// A log-scaled RMS threshold, with its value in dB beside the label.
  ///
  /// [nullLabel] and [clearLabel] exist for the playback gate. For the three detector thresholds an
  /// unset value means "fall back to the Server", and the copy says so; an unset playback gate means
  /// no gate at all, which is a different sentence about a different thing. Everything else about the
  /// row — the log track, the dB readout, the quantising — is the same question asked the same way.
  Widget _rmsRow({
    required String label,
    required double? value,
    required double fallback,
    required ValueChanged<double> onChanged,
    required VoidCallback onClear,
    String nullLabel = 'the Server’s default',
    String clearLabel = 'Use the default',
  }) {
    const minRms = 0.0002;
    const maxRms = 0.05;

    final current = value ?? fallback;
    final logMin = math.log(minRms);
    final logMax = math.log(maxRms);
    final position = ((math.log(current) - logMin) / (logMax - logMin)).clamp(
      0.0,
      1.0,
    );

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            Expanded(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Flexible(
                    child: Text(
                      label,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 13,
                        color: Nocturne.mix(Nocturne.text, 72),
                      ),
                    ),
                  ),
                  const SizedBox(width: 9),
                  Flexible(
                    child: Text(
                      value == null
                          ? nullLabel
                          : '${(20 * math.log(current) / math.ln10).round()} dB',
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 13.5,
                        fontWeight: Nocturne.headingWeight,
                        color: Nocturne.text,
                      ),
                    ),
                  ),
                ],
              ),
            ),
            if (value != null) ...[
              const SizedBox(width: 12),
              LinkText(clearLabel, onTap: onClear),
            ],
          ],
        ),
        const SizedBox(height: 9),
        NocturneSlider(
          value: position,
          min: 0,
          max: 1,
          onChanged: (picked) => onChanged(
            _quantize(math.exp(logMin + picked * (logMax - logMin))),
          ),
        ),
      ],
    );
  }

  Widget get _retention {
    final days = _edited.retentionDays;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            // The sentence gives way before the link does: "Use the default" is the only thing
            // in this row you can act on, so it keeps its width and the prose ellipsises.
            Expanded(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Flexible(
                    child: Text(
                      'Keep recordings for',
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 13,
                        color: Nocturne.mix(Nocturne.text, 72),
                      ),
                    ),
                  ),
                  const SizedBox(width: 9),
                  Flexible(
                    child: Text(
                      days == null
                          ? 'the Server’s default'
                          : '$days day${days == 1 ? '' : 's'}',
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 13.5,
                        fontWeight: Nocturne.headingWeight,
                        color: Nocturne.text,
                      ),
                    ),
                  ),
                ],
              ),
            ),
            if (days != null) ...[
              const SizedBox(width: 12),
              LinkText(
                'Use the default',
                onTap: () =>
                    _update((r) => r.copyWith(clearRetentionDays: true)),
              ),
            ],
          ],
        ),
        const SizedBox(height: 9),
        NocturneSlider(
          value: (days ?? _defaultRetentionDays).toDouble(),
          min: 1,
          max: _maxRetentionDays.toDouble(),
          // Live whenever a stream is set to Recording, whether or not it is recording right now:
          // the Server expires what is on disk on this schedule either way, so a camera switched
          // off is still ageing out its old footage and this is still the dial for it. Only a
          // camera with no record stream at all has nothing for it to govern — which is the same
          // line the Server's own advisory draws.
          onChanged: _edited.recordStreamName != null
              ? (value) =>
                    _update((r) => r.copyWith(retentionDays: value.round()))
              : null,
        ),
        if (_retentionEstimate case final estimate?) ...[
          const SizedBox(height: 9),
          Text(
            estimate,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              height: 1.45,
              color: Nocturne.mix(Nocturne.text, 42),
            ),
          ),
        ],
      ],
    );
  }

  /// `Holding 412 GB now, about 59 GB/day — around 1.7 TB at 30 days.`
  ///
  /// What the retention setting costs in disk, from the write rate `GET /api/system/stats`
  /// measures. Measured rather than multiplied out from a nominal bitrate, so it is what this
  /// camera has actually written — audio and keyframes and all.
  ///
  /// Null while the Server has not measured it, has the walk switched off, or has not yet seen a
  /// long enough span to divide by. The slider is unaffected — an estimate is a nicety, and
  /// nothing here waits on it.
  String? get _retentionEstimate {
    final usage = widget.diskUsage;
    if (usage == null) return null;

    // What is still on disk is worth saying — turning Recording off leaves the old footage to age
    // out rather than deleting it — but a per-day rate and a projection are not: both describe a
    // camera that is still writing, and this one is not.
    if (!_edited.records) {
      return usage.bytes == 0
          ? null
          : 'Holding ${formatBytes(usage.bytes)} of older footage, ageing out.';
    }

    final perDay = usage.bytesPerDay;
    if (perDay == null) {
      return usage.bytes == 0
          ? null
          : 'Holding ${formatBytes(usage.bytes)} now.';
    }

    final held =
        'Holding ${formatBytes(usage.bytes)} now, about '
        '${formatBytesPerDay(perDay)}';

    // Projected against the *edited* value rather than the saved one, so dragging the slider
    // answers the question that made you drag it — and left off entirely while the slider still
    // sits where the measurement was taken, where the projection would only restate the figure
    // at the front of the sentence.
    final days = _edited.retentionDays ?? _defaultRetentionDays;
    final measured = usage.oldestSegmentAt == null
        ? null
        : DateTime.now().difference(usage.oldestSegmentAt!).inDays;

    if (measured != null && (days - measured).abs() <= 1) {
      return '$held.';
    }

    return '$held — around ${formatBytes(perDay * days)} at '
        '$days day${days == 1 ? '' : 's'}.';
  }

  // ---------------------------------------------------------------- save bar

  /// The pinned bar, and the only place a change is committed.
  ///
  /// The Server settings page's own bar, with the Server settings page's own phrasing — see
  /// [SettingsSaveBar.changeSummary]. This screen used to word the same idea its own way, which is
  /// how "1 change not saved yet — the name" and "1 setting changed, not yet saved" came to be two
  /// sentences for one state.
  Widget _saveBar(bool compact, CameraSection? open) {
    final problem = _problem;
    final changes = _changes;
    final dirty = changes.isNotEmpty || widget.creating;

    final where = [
      for (final entry in _changesBySection.entries)
        if (entry.value.isNotEmpty) entry.key.title,
    ];

    // A validation problem outranks the count: the record would be refused, and saying which
    // section holds an edit is no use while nothing can be sent at all.
    final status =
        problem ??
        (widget.creating
            ? 'Not added yet.'
            : SettingsSaveBar.changeSummary(changes.length, where));

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // Above the bar rather than inside it: a failure from the Server is several lines and
        // carries its own detail, which a bar sized for one sentence would clip.
        if (_failure case final failure?)
          Padding(
            padding: EdgeInsets.fromLTRB(compact ? 18 : 24, 0, 18, 10),
            child: SaveFailureNote(error: failure),
          ),
        SettingsSaveBar(
          status: status,
          dirty: dirty,
          saving: _saving,
          // Disabled while the record would be refused, so the reason is read here rather than
          // in a 400 after the fact.
          blocked: problem != null,
          saveLabel: widget.creating ? 'Add camera' : 'Save camera',
          compactSaveLabel: widget.creating ? 'Add' : 'Save',
          discardLabel: widget.creating ? 'Cancel' : 'Discard',
          onSave: _save,
          onDiscard: () {
            final discard = widget.onDiscard;
            if (discard != null) {
              discard();
            } else {
              _revert();
            }
          },
          compact: compact,
        ),
      ],
    );
  }

  void _revert() => setState(() {
    _edited = widget.record;
    _name.text = widget.record.name;
    _location.text = widget.record.location ?? '';
    _onvifUrl.text = widget.record.onvifUrl ?? '';
    _onvifUsername.text = widget.record.onvifUsername ?? '';
    _onvifPassword.text = (widget.record.onvifPassword ?? '').isEmpty
        ? ''
        : '••••••••';
    _onvifProfile.text = widget.record.onvifProfileToken ?? '';
    _passwordReplaced = false;
    _failure = null;
  });
}

/// One section, filling the pane: its name, how many settings it holds, and its contents.
///
/// The Server settings page's [_GroupPane] by another name, minus the section-level reset — a
/// camera has no equivalent of "put this whole group back", because putting a section back means
/// clearing overrides one by one and each card already offers that.
class _SectionPane extends StatelessWidget {
  const _SectionPane({
    required this.section,
    required this.count,
    required this.changed,
    required this.builder,
    this.showTitle = true,
  });

  final CameraSection section;

  /// How many settings this section holds.
  ///
  /// **Counted from the labels the section actually draws, not restated on the enum.** It was a
  /// field on `CameraSection` once, and by the time this was written two of them were wrong —
  /// *What it looks for* claimed 10 and drew 12, *How sensitive its ears are* claimed 3 and drew 5
  /// — because a number written beside a section is a number nobody updates when a card is added.
  final int count;

  /// How many of this section's settings are edited and unsaved.
  final int changed;

  /// The contents, given the pane's own width — only it knows whether cards pair.
  final Widget Function(double width) builder;

  /// False where the app bar above already carries the section's name.
  final bool showTitle;

  String get _subtitle {
    final settings = '$count ${count == 1 ? 'setting' : 'settings'}';
    return changed == 0 ? settings : '$settings · $changed not saved';
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      if (showTitle) ...[
        Text(
          section.title,
          style: const TextStyle(
            fontFamily: Nocturne.fontHeading,
            fontSize: 16,
            fontWeight: Nocturne.headingWeight,
            color: Nocturne.text,
          ),
        ),
        const SizedBox(height: 3),
      ],
      Text(
        _subtitle,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 12,
          height: 1.45,
          color: Nocturne.mix(Nocturne.text, 50),
        ),
      ),
      const SizedBox(height: 6),
      Text(section.blurb, style: settingHelpStyle()),
      const SizedBox(height: 14),
      Expanded(
        child: Stack(
          children: [
            LayoutBuilder(
              builder: (context, constraints) => SingleChildScrollView(
                padding: const EdgeInsets.only(bottom: 22),
                child: builder(constraints.maxWidth),
              ),
            ),
            // The last card fades into the bar rather than being cut off by it, so a section
            // that runs past the fold says so.
            Positioned(
              left: 0,
              right: 0,
              bottom: 0,
              child: IgnorePointer(
                child: Container(
                  height: 22,
                  decoration: BoxDecoration(
                    gradient: LinearGradient(
                      begin: Alignment.topCenter,
                      end: Alignment.bottomCenter,
                      colors: [Serval.panel.withValues(alpha: 0), Serval.panel],
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    ],
  );
}

/// The one sentence that explains the whole page: a blank setting is not an empty setting.
///
/// Under the index rather than at the head of each section — it is a fact about the form, and
/// saying it ten times is how a form teaches people to stop reading it.
class _InheritanceNote extends StatelessWidget {
  const _InheritanceNote({required this.onOpenServerSettings});

  final VoidCallback? onOpenServerSettings;

  @override
  Widget build(BuildContext context) => CustomPaint(
    painter: DashedBorder(color: Nocturne.mix(Nocturne.text, 14), radius: 8),
    child: Padding(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 9),
      child: Text.rich(
        TextSpan(
          children: [
            const TextSpan(text: 'Blank settings fall through to '),
            if (onOpenServerSettings case final open?)
              WidgetSpan(
                alignment: PlaceholderAlignment.baseline,
                baseline: TextBaseline.alphabetic,
                child: SettingsLinkText('Server settings', onTap: open),
              )
            else
              const TextSpan(text: 'Server settings'),
            const TextSpan(text: '.'),
          ],
        ),
        style: settingHelpStyle(),
      ),
    ),
  );
}

/// One stream: its name and address, what it is used for, and whether it is re-encoded.
class _StreamCard extends StatefulWidget {
  const _StreamCard({
    required this.stream,
    required this.onChanged,
    required this.onDelete,
  });

  final CameraStreamRecord stream;
  final ValueChanged<CameraStreamRecord> onChanged;
  final VoidCallback? onDelete;

  @override
  State<_StreamCard> createState() => _StreamCardState();
}

class _StreamCardState extends State<_StreamCard> {
  late final TextEditingController _name;
  late final TextEditingController _url;

  @override
  void initState() {
    super.initState();
    _name = TextEditingController(text: widget.stream.name);
    _url = TextEditingController(text: widget.stream.url);
  }

  @override
  void dispose() {
    _name.dispose();
    _url.dispose();
    super.dispose();
  }

  void _toggleRole(StreamRole role, bool assigned) => widget.onChanged(
    widget.stream.copyWith(
      roles: assigned
          ? [...widget.stream.roles, role]
          : [
              for (final existing in widget.stream.roles)
                if (existing != role) existing,
            ],
    ),
  );

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.all(13),
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.text, 3),
      borderRadius: BorderRadius.circular(8),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 10)),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            Expanded(
              child: NocturneField(
                label: 'Stream name',
                controller: _name,
                mono: true,
                onChanged: (value) =>
                    widget.onChanged(widget.stream.copyWith(name: value)),
              ),
            ),
            if (widget.onDelete != null) ...[
              const SizedBox(width: 8),
              NocturneButton.icon(
                icon: PhosphorIconsRegular.trash,
                variant: NocturneButtonVariant.ghost,
                height: 36,
                onPressed: widget.onDelete,
              ),
            ],
          ],
        ),
        const SizedBox(height: 11),
        NocturneField(
          label: 'Address',
          controller: _url,
          mono: true,
          hint: 'rtsp://…',
          // The camera's credentials live in this URL. Masked with a reveal like any other
          // password — but as the address with its password starred, not as a field of dots:
          // the host and path are what an installer is reading. Copy still yields the real one,
          // since a copy button that hands you stars is worse than no copy button.
          obscure: widget.stream.url.contains('@'),
          maskedPreview: widget.stream.maskedUrl,
          // Revealing is what gives this box a caret, and a box that silently refuses typing is
          // a dead end unless it says so where the typing was about to happen.
          maskedNote:
              'Hidden because it carries the camera’s password — reveal it to edit.',
          copyable: true,
          onChanged: (value) =>
              widget.onChanged(widget.stream.copyWith(url: value)),
        ),
        const SizedBox(height: 11),
        Wrap(
          crossAxisAlignment: WrapCrossAlignment.center,
          spacing: 8,
          runSpacing: 8,
          children: [
            Text(
              'Used for',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12,
                color: Nocturne.mix(Nocturne.text, 55),
              ),
            ),
            for (final role in StreamRole.values)
              RoleChip(
                role: role,
                assigned: widget.stream.roles.contains(role),
                onChanged: (assigned) => _toggleRole(role, assigned),
              ),
          ],
        ),
        // Said here rather than left to the empty chip row, which reads as "not set up yet" when
        // it is in fact a state you can save and come back to.
        if (widget.stream.roles.isEmpty) ...[
          const SizedBox(height: 7),
          Text(
            'Nothing is pulled from this stream. Its address is kept, so give it a job whenever '
            'you want it back.',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              height: 1.4,
              color: Nocturne.mix(Nocturne.text, 42),
            ),
          ),
        ],
        const SizedBox(height: 11),
        _TranscodeRow(stream: widget.stream, onChanged: widget.onChanged),
      ],
    ),
  );
}

/// The *Re-encode* switch and, once on, which codec to encode to.
///
/// Off is the default and the cheap path: the camera's bits go into the archive untouched. On
/// costs roughly a core per camera continuously, so the design gives it a switch rather than
/// burying it in a codec list where it could be picked by accident.
class _TranscodeRow extends StatelessWidget {
  const _TranscodeRow({required this.stream, required this.onChanged});

  final CameraStreamRecord stream;
  final ValueChanged<CameraStreamRecord> onChanged;

  static const _codecs = ['h264', 'vp9', 'av1'];

  @override
  Widget build(BuildContext context) {
    final transcode = stream.transcode;
    // Named for the role, not for whether the camera is recording right now — the two are separate
    // since *Keeping footage* gained its own switch, and only the role decides what a transcode
    // could ever apply to.
    final recorded = stream.roles.contains(StreamRole.record);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Text(
              'Re-encode',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12,
                color: Nocturne.mix(Nocturne.text, recorded ? 55 : 30),
              ),
            ),
            const SizedBox(width: 8),
            NocturneToggle(
              value: transcode != null,
              compact: true,
              // Only the recorded stream is written to disk, so the Server rejects a transcode
              // anywhere else rather than ignoring it.
              onChanged: recorded
                  ? (value) => onChanged(
                      value
                          ? stream.copyWith(
                              transcode: const TranscodeSettings(codec: 'h264'),
                            )
                          : stream.copyWith(clearTranscode: true),
                    )
                  : null,
            ),
            const Spacer(),
            if (transcode != null)
              SizedBox(
                width: 120,
                child: NocturneSelect<String>(
                  label: 'Codec',
                  value: transcode.codec,
                  options: _codecs,
                  optionLabel: (codec) => codec,
                  onChanged: (codec) => onChanged(
                    stream.copyWith(
                      transcode: transcode.copyWith(codec: codec ?? 'h264'),
                    ),
                  ),
                ),
              ),
          ],
        ),
        if (!recorded) ...[
          const SizedBox(height: 5),
          Text(
            // A stream with no jobs keeps a setting it already had — the Server stores it and
            // applies it again when the stream is recorded — so the note says "kept" rather than
            // leaving a switched-on control with no explanation beside it.
            transcode != null && stream.roles.isEmpty
                ? 'Kept while this stream has no jobs. It applies again when the stream is the '
                      'one being recorded.'
                : 'Only the recording stream can be re-encoded.',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              color: Nocturne.mix(Nocturne.text, 35),
            ),
          ),
        ],
      ],
    );
  }
}

/// The camera header's destructive action, folded behind a ⋮ so it is not under a thumb.
class _OverflowMenu extends StatelessWidget {
  const _OverflowMenu({required this.onDelete});

  final VoidCallback? onDelete;

  @override
  Widget build(BuildContext context) => PopupMenuButton<void>(
    color: Serval.overlay,
    position: PopupMenuPosition.under,
    tooltip: 'More',
    onSelected: (_) => onDelete?.call(),
    itemBuilder: (context) => [
      PopupMenuItem<void>(
        enabled: onDelete != null,
        child: _item(
          PhosphorIconsRegular.trash,
          'Remove camera',
          onDelete == null ? Nocturne.mix(Nocturne.text, 35) : Serval.alertText,
        ),
      ),
    ],
    child: const CompactBarAction(
      icon: PhosphorIconsRegular.dotsThreeVertical,
      tooltip: 'More',
    ),
  );

  static Widget _item(PhosphorIconData icon, String label, Color color) => Row(
    children: [
      PhosphorIcon(icon, size: 18, color: color),
      const SizedBox(width: 13),
      Text(
        label,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 14.5,
          color: color,
        ),
      ),
    ],
  );
}
