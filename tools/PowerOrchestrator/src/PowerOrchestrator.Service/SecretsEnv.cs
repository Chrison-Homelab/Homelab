namespace PowerOrchestrator.Service;

/// <summary>
/// Loads a gitignored <c>secrets.env</c> (KEY=VALUE, optional <c>export </c> prefix) into process
/// environment variables, mirroring Infrastructure/engine/Shapes/SecretsEnv.cs. Existing env vars
/// win (systemd's <c>EnvironmentFile=</c> / an explicit <c>set -a; . secrets.env</c> take
/// precedence), so this is just a dev-box convenience. Searches up from the working directory,
/// or honors <c>ORCH_SECRETS_ENV</c>.
/// </summary>
public static class SecretsEnv
{
    public static void LoadIntoEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("ORCH_SECRETS_ENV") ?? FindUp("secrets.env");
        if (path is null || !File.Exists(path)) return;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim().Trim('"', '\'');
            // Don't clobber a value already in the environment.
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, val);
        }
    }

    private static string? FindUp(string fileName)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
