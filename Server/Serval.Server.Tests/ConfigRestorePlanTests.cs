using Serval.Server.Auth;
using Serval.Server.Backup;

namespace Serval.Server.Tests;

/// <summary>
/// What a restore decides before it writes anything.
///
/// <para>Both halves under test here are the parts a restore can get wrong quietly. A settings
/// merge that leaves half of an old list behind produces a list nobody configured, and it looks
/// fine until somebody counts. An account plan that demotes the wrong person produces a Server its
/// owner cannot administer, with no recovery path. Neither needs a database to be wrong, so neither
/// needs one to be tested.</para>
/// </summary>
public class ConfigRestorePlanTests
{
    private static Dictionary<string, string?> Plan(
        Dictionary<string, string> file,
        params string[] stored) =>
        ConfigRestoreService.PlanSettingsWrites(
            file, stored.ToHashSet(StringComparer.OrdinalIgnoreCase), [], []);

    private static Dictionary<string, string?> Plan(
        Dictionary<string, string> file,
        List<RestoreSkip> skips,
        List<string> notes,
        params string[] stored) =>
        ConfigRestoreService.PlanSettingsWrites(
            file, stored.ToHashSet(StringComparer.OrdinalIgnoreCase), skips, notes);

    // ---------------------------------------------------------------- settings

    [Fact]
    public void A_scalar_in_the_file_is_written()
    {
        Dictionary<string, string?> writes = Plan(new() { ["Serval:Media:RetentionDays"] = "21" });

        Assert.Equal("21", Assert.Single(writes).Value);
    }

    /// <summary>
    /// The whole of merge, in one assertion: this Server's own override of a setting the file is
    /// silent about is not touched, and so is not in the write set at all.
    /// </summary>
    [Fact]
    public void A_setting_the_file_does_not_name_is_left_alone()
    {
        Dictionary<string, string?> writes = Plan(
            new() { ["Serval:Media:RetentionDays"] = "21" },
            "Serval:Ai:Detection:ScoreThreshold");

        Assert.DoesNotContain("Serval:Ai:Detection:ScoreThreshold", writes.Keys);
    }

    /// <summary>
    /// The reason a list cannot be merged index by index. Five stored, two in the file: writing only
    /// the file's two would leave 2–4 behind and the binder would read a five-entry list that is
    /// neither this Server's nor the file's.
    /// </summary>
    [Fact]
    public void A_list_the_file_shortens_loses_its_tail()
    {
        Dictionary<string, string?> writes = Plan(
            new()
            {
                ["Serval:Ai:Sound:AlertLabels:0"] = "Glass",
                ["Serval:Ai:Sound:AlertLabels:1"] = "Siren",
            },
            "Serval:Ai:Sound:AlertLabels:0",
            "Serval:Ai:Sound:AlertLabels:1",
            "Serval:Ai:Sound:AlertLabels:2",
            "Serval:Ai:Sound:AlertLabels:3",
            "Serval:Ai:Sound:AlertLabels:4");

        Assert.Equal("Glass", writes["Serval:Ai:Sound:AlertLabels:0"]);
        Assert.Equal("Siren", writes["Serval:Ai:Sound:AlertLabels:1"]);
        Assert.Null(writes["Serval:Ai:Sound:AlertLabels:2"]);
        Assert.Null(writes["Serval:Ai:Sound:AlertLabels:3"]);
        Assert.Null(writes["Serval:Ai:Sound:AlertLabels:4"]);
    }

    /// <summary>
    /// And the boundary that keeps that from becoming a replace: clearing happens only for a list
    /// the file actually names. A different list this Server holds is untouched.
    /// </summary>
    [Fact]
    public void A_list_the_file_does_not_name_keeps_every_entry()
    {
        Dictionary<string, string?> writes = Plan(
            new() { ["Serval:Ai:Sound:AlertLabels:0"] = "Glass" },
            "Serval:Ai:Sound:AlertLabels:0",
            "Serval:Ai:Detection:Classes:0",
            "Serval:Ai:Detection:Classes:1");

        Assert.DoesNotContain("Serval:Ai:Detection:Classes:0", writes.Keys);
        Assert.DoesNotContain("Serval:Ai:Detection:Classes:1", writes.Keys);
    }

    [Fact]
    public void A_list_this_server_has_never_stored_is_simply_written()
    {
        Dictionary<string, string?> writes = Plan(new()
        {
            ["Serval:Ai:Sound:AlertLabels:0"] = "Glass",
            ["Serval:Ai:Sound:AlertLabels:1"] = "Siren",
        });

        Assert.Equal(2, writes.Count);
        Assert.DoesNotContain(writes, w => w.Value is null);
    }

    /// <summary>
    /// An environment-only key in the file is refused, not written. This is the allowlist doing the
    /// same job on a restored file that it does on a settings form — a hand-edited backup is exactly
    /// the way somebody would try to set a signing key through an API that has no field for it.
    /// </summary>
    [Fact]
    public void An_environment_only_key_is_skipped_with_a_reason()
    {
        List<RestoreSkip> skips = [];
        Dictionary<string, string?> writes = Plan(
            new() { ["Serval:Auth:SigningKey"] = "hunter2" }, skips, []);

        Assert.Empty(writes);
        Assert.Contains("not a setting this Server can change", Assert.Single(skips).Reason);
    }

    [Fact]
    public void A_value_outside_its_bounds_is_skipped_and_the_rest_still_lands()
    {
        List<RestoreSkip> skips = [];
        Dictionary<string, string?> writes = Plan(
            new()
            {
                ["Serval:Media:RetentionDays"] = "99999",
                ["Serval:Ai:Detection:ScoreThreshold"] = "0.4",
            },
            skips, []);

        Assert.Equal("0.4", Assert.Single(writes).Value);
        Assert.Equal("Serval:Media:RetentionDays", Assert.Single(skips).Item);
    }

    /// <summary>
    /// A restart-gated setting is stored but is not what the running Server is using, so a restore
    /// that moved one has to say so — otherwise the operator reads the value back, sees what they
    /// expected, and concludes it worked.
    /// </summary>
    [Fact]
    public void A_restart_gated_setting_says_so()
    {
        List<string> notes = [];
        Plan(new() { ["Serval:ServerAi:Enabled"] = "false" }, [], notes);

        Assert.Contains(notes, n => n.Contains("restart", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- accounts

    private static User Existing(string id, Role role) =>
        new() { Id = id, DisplayName = id, PasswordHash = Hash, Role = role };

    /// <summary>A hash the shape checker accepts: the v3 format marker followed by padding.</summary>
    private static readonly string Hash = Convert.ToBase64String([0x01, .. new byte[48]]);

    [Fact]
    public void An_account_the_server_does_not_have_is_created()
    {
        List<RestoreSkip> skips = [];
        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [new BackupUser("sam", "Sam", Hash, Role.Viewer, DateTimeOffset.UtcNow)],
            [Existing("jeremiah", Role.Admin)],
            "jeremiah",
            skips);

        UserPlan plan = Assert.Single(plans);
        Assert.True(plan.IsNew);
        Assert.Equal("sam", plan.Username);
        Assert.Equal(Hash, plan.PasswordHash);
    }

    [Fact]
    public void An_account_the_server_already_has_is_updated()
    {
        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [new BackupUser("sam", "Samantha", Hash, Role.Admin, DateTimeOffset.UtcNow)],
            [Existing("jeremiah", Role.Admin), Existing("sam", Role.Viewer)],
            "jeremiah",
            []);

        UserPlan plan = Assert.Single(plans);
        Assert.False(plan.IsNew);
        Assert.Equal("Samantha", plan.DisplayName);
        Assert.Equal(Role.Admin, plan.Role);
    }

    /// <summary>
    /// The guard the whole feature leans on. A restore is one unattended button press, and one that
    /// demotes or re-credentials the person pressing it takes away their ability to finish or
    /// reverse it — with no recovery path, since AdminBootstrap will not run once accounts exist.
    /// </summary>
    [Fact]
    public void The_account_running_the_restore_keeps_its_own_role_and_password()
    {
        List<RestoreSkip> skips = [];
        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [new BackupUser("jeremiah", "Jeremiah", Hash, Role.Viewer, DateTimeOffset.UtcNow)],
            [Existing("jeremiah", Role.Admin)],
            "jeremiah",
            skips);

        UserPlan plan = Assert.Single(plans);
        Assert.Null(plan.Role);
        Assert.Null(plan.PasswordHash);
        Assert.Equal("Jeremiah", plan.DisplayName);
        Assert.Contains("must not sign out the person running it", Assert.Single(skips).Reason);
    }

    [Fact]
    public void The_caller_is_matched_however_they_capitalised_their_username()
    {
        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [new BackupUser("Jeremiah", "Jeremiah", Hash, Role.Viewer, DateTimeOffset.UtcNow)],
            [Existing("jeremiah", Role.Admin)],
            "JEREMIAH",
            []);

        Assert.Null(Assert.Single(plans).Role);
    }

    /// <summary>
    /// Section-granular rather than per-row: the invariant is over the whole set, and refusing
    /// individual demotions would apply an arbitrary prefix of them and leave a state present in
    /// neither the file nor this Server.
    /// </summary>
    [Fact]
    public void A_file_that_would_leave_no_admin_changes_no_account_at_all()
    {
        List<RestoreSkip> skips = [];
        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [
                new BackupUser("sam", "Sam", Hash, Role.Viewer, DateTimeOffset.UtcNow),
                new BackupUser("kim", "Kim", Hash, Role.Viewer, DateTimeOffset.UtcNow),
            ],
            [Existing("sam", Role.Admin), Existing("kim", Role.Admin)],
            actorUserId: null,
            skips);

        Assert.Empty(plans);
        Assert.Contains("no Admin", Assert.Single(skips).Reason);
    }

    [Fact]
    public void An_admin_the_file_does_not_name_counts_towards_keeping_one()
    {
        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [new BackupUser("sam", "Sam", Hash, Role.Viewer, DateTimeOffset.UtcNow)],
            [Existing("sam", Role.Admin), Existing("kim", Role.Admin)],
            actorUserId: null,
            []);

        Assert.Equal(Role.Viewer, Assert.Single(plans).Role);
    }

    /// <summary>
    /// A hash that is not base64 makes PasswordHasher throw on every login for that username —
    /// a permanent 500 with no way back through the UI. Refusing the account is recoverable;
    /// writing it is not.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!!")]
    public void A_new_account_with_an_unreadable_password_is_not_created(string hash)
    {
        List<RestoreSkip> skips = [];
        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [new BackupUser("sam", "Sam", hash, Role.Viewer, DateTimeOffset.UtcNow)],
            [Existing("jeremiah", Role.Admin)],
            "jeremiah",
            skips);

        Assert.Empty(plans);
        Assert.Contains("not one this Server can read", Assert.Single(skips).Reason);
    }

    /// <summary>
    /// An existing account survives the same file: everything else about it restores and it keeps
    /// the password it already had, which is strictly better than being locked out of it.
    /// </summary>
    [Fact]
    public void An_existing_account_with_an_unreadable_password_keeps_the_one_it_has()
    {
        List<RestoreSkip> skips = [];
        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [new BackupUser("sam", "Samantha", "not base64!!", Role.Viewer, DateTimeOffset.UtcNow)],
            [Existing("jeremiah", Role.Admin), Existing("sam", Role.Viewer)],
            "jeremiah",
            skips);

        UserPlan plan = Assert.Single(plans);
        Assert.Null(plan.PasswordHash);
        Assert.Equal("Samantha", plan.DisplayName);
        Assert.Single(skips);
    }

    /// <summary>
    /// Restoring the same file twice must not sign everybody out twice. A hash equal to the stored
    /// one is not a password change, so it is not written — and it is the write that revokes the
    /// sessions. Idempotence is the property this feature leans on when it tells somebody to fix a
    /// skip and restore the same file again.
    /// </summary>
    [Fact]
    public void An_unchanged_password_is_not_a_password_change()
    {
        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [new BackupUser("sam", "Sam", Hash, Role.Viewer, DateTimeOffset.UtcNow)],
            [Existing("jeremiah", Role.Admin), Existing("sam", Role.Viewer)],
            "jeremiah",
            []);

        Assert.Null(Assert.Single(plans).PasswordHash);
    }

    [Fact]
    public void A_password_that_really_did_change_is_written()
    {
        string other = Convert.ToBase64String([0x01, .. new byte[49]]);

        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [new BackupUser("sam", "Sam", other, Role.Viewer, DateTimeOffset.UtcNow)],
            [Existing("jeremiah", Role.Admin), Existing("sam", Role.Viewer)],
            "jeremiah",
            []);

        Assert.Equal(other, Assert.Single(plans).PasswordHash);
    }

    [Fact]
    public void A_username_that_could_not_have_been_created_here_is_refused()
    {
        List<RestoreSkip> skips = [];
        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [new BackupUser("has space", "Nope", Hash, Role.Viewer, DateTimeOffset.UtcNow)],
            [Existing("jeremiah", Role.Admin)],
            "jeremiah",
            skips);

        Assert.Empty(plans);
        Assert.Single(skips);
    }

    [Fact]
    public void An_account_with_no_display_name_falls_back_to_its_username()
    {
        IReadOnlyList<UserPlan> plans = ConfigRestoreService.PlanUsers(
            [new BackupUser("sam", "  ", Hash, Role.Viewer, DateTimeOffset.UtcNow)],
            [Existing("jeremiah", Role.Admin)],
            "jeremiah",
            []);

        Assert.Equal("sam", Assert.Single(plans).DisplayName);
    }
}
