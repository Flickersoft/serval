using Serval.Server.Auth;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Preferences;

namespace Serval.Server.Backup;

/// <summary>
/// Gathers the four collections that hold configuration into one <see cref="ConfigBackupFile"/>.
///
/// <para>Reads only. Nothing here writes anything, including to disk — the file is built in memory
/// and handed straight to the response, so a backup never leaves a copy of every camera password on
/// the server for a later reader to find.</para>
/// </summary>
public sealed class ConfigBackupBuilder
{
    private readonly CameraRepository _cameras;
    private readonly ServerSettingsStore _settings;
    private readonly UserRepository _users;
    private readonly UserPreferencesRepository _preferences;

    public ConfigBackupBuilder(
        CameraRepository cameras,
        ServerSettingsStore settings,
        UserRepository users,
        UserPreferencesRepository preferences)
    {
        _cameras = cameras;
        _settings = settings;
        _users = users;
        _preferences = preferences;
    }

    public async Task<ConfigBackupFile> BuildAsync(
        string? actorUserId, CancellationToken cancellationToken = default)
    {
        // Deliberately NOT Camera.WithoutSecrets(). A backup that strips the ONVIF password and the
        // userinfo out of the stream URLs restores a list of cameras that cannot connect to
        // anything, and leaves the operator retyping the one thing they most wanted kept. Including
        // them is the whole reason this file carries a warning as its second key.
        List<Camera> cameras = await _cameras.ListAsync(cancellationToken);
        ServerSettingsDocument settings = await _settings.LoadAsync(cancellationToken);
        List<User> users = await _users.ListAsync(cancellationToken);
        List<UserPreferences> preferences = await _preferences.ListAsync(cancellationToken);

        return new ConfigBackupFile(
            Kind: ConfigBackupFile.FileKind,
            Warning: ConfigBackupFile.SecretWarning,
            CreatedAt: DateTimeOffset.UtcNow,
            CreatedBy: actorUserId,
            CreatedOn: Environment.MachineName,
            Settings: new Dictionary<string, string>(settings.Values, StringComparer.OrdinalIgnoreCase),
            Cameras: cameras,
            Users: [.. users.Select(u => new BackupUser(
                Username: u.Id,
                DisplayName: u.DisplayName,
                PasswordHash: u.PasswordHash,
                Role: u.Role,
                CreatedAt: u.CreatedAt))],
            Preferences: [.. preferences.Select(p => new BackupPreferences(
                UserId: p.UserId,
                WallLayout: [.. p.WallLayout.Select(t =>
                    new WallTilePayload(t.CameraId, t.Column, t.Row, t.ColumnSpan, t.RowSpan))],
                NotificationsEnabled: p.NotificationsEnabled,
                Notifications: [.. p.Notifications.Select(r =>
                    new CameraNotificationRulePayload(
                        r.CameraId,
                        r.Enabled,
                        r.ObjectClasses,
                        r.SoundLabels,
                        r.CooldownSeconds))]))]);
    }
}
