using Serval.Server.Alerts;
using Serval.Server.Configuration;
using Serval.Server.Push;

namespace Serval.Server.Tests;

/// <summary>
/// The fourth link in the chain that decides whether somebody is interrupted, and the only one that
/// is about <em>when</em> rather than <em>what</em>.
///
/// <para>By the time anything here is asked, an alert row exists and this person has said they want
/// it — see <see cref="NotificationRuleTests"/> for the three links before. What is left is whether
/// they were told this exact thing a moment ago. Nothing here can stop a row being written, which is
/// the property the whole design turns on: a held notification is a phone left alone, not an alert
/// that did not happen.</para>
///
/// <para>The two that would be quietly wrong and still pass a casual read are
/// <see cref="TheWindowRunsFromTheLastPushRatherThanTheLastAlert"/> and
/// <see cref="ABarkingDogDoesNotSilenceAVisibleOne"/>.</para>
/// </summary>
public class NotificationCooldownTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private const int TwoMinutes = 120;

    [Fact]
    public void TheFirstAlertForACameraIsSent()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, label: "person", at: Noon));
        Assert.Equal(1, cooldown.Pushed);
        Assert.Equal(0, cooldown.Suppressed);
    }

    [Fact]
    public void TheSameLabelInsideTheWindowIsSuppressed()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, label: "person", at: Noon));
        Assert.False(Allows(cooldown, label: "person", at: Noon.AddSeconds(20)));
        Assert.Equal(1, cooldown.Suppressed);
    }

    [Fact]
    public void TheSameLabelOutsideTheWindowIsSentAgain()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, label: "person", at: Noon));
        Assert.True(Allows(cooldown, label: "person", at: Noon.AddSeconds(TwoMinutes)));
    }

    /// <summary>
    /// Why the label is in the key at all. Somebody walking about is one thing; a car arriving while
    /// they do is another, and a cooldown that swallowed it would be hiding the more interesting of
    /// the two.
    /// </summary>
    [Fact]
    public void ADifferentLabelOnTheSameCameraIsNotSuppressed()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, label: "person", at: Noon));
        Assert.True(Allows(cooldown, label: "car", at: Noon.AddSeconds(20)));
    }

    /// <summary>
    /// Why <see cref="AlertKind"/> is in the key. <c>Dog</c> is a COCO class and an AudioSet label
    /// both, and they are the pair a camera is most likely to produce together.
    /// </summary>
    [Fact]
    public void ABarkingDogDoesNotSilenceAVisibleOne()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, kind: AlertKind.Sound, label: "Dog", at: Noon));
        Assert.True(Allows(
            cooldown, kind: AlertKind.Object, label: "Dog", at: Noon.AddSeconds(5)));
    }

    [Fact]
    public void AnotherCamerasAlertIsNotSuppressed()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, camera: "front-door", at: Noon));
        Assert.True(Allows(cooldown, camera: "back-yard", at: Noon.AddSeconds(20)));
    }

    /// <summary>One person's phone going quiet must not quiet the household's.</summary>
    [Fact]
    public void AnotherAccountIsNotSuppressed()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, user: "jeremiah", at: Noon));
        Assert.True(Allows(cooldown, user: "someone-else", at: Noon.AddSeconds(20)));
    }

    [Fact]
    public void AZeroCooldownSuppressesNothing()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, seconds: 0, at: Noon));
        Assert.True(Allows(cooldown, seconds: 0, at: Noon.AddSeconds(1)));
        Assert.True(Allows(cooldown, seconds: 0, at: Noon.AddSeconds(2)));

        // And it stores nothing, so switching the cooldown off cannot leave a dictionary filling
        // with entries that nothing will ever read.
        Assert.Equal(0, cooldown.Tracked);
    }

    /// <summary>Mirrors <c>NotificationRuleTests.LabelMatchingIgnoresCase</c>: the rule that decides
    /// whether a label is wanted is case-insensitive, so the window over it has to be too.</summary>
    [Fact]
    public void LabelMatchingIgnoresCase()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, label: "Person", at: Noon));
        Assert.False(Allows(cooldown, label: "person", at: Noon.AddSeconds(20)));
    }

    /// <summary>
    /// The one that would be silently wrong. If a suppressed alert restamped the window, a camera
    /// alerting more often than the window is long would push its own window ahead of itself
    /// forever and never be heard from again — the exact opposite of the setting's purpose, and
    /// invisible except to somebody wondering why a busy camera went quiet.
    /// </summary>
    [Fact]
    public void TheWindowRunsFromTheLastPushRatherThanTheLastAlert()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, at: Noon));
        Assert.False(Allows(cooldown, at: Noon.AddSeconds(100)));
        Assert.True(Allows(cooldown, at: Noon.AddSeconds(TwoMinutes)));
    }

    /// <summary>
    /// A module may post telemetry dated before what has already been seen. Dating the window from
    /// the alert rather than the wall clock means the arithmetic goes negative here, and negative is
    /// inside the window.
    /// </summary>
    [Fact]
    public void AnAlertArrivingOutOfOrderIsSuppressed()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, at: Noon));
        Assert.False(Allows(cooldown, at: Noon.AddSeconds(-30)));
    }

    [Fact]
    public void StaleKeysAreForgotten()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, camera: "front-door", at: Noon));
        Assert.True(Allows(cooldown, camera: "back-yard", at: Noon));
        Assert.Equal(2, cooldown.Tracked);

        // A sweep only runs when one is due, and only drops what is past the longest window anybody
        // could have configured — so this is the first call that can forget anything.
        DateTimeOffset later = Noon.AddSeconds(PushOptions.MaxCooldownSeconds + 1);
        Assert.True(Allows(cooldown, camera: "side-gate", at: later));

        Assert.Equal(1, cooldown.Tracked);
    }

    /// <summary>A live window is never swept, however long the process has been up.</summary>
    [Fact]
    public void ASweepKeepsAWindowThatIsStillDeciding()
    {
        var cooldown = new NotificationCooldown();

        Assert.True(Allows(cooldown, camera: "front-door", at: Noon));

        // Far enough on to trigger a sweep, but the entry is inside the horizon.
        DateTimeOffset later = Noon.AddSeconds(PushOptions.MaxCooldownSeconds - 30);
        Assert.True(Allows(cooldown, camera: "back-yard", at: later));

        Assert.Equal(2, cooldown.Tracked);
    }

    private static bool Allows(
        NotificationCooldown cooldown,
        DateTimeOffset at,
        string user = "jeremiah",
        string camera = "front-door",
        AlertKind kind = AlertKind.Object,
        string label = "person",
        int seconds = TwoMinutes) =>
        cooldown.Allows(user, camera, kind, label, seconds, at);
}
