namespace Lumiere.Api;

/// <summary>Development-only loader for the repo-root .env. Never overrides real environment variables; never logs values.</summary>
public static class DotEnv
{
    public static void LoadFromRepoRoot()
    {
        var path = FindRepoFile(Directory.GetCurrentDirectory()) ?? FindRepoFile(AppContext.BaseDirectory);
        if (path is null) return;
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0 && Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string? FindRepoFile(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lumiere.sln")))
                return File.Exists(Path.Combine(dir.FullName, ".env")) ? Path.Combine(dir.FullName, ".env") : null;
        return null;
    }
}
