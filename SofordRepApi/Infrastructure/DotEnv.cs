/// <summary>
/// Minimal .env loader for local development so `dotnet run` picks up the same settings as docker compose.
/// Keys use the environment-variable convention (Alibaba__AppKey) and only fill values that are still empty.
/// </summary>
public static class DotEnv
{
    public static void AddDotEnvDefaults(this ConfigurationManager config, params string[] files)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(File.Exists))
        {
            foreach (var (key, value) in Parse(File.ReadAllLines(file)))
            {
                values.TryAdd(key, value);
            }
        }

        var missing = values
            .Where(item => string.IsNullOrWhiteSpace(config[item.Key]) && !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(item => item.Key, item => item.Value);
        if (missing.Count > 0)
        {
            config.AddInMemoryCollection(missing);
        }
    }

    public static IEnumerable<KeyValuePair<string, string>> Parse(IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = line[..index].Trim();
            if (key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(index + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                value = value[1..^1];
            }

            yield return new(key.Replace("__", ":"), value);
        }
    }
}
