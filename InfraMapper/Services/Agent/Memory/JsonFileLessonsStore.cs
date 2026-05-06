using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace InfraMapper.Services.Agent.Memory;

public sealed class JsonFileLessonsStore : ILessonsStore
{
    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".inframapper", "lessons.json");

    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public JsonFileLessonsStore(IHostEnvironment environment)
    {
        _filePath = Path.Combine(environment.ContentRootPath, ".inframapper", "lessons.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        if (!File.Exists(_filePath) && File.Exists(LegacyFilePath))
            File.Copy(LegacyFilePath, _filePath);
    }

    public void Write(Lesson lesson)
    {
        var lessons = Load();
        lessons.Add(lesson);

        using var fs = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.None);
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
        if (!File.Exists(_filePath)) return [];
        try
        {
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<List<Lesson>>(fs, JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
