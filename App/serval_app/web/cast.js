// Google Cast sender, kept in JavaScript rather than Dart interop on purpose.
//
// A callback from JavaScript into Dart is the direction that kept breaking: dart2js binds
// arguments and checks types *before* the body runs, so an SDK that calls back with one argument
// where two are documented throws inside Google's own code, with no Dart frame in the stack. So
// nothing here calls into Dart. It publishes plain state and Dart polls it — see cast_sender_web.
//
// What gets cast is Serval's own receiver application, not Google's default one. That receiver
// opens a WebRTC connection back to this server and plays the URL it is handed only if that fails,
// so a television gets the same sub-second picture the App shows. The default receiver could only
// ever play the URL, several seconds behind, which is what this used to do.
//
// The application id is per-deployment — the operator registers their own against their own
// server's receiver URL — so it arrives from the server at start() time rather than being a
// constant here.
(function () {
  'use strict';

  var SDK = 'https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1';
  var READY_TIMEOUT_MS = 20000;
  var POLL_MS = 250;

  // How long to give a cold-launched receiver before loading again. Long enough for a device to
  // fetch and start the page, short enough that a viewer does not give up and press it themselves —
  // which is what they have been doing.
  var RETRY_AFTER_MS = 4000;

  var ready = false;
  var receiverAvailable = false;
  var lastError = '';
  var initialised = false;
  var currentAppId = null;

  // The application the SDK has been told to look for, as opposed to the one it has been
  // configured with. Held separately because the two are set at different times: this arrives from
  // the server as soon as a camera screen opens, and is applied once the SDK finishes loading.
  var wantedAppId = null;

  function log(message) {
    if (window.__servalCastDebug) console.log('[cast] ' + message);
  }

  /**
   * Starts the framework for one application id.
   *
   * Re-initialising with a different id is supported because the id is server-supplied and can
   * change under us (an operator registering a receiver, or clearing one). The SDK tolerates
   * setOptions being called again; what it does not tolerate is being asked to launch an id it was
   * not initialised with.
   */
  function configure(appId) {
    if (!window.chrome || !chrome.cast || !chrome.cast.isAvailable) return false;
    if (currentAppId === appId) return true;

    cast.framework.CastContext.getInstance().setOptions({
      receiverApplicationId: appId,

      // Leave a session running when the tab goes away. Casting a camera to a television is a
      // "put it on that screen and walk off" action, and tearing it down on navigation would make
      // the button useless for the one thing it is for.
      autoJoinPolicy: chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED
    });

    currentAppId = appId;
    watchReceivers();
    return true;
  }

  function watchReceivers() {
    var context = cast.framework.CastContext.getInstance();

    // CastState is on cast.framework, not chrome.cast — the two namespaces both exist and only one
    // has it, so getting this wrong throws inside the discovery callback and the button never
    // appears. The string is the enum's own value, kept as a fallback so a namespace that moves
    // again degrades to a working comparison rather than to no casting at all.
    var noDevices =
      (cast.framework.CastState && cast.framework.CastState.NO_DEVICES_AVAILABLE)
      || 'NO_DEVICES_AVAILABLE';

    function refresh() {
      receiverAvailable = context.getCastState() !== noDevices;
      log('cast state: ' + context.getCastState());
    }

    context.addEventListener(
      cast.framework.CastContextEventType.CAST_STATE_CHANGED, refresh);
    refresh();
  }

  /**
   * Loads Google's sender SDK.
   *
   * Both the callback and a poll, because Chrome injects its own sender script into a tab that has
   * cast before — so the API can already be initialised, and the callback already dispatched,
   * before this file ever runs. Waiting only on the callback means the button never appears in
   * exactly the tabs most likely to want it.
   */
  function loadSdk() {
    if (initialised) return;
    initialised = true;

    window.__onGCastApiAvailable = function (available) {
      ready = !!available;
      log('api available: ' + available);
    };

    var script = document.createElement('script');
    script.src = SDK;
    document.head.appendChild(script);

    var waited = 0;
    var poll = setInterval(function () {
      waited += POLL_MS;
      if (window.chrome && chrome.cast && chrome.cast.isAvailable) {
        ready = true;
        clearInterval(poll);
        log('api ready after ' + waited + 'ms');

        // Discovery cannot start until the SDK knows which application to look for, so applying
        // this is what makes a receiver findable at all — and therefore what makes the button
        // appear. Doing it only at launch time was a deadlock: no discovery, no button, no launch.
        if (wantedAppId) configure(wantedAppId);
      } else if (waited >= READY_TIMEOUT_MS) {
        clearInterval(poll);
        log('api never became available');
      }
    }, POLL_MS);
  }

  function session() {
    if (!ready || !window.cast || !cast.framework) return null;
    return cast.framework.CastContext.getInstance().getCurrentSession();
  }

  /**
   * Describes what to play. Shared by both launch paths, which differ only in whether a session
   * has to be asked for first.
   *
   * The URL is a live HLS playlist, which is what the receiver falls back to — it negotiates WebRTC
   * off the same URL first, so this describes the fallback rather than what will actually play.
   */
  function mediaInfo(url, title, live) {
    var info = new chrome.cast.media.MediaInfo(url, 'application/vnd.apple.mpegurl');

    // A recording is BUFFERED, which is what gives the television a duration, a scrub bar and
    // working transport controls. Marking one LIVE would take all three away, and marking a live
    // stream BUFFERED would have it seek to a beginning that does not exist.
    info.streamType = live
      ? chrome.cast.media.StreamType.LIVE
      : chrome.cast.media.StreamType.BUFFERED;

    // The live fallback is fMP4, straight off the recorder. A recording is MPEG-TS, because it is
    // transcoded and TS segments carry their own parameter sets. Say which: the media player
    // library assumes transport stream, and an fMP4 stream that does not declare itself is parsed,
    // fetched in full, and rendered as nothing at all, with no error on either side.
    var formats = chrome.cast.media.HlsVideoSegmentFormat;
    info.hlsVideoSegmentFormat = live
      ? ((formats && formats.FMP4) || 'fmp4')
      : ((formats && formats.MPEG2_TS) || 'mpeg2_ts');

    if (live) {
      info.hlsSegmentFormat =
        (chrome.cast.media.HlsSegmentFormat && chrome.cast.media.HlsSegmentFormat.FMP4) || 'fmp4';
    }

    info.metadata = new chrome.cast.media.GenericMediaMetadata();
    info.metadata.title = title;

    return info;
  }

  /**
   * Loads the media, and loads it again if the first one goes nowhere.
   *
   * A cold launch is a race the sender loses: the session is reported established before the
   * receiver page has finished starting, so the first load can arrive at a receiver that has not
   * registered its message interceptor yet and is dropped — the television then sits on the
   * receiver's own screen forever, and a second press works because the receiver is by then
   * already running. Retrying is safe: the receiver treats a second load as a fresh camera.
   *
   * Both failure shapes are covered because they are not the same. A dropped load may reject, and
   * it may equally just never settle, so there is a timer as well — whichever fires first retries,
   * and `settled` keeps that to exactly one extra attempt.
   */
  function load(active, info, title, startSeconds) {
    var settled = false;
    var retried = false;

    function request() {
      var req = new chrome.cast.media.LoadRequest(info);

      // Where in the recording to open.
      //
      // The playlist carries an EXT-X-START for the same instant and the receiver ignores it —
      // that tag arrived in HLS version 6 and this playlist is version 3, which it has to be for
      // MPEG-TS segments with no EXT-X-MAP. Saying it in the load request is what actually works,
      // and it is the sender that knows: the window is far wider than the playhead, so without it
      // a cast opens hours before whatever is being watched.
      if (startSeconds > 0) req.currentTime = startSeconds;

      return req;
    }

    function attempt() {
      active.loadMedia(request()).then(function () {
        settled = true;
        log('loaded ' + title);
      }, function (err) {
        if (settled) return;
        if (retried) {
          settled = true;
          lastError = 'The Cast device refused the stream (' + err + ').';
          return;
        }
        retry('it was refused (' + err + ')');
      });
    }

    function retry(why) {
      if (settled || retried) return;
      retried = true;
      log('retrying the load: ' + why);
      attempt();
    }

    attempt();
    setTimeout(function () { retry('the receiver did not answer in time'); }, RETRY_AFTER_MS);
  }

  window.servalCast = {
    /**
     * Loads the sender SDK and starts looking for `appId`.
     *
     * Called when a camera screen opens, with the application this deployment registered. Safe to
     * call repeatedly and with a different id — the SDK is loaded once, and the id is applied as
     * soon as it is ready.
     */
    initialise: function (appId) {
      wantedAppId = appId || null;
      loadSdk();
      if (ready && wantedAppId) configure(wantedAppId);
    },

    /** Whether a receiver is reachable AND this deployment has a receiver to launch. */
    available: function () {
      return ready && receiverAvailable && currentAppId !== null;
    },

    casting: function () {
      return session() !== null;
    },

    /** The last failure, cleared by reading it — Dart shows it once and moves on. */
    takeError: function () {
      var error = lastError;
      lastError = '';
      return error;
    },

    /**
     * Casts one camera. `appId` is which receiver to launch, `url` what to hand it, `live`
     * whether that URL is the live camera or a recording — the two want different stream types —
     * and `startSeconds` how far into a recording to open, which is ignored when live.
     */
    start: function (appId, url, title, live, startSeconds) {
      lastError = '';

      if (!ready) {
        lastError = 'Casting is not available in this browser.';
        return;
      }

      if (!configure(appId)) {
        lastError = 'Casting is not available in this browser.';
        return;
      }

      // Already casting: load into the session that exists rather than asking for one.
      //
      // requestSession() is how a viewer *chooses* a device, and it puts Cast's own dialog on the
      // screen to do it. Calling it while a session is running therefore interrupts somebody who
      // has already chosen — which is what scrubbing outside the cast window was doing, popping the
      // device and volume dialog onto the phone in the middle of a seek.
      var existing = session();
      if (existing) {
        load(existing, mediaInfo(url, title, live), title, startSeconds || 0);
        return;
      }

      cast.framework.CastContext.getInstance().requestSession().then(function () {
        var active = session();
        if (!active) {
          lastError = 'No Cast device was chosen.';
          return;
        }

        load(active, mediaInfo(url, title, live), title, startSeconds || 0);
      }, function (err) {
        // Cancelling the device picker arrives here too, and is not worth reporting.
        if (err !== 'cancel') lastError = 'Could not start casting (' + err + ').';
      });
    },

    /**
     * Moves the television to `seconds` into whatever it is already playing.
     *
     * Used when somebody scrubs the timeline here while a recording is on screen there. A seek
     * rather than a fresh load because a load restarts the receiver's media — several seconds of
     * black — where this is immediate, and because the playlist already covers the whole window.
     * Silent when nothing is playing: the caller cannot know that without asking, and a scrub is
     * not the moment to report it.
     */
    seek: function (seconds) {
      var active = session();
      if (!active) return;

      var media = active.getMediaSession();
      if (!media) return;

      var request = new chrome.cast.media.SeekRequest();
      request.currentTime = seconds;

      // Keep playing across the jump — the default resumes whatever state it was in, and a scrub
      // landing on a paused television reads as a seek that did not work.
      request.resumeState = chrome.cast.media.ResumeState.PLAYBACK_START;

      media.seek(request, function () {
        log('sought to ' + seconds.toFixed(1) + 's');
      }, function (err) {
        lastError = 'The Cast device would not seek (' + err + ').';
      });
    },

    stop: function () {
      var active = session();
      if (active) active.endSession(true);
    }
  };
})();
