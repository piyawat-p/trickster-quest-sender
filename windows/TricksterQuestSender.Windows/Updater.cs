using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace QuestSenderWin;

/// <summary>Checks GitHub Releases for a new version (only when installed via Setup.exe).</summary>
internal sealed class Updater
{
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public static string? RepoUrl
    {
        get
        {
            var value = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "UpdateRepoUrl")?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    /// <returns>The new version, downloaded and ready to apply, or null.</returns>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (RepoUrl is null) return null;
        _manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
        if (!_manager.IsInstalled) return null; // running from a build folder, not installed
        var info = await _manager.CheckForUpdatesAsync();
        if (info is null) return null;
        await _manager.DownloadUpdatesAsync(info);
        _pending = info;
        return info.TargetFullRelease.Version.ToString();
    }

    public void ApplyAndRestart()
    {
        if (_manager is not null && _pending is not null)
            _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
