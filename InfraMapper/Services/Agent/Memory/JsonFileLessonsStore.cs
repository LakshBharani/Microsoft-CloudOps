using System.Text.Json;

namespace InfraMapper.Services.Agent.Memory;

/// <summary>
/// Persists deployment lessons to ~/.inframapper/lessons.json.
/// Uses a file lock on writes so multiple processes don't corrupt the file.
/// Loads the full list on every Query (file is small — single demo deployment data).
/// </summary>
public sealed class JsonFileLessonsStore : ILessonsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".inframapper", "lessons.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public JsonFileLessonsStore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
    }

    public void Write(Lesson lesson)
    {
        var lessons = Load();
        lessons.Add(lesson);

        using var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(fs, lessons, JsonOpts);
    }

    public IReadOnlyList<Lesson> Query(string[] resourceTypes)
    {
        if (resourceTypes.Length == 0) return Load();

        var normalized = resourceTypes
            .Select(r => r.ToLowerInvariant())
            .ToHashSet();

        return Load()
            .Where(l => l.AppliesTo.Any(t => normalized.Contains(t.ToLowerInvariant())))
            .ToList();
    }

    private List<Lesson> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<List<Lesson>>(fs, JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
