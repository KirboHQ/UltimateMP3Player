using System.Reflection;

namespace UltimateMP3Player.Services;

// Build-time settings from UltimateMP3Player.csproj.
public static class AppInfo
{
    private static string Meta(string key)
        => typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? "";

    // "owner/repo" publishing the releases.
    public static string GitHubRepo { get; } = Meta("GitHubRepo");

    // Discord application id for Rich Presence.
    public static string DiscordAppId { get; } = Meta("DiscordAppId");

    public static Version Version { get; } = typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => Version.ToString(3);

    public static string? RepoUrl => GitHubRepo.Length > 0 ? "https://github.com/" + GitHubRepo : null;

    public static string? LogoUrl => GitHubRepo.Length > 0 ? $"https://raw.githubusercontent.com/{GitHubRepo}/main/assets/logo.png" : null;
}
