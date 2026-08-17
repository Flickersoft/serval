using Serval.Server.Alerts;
using Serval.Server.Configuration;

namespace Serval.Server.Push;

/// <summary>
/// Whether somebody has just been told this, and is therefore not worth telling again.
///
/// <para>The last link in the chain that decides whether a notification happens, and the only one
/// that is about <em>when</em> rather than <em>what</em>. The three before it —
/// <c>Detection:AlertClasses</c>, a camera's own override, and
/// <see cref="Preferences.CameraNotificationRule"/> — decide whether an alert exists and whether
/// this person wants it. This asks the remaining question: they wanted it, but they were
/// interrupted about exactly this a moment ago.</para>
///
/// <para><b>Nothing here touches the queue.</b> The row is already written and its clip is already
/// on its way by the time this runs, which is the whole reason the cooldown lives at the push layer
/// rather than in <c>ObjectEventPolicy</c> or <c>AlertService</c>. A held notification is a phone
/// left alone, not an alert that did not happen — the screen still shows both, and a suppression
/// that reached the queue would be a screen lying about what the camera saw.</para>
///
/// <para><b>Not synchronised, and must not need to be.</b> <see cref="AlertNotifier"/>'s channel is
/// created with <c>SingleReader = true</c> and its drain loop is the only caller, so this runs one
/// alert at a time. It is held as a private field there rather than registered in DI precisely so
/// that stays true: nothing else in the process holds a reference, and no future endpoint can reach
/// in. Registering it is the change that would silently make this wrong.</para>
///
/// <para>Shaped after <c>SoundEventPolicy</c>, which solves the same problem one layer down and
/// where the same two properties are load-bearing: the clock arrives as an argument so the window
/// is testable without sleeping, and a suppressed event does not restamp.</para>
/// </summary>
internal sealed class NotificationCooldown
{
    /// <summary>
    /// How old an entry must be before it is forgotten. Anchored to the longest window anybody may
    /// configure, so a sweep can never drop a key that is still deciding something.
    /// </summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromSeconds(PushOptions.MaxCooldownSeconds);

    /// <summary>How often the sweep is worth doing at all. It walks the whole dictionary.</summary>
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(5);

    private readonly Dictionary<PushKey, DateTimeOffset> _lastPushed = [];
    private DateTimeOffset _sweptAt = DateTimeOffset.MinValue;

    /// <summary>Notifications held back because the same thing had just gone out.</summary>
    public long Suppressed { get; private set; }

    /// <summary>Notifications this let through.</summary>
    public long Pushed { get; private set; }

    /// <summary>How many windows are being remembered. What the sweep is measured by.</summary>
    public int Tracked => _lastPushed.Count;

    /// <summary>
    /// Whether this alert should be pushed to this person, and stamps the window when it should.
    /// </summary>
    /// <param name="cooldownSeconds">
    /// This person's window for this camera, already resolved against the deployment's default by
    /// <c>UserPreferences.CooldownSecondsFor</c>. Zero or less sends everything.
    /// </param>
    /// <param name="now">Injected so the window is testable without sleeping.</param>
    public bool Allows(
        string userId,
        string cameraId,
        AlertKind kind,
        string label,
        int cooldownSeconds,
        DateTimeOffset now)
    {
        Sweep(now);

        // Ahead of the key, so switching the cooldown off does not leave a dictionary quietly
        // filling with entries nothing will ever read.
        if (cooldownSeconds <= 0)
        {
            Pushed++;
            return true;
        }

        var key = new PushKey(userId, cameraId, kind, label.ToLowerInvariant());

        if (_lastPushed.TryGetValue(key, out DateTimeOffset last)
            && now - last < TimeSpan.FromSeconds(cooldownSeconds))
        {
            // Deliberately not restamped. The window runs from the last notification that was
            // actually sent, not from the last one considered — a camera alerting once a second
            // would otherwise push its own window ahead of itself forever and never be heard from
            // again.
            Suppressed++;
            return false;
        }

        _lastPushed[key] = now;
        Pushed++;
        return true;
    }

    /// <summary>
    /// Forgets windows that have expired.
    ///
    /// Bounded in practice without this — the keys are accounts times cameras times the labels a
    /// detector can emit, which is thousands of entries at the outside. The sweep is here because
    /// accounts are deleted, cameras are removed and a model swap retires a whole vocabulary, and
    /// none of those should leave something behind for the life of the process.
    /// </summary>
    private void Sweep(DateTimeOffset now)
    {
        if (now - _sweptAt < SweepEvery)
        {
            return;
        }

        _sweptAt = now;

        // Collected first: a dictionary cannot be written while it is being enumerated. Null until
        // there is something to drop, because the usual answer is nothing.
        List<PushKey>? drop = null;

        foreach ((PushKey key, DateTimeOffset at) in _lastPushed)
        {
            if (now - at >= Horizon)
            {
                (drop ??= []).Add(key);
            }
        }

        if (drop is null)
        {
            return;
        }

        foreach (PushKey key in drop)
        {
            _lastPushed.Remove(key);
        }
    }

    /// <summary>
    /// One person's window for one thing on one camera.
    ///
    /// <para><see cref="Kind"/> is in the key rather than implied by the label because the two
    /// vocabularies overlap: <c>Dog</c> is a COCO class and an AudioSet label both. Without it a
    /// barking dog would silence a visible one, which is the pair a camera is most likely to see
    /// together and the pair a person would most want told apart.</para>
    ///
    /// <para><see cref="Label"/> is lowercased by the caller rather than compared with a custom
    /// comparer, because a record struct's equality is ordinal and cannot be given one.
    /// <c>CameraNotificationRule.Allows</c> already matches labels case-insensitively, and a
    /// cooldown that did not would give <c>Person</c> and <c>person</c> a window each.</para>
    /// </summary>
    private readonly record struct PushKey(
        string UserId, string CameraId, AlertKind Kind, string Label);
}
