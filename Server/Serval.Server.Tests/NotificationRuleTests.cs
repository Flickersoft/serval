using Serval.Server.Alerts;
using Serval.Server.Configuration;
using Serval.Server.Preferences;

namespace Serval.Server.Tests;

/// <summary>
/// The third link in the chain that decides whether somebody is interrupted.
///
/// <para>The first two — the deployment's <c>AlertClasses</c> and a camera's override — run in the
/// detection loop and decide whether an alert row exists at all. By the time anything here is
/// asked, a row exists, which is what makes these rules narrowing-only and why nothing in this file
/// consults a camera: the alert's existence is already the camera's answer.</para>
///
/// <para>The distinction worth protecting is <b>null versus empty</b>. Null inherits and empty
/// means nothing, and the two are one keystroke apart in every client that will ever write them.</para>
/// </summary>
public class NotificationRuleTests
{
    [Fact]
    public void ACameraWithNoRuleNotifies()
    {
        UserPreferences preferences = UserPreferences.Empty("jeremiah");

        Assert.True(preferences.WantsNotifiedOf("front-door", AlertKind.Object, "person"));
        Assert.True(preferences.WantsNotifiedOf("back-yard", AlertKind.Sound, "Glass"));
    }

    [Fact]
    public void TheMasterSwitchSilencesEverything()
    {
        var preferences = new UserPreferences
        {
            UserId = "jeremiah",
            NotificationsEnabled = false,
            Notifications = [new CameraNotificationRule { CameraId = "front-door" }],
        };

        Assert.False(preferences.WantsNotifiedOf("front-door", AlertKind.Object, "person"));
        Assert.False(preferences.WantsNotifiedOf("anything-else", AlertKind.Object, "person"));
    }

    [Fact]
    public void ADisabledRuleSilencesOnlyItsOwnCamera()
    {
        var preferences = new UserPreferences
        {
            UserId = "jeremiah",
            Notifications =
            [
                new CameraNotificationRule { CameraId = "front-door", Enabled = false },
            ],
        };

        Assert.False(preferences.WantsNotifiedOf("front-door", AlertKind.Object, "person"));
        Assert.True(preferences.WantsNotifiedOf("back-yard", AlertKind.Object, "person"));
    }

    /// <summary>Null is "whatever this camera alerts on", which is the default a person never set.</summary>
    [Fact]
    public void NullClassesInherit()
    {
        var rule = new CameraNotificationRule { CameraId = "front-door" };

        Assert.True(rule.Allows(AlertKind.Object, "person"));
        Assert.True(rule.Allows(AlertKind.Object, "car"));
        Assert.True(rule.Allows(AlertKind.Sound, "Glass"));
    }

    /// <summary>
    /// An empty list is a choice, not an absence — it is how "this camera, but never for objects"
    /// is said, and reading it as inherit would turn muting into its opposite.
    /// </summary>
    [Fact]
    public void EmptyClassesAllowNothingOfThatKind()
    {
        var rule = new CameraNotificationRule
        {
            CameraId = "front-door",
            ObjectClasses = [],
        };

        Assert.False(rule.Allows(AlertKind.Object, "person"));

        // Sounds are untouched: they have their own list, and it is still null.
        Assert.True(rule.Allows(AlertKind.Sound, "Glass"));
    }

    [Fact]
    public void ObjectsAndSoundsAreFilteredSeparately()
    {
        var rule = new CameraNotificationRule
        {
            CameraId = "back-yard",
            ObjectClasses = ["person"],
            SoundLabels = ["Glass"],
        };

        Assert.True(rule.Allows(AlertKind.Object, "person"));
        Assert.False(rule.Allows(AlertKind.Object, "Glass"));

        Assert.True(rule.Allows(AlertKind.Sound, "Glass"));
        Assert.False(rule.Allows(AlertKind.Sound, "person"));
    }

    /// <summary>
    /// The detector spells its classes lowercase and the audio model capitalises its phrases; a
    /// person picking from a list gets whatever the list held. Matching case-sensitively would make
    /// a rule that looks right silently match nothing.
    /// </summary>
    [Fact]
    public void LabelMatchingIgnoresCase()
    {
        var rule = new CameraNotificationRule
        {
            CameraId = "front-door",
            ObjectClasses = ["Person"],
            SoundLabels = ["glass"],
        };

        Assert.True(rule.Allows(AlertKind.Object, "person"));
        Assert.True(rule.Allows(AlertKind.Sound, "Glass"));
    }

    /// <summary>
    /// A rule naming something its camera does not alert on is stored and harmless. It matches
    /// nothing today because no such row is ever raised, and starts working the day an admin widens
    /// the camera — which is why the write path does not validate against that set.
    /// </summary>
    [Fact]
    public void NamingAClassTheCameraDoesNotRaiseIsHarmless()
    {
        var rule = new CameraNotificationRule
        {
            CameraId = "front-door",
            ObjectClasses = ["person", "elephant"],
        };

        Assert.True(rule.Allows(AlertKind.Object, "person"));

        // Nothing rejects "elephant"; there simply is never an alert carrying it.
        Assert.True(rule.Allows(AlertKind.Object, "elephant"));
    }

    [Fact]
    public void RulesApplyToTheirOwnCameraOnly()
    {
        var preferences = new UserPreferences
        {
            UserId = "jeremiah",
            Notifications =
            [
                new CameraNotificationRule { CameraId = "front-door", ObjectClasses = ["person"] },
                new CameraNotificationRule { CameraId = "driveway", ObjectClasses = ["car"] },
            ],
        };

        Assert.True(preferences.WantsNotifiedOf("front-door", AlertKind.Object, "person"));
        Assert.False(preferences.WantsNotifiedOf("front-door", AlertKind.Object, "car"));

        Assert.True(preferences.WantsNotifiedOf("driveway", AlertKind.Object, "car"));
        Assert.False(preferences.WantsNotifiedOf("driveway", AlertKind.Object, "person"));
    }

    [Fact]
    public void RejectsTwoRulesForOneCamera()
    {
        Assert.Throws<PreferencesValidationException>(() =>
            UserPreferencesRepository.ValidateRules(
            [
                new CameraNotificationRule { CameraId = "front-door" },
                new CameraNotificationRule { CameraId = "front-door", Enabled = false },
            ]));
    }

    [Fact]
    public void RejectsARuleNamingNoCamera()
    {
        Assert.Throws<PreferencesValidationException>(() =>
            UserPreferencesRepository.ValidateRules(
                [new CameraNotificationRule { CameraId = "  " }]));
    }

    [Fact]
    public void AcceptsAnOrdinarySetOfRules()
    {
        UserPreferencesRepository.ValidateRules(
        [
            new CameraNotificationRule { CameraId = "front-door", ObjectClasses = ["person"] },
            new CameraNotificationRule { CameraId = "driveway", Enabled = false },
            new CameraNotificationRule { CameraId = "back-yard", SoundLabels = [] },
        ]);
    }

    [Fact]
    public void ACameraWithNoRuleUsesTheDeploymentDefault()
    {
        UserPreferences preferences = UserPreferences.Empty("jeremiah");

        Assert.Equal(120, preferences.CooldownSecondsFor("front-door", deploymentDefault: 120));
    }

    /// <summary>Null inherits, exactly as a null class list does. Somebody who narrowed which
    /// objects reach them has said nothing about how often.</summary>
    [Fact]
    public void ANullCooldownOnARuleInherits()
    {
        var preferences = new UserPreferences
        {
            UserId = "jeremiah",
            Notifications =
            [
                new CameraNotificationRule { CameraId = "front-door", ObjectClasses = ["person"] },
            ],
        };

        Assert.Equal(120, preferences.CooldownSecondsFor("front-door", deploymentDefault: 120));
    }

    /// <summary>
    /// Zero is a decision and null is the absence of one. Somebody who has decided this camera
    /// should always reach them wants that to survive an admin raising the deployment's default,
    /// and only a stored zero says so.
    /// </summary>
    [Fact]
    public void AZeroCooldownIsAChoiceRatherThanInheritance()
    {
        var preferences = new UserPreferences
        {
            UserId = "jeremiah",
            Notifications =
            [
                new CameraNotificationRule { CameraId = "front-door", CooldownSeconds = 0 },
            ],
        };

        Assert.Equal(0, preferences.CooldownSecondsFor("front-door", deploymentDefault: 600));
    }

    [Fact]
    public void AStoredCooldownBeatsTheDeploymentDefault()
    {
        var preferences = new UserPreferences
        {
            UserId = "jeremiah",
            Notifications =
            [
                new CameraNotificationRule { CameraId = "front-door", CooldownSeconds = 900 },
            ],
        };

        Assert.Equal(900, preferences.CooldownSecondsFor("front-door", deploymentDefault: 120));
        Assert.Equal(120, preferences.CooldownSecondsFor("back-yard", deploymentDefault: 120));
    }

    [Fact]
    public void RejectsANegativeCooldown()
    {
        Assert.Throws<PreferencesValidationException>(() =>
            UserPreferencesRepository.ValidateRules(
                [new CameraNotificationRule { CameraId = "front-door", CooldownSeconds = -1 }]));
    }

    [Fact]
    public void RejectsACooldownPastTheCeiling()
    {
        Assert.Throws<PreferencesValidationException>(() =>
            UserPreferencesRepository.ValidateRules(
            [
                new CameraNotificationRule
                {
                    CameraId = "front-door",
                    CooldownSeconds = PushOptions.MaxCooldownSeconds + 1,
                },
            ]));
    }

    /// <summary>
    /// The settings page draws its slider from the catalogue and the preferences endpoint validates
    /// against the constant. Two literals would drift, and drift here means a number the settings
    /// page offers and the endpoint refuses.
    /// </summary>
    [Fact]
    public void TheCatalogueBoundMatchesWhatARuleMayStore()
    {
        SettingDescriptor? descriptor = SettingsCatalog.Find("Serval:Push:CooldownSeconds");

        Assert.NotNull(descriptor);
        Assert.Equal(PushOptions.MaxCooldownSeconds, descriptor.Max);
        Assert.Equal(0, descriptor.Min);
    }
}
