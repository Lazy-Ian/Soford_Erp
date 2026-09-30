using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Resolves the data directory (Soford:DataPath, default ContentRoot/App_Data).</summary>
public sealed class AppPaths
{
    public AppPaths(string dataPath)
    {
        DataPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataPath));
        Directory.CreateDirectory(DataPath);
    }

    public string DataPath { get; }

    public string File(params string[] segments) => Path.Combine([DataPath, .. segments]);

    public static AppPaths FromConfiguration(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["Soford:DataPath"];
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "App_Data")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
        return new AppPaths(path);
    }
}

public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static async Task<T?> ReadAsync<T>(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return default;
        }

        await using var stream = System.IO.File.OpenRead(path);
        if (stream.Length == 0)
        {
            // WriteAtomicAsync never produces an empty file, so this is damage (e.g. a power cut). Treating it as
            // "no data" would let the next write replace everything with an empty list.
            throw new InvalidDataException($"数据文件 {Path.GetFileName(path)} 为空（可能已损坏），请从备份恢复。");
        }

        return await JsonSerializer.DeserializeAsync<T>(stream, Options);
    }

    /// <summary>Writes to a temp file first, then atomically replaces the target so a crash never leaves a truncated file.</summary>
    public static async Task WriteAtomicAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = System.IO.File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options);
                // Reach the disk before the rename, or a power cut can leave the renamed file empty.
                stream.Flush(flushToDisk: true);
            }

            System.IO.File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { System.IO.File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }
}
