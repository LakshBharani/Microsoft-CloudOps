using System.ComponentModel;
using System.Text.Json;
using InfraMapper.Services.Agent.Memory;

namespace InfraMapper.Services.Agent.Tools;

public sealed class ReflectorTools
{
    private readonly ILessonsStore _lessonsStore;

    public ReflectorTools(ILessonsStore lessonsStore) =>
        _lessonsStore = lessonsStore;

    [Description("Record a lesson from this deployment for use in future planning sessions. " +
                 "Call this after every deployment (success or failure) to build institutional memory.")]
    public string WriteLesson(
        [Description("Brief summary of what was attempted (e.g. 'Deploy storage account infraops001 to West US 2')")] string intentSummary,
        [Description("Azure resource types involved (e.g. ['Microsoft.Storage/storageAccounts', 'Microsoft.Network/virtualNetworks'])")] string[] appliesTo,
        [Description("What failed or caused retries, if anything (null if first-try success)")] string? whatFailed,
        [Description("Key recommendation for future deployments of this type")] string recommendation)
    {
        var lesson = new Lesson
        {
            IntentSummary = intentSummary,
            AppliesTo = appliesTo,
            WhatFailed = whatFailed,
            Recommendation = recommendation,
        };

        _lessonsStore.Write(lesson);

        return JsonSerializer.Serialize(new
        {
            lesson_id = lesson.Id,
            recorded = true,
            message = "Lesson recorded successfully.",
        });
    }
}
