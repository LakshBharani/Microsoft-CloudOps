using System.ComponentModel;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel;

namespace InfraMapper.Services.Agent.Plugins;

public sealed class AzurePlanPlugin(AzureCrudTools tools)
{
    [KernelFunction("ask_clarifying_question")]
    [Description("Ask the user a targeted clarification question when required infrastructure details are missing or ambiguous.")]
    public string AskClarifyingQuestion(
        [Description("Short title for the question.")] string title,
        [Description("Specific prompt explaining what value is needed and why.")] string prompt,
        [Description("Whether the user may type a custom answer.")] bool allow_custom = true)
        => tools.AskClarifyingQuestion(title, prompt, allow_custom: allow_custom);

    [KernelFunction("whatif_arm_template")]
    [Description("Run ARM what-if analysis. Predicts resource changes without deploying. Call before create_plan for any Deploy operation.")]
    public Task<string> WhatIfArmTemplateAsync(
        [Description("Resource group for rg-scoped what-if. Omit for subscription-scoped.")] string? resource_group_name = null,
        [Description("Deployment location for subscription-scoped what-if.")] string? location = null,
        [Description("ARM template object.")] Dictionary<string, object?>? template = null,
        [Description("ARM parameters object.")] Dictionary<string, object?>? parameters = null,
        [Description("Deployment mode: Incremental or Complete.")] string mode = "Incremental",
        CancellationToken ct = default)
        => tools.WhatIfArmTemplateAsync(
            resourceGroupName: resource_group_name,
            location: location,
            template: template,
            parameters: parameters,
            mode: mode,
            cancellationToken: ct);

    [KernelFunction("create_plan")]
    [Description("Create a pending approval plan card. MANDATORY before any Azure write/update/delete/deploy. Execution blocks until user clicks Run plan.")]
    public string CreatePlan(
        [Description("Short one-sentence goal of the plan.")] string title,
        [Description("Required operations array. Each entry: { action: Create|Update|Delete|Deploy, resource_type, resource_name, resource_group, details }.")] List<Dictionary<string, object?>> operations,
        [Description("Risk level: Low, Medium, or High.")] string risk_level = "Medium",
        [Description("Optional ARM template object for Deploy operations.")] Dictionary<string, object?>? template_json = null,
        [Description("Optional ARM parameters object.")] Dictionary<string, object?>? parameters_json = null,
        [Description("Optional resource group for rg-scoped deployment.")] string? resource_group_name = null,
        [Description("Deployment location for subscription-scoped deployment.")] string? location = null,
        [Description("Optional deployment name.")] string? deployment_name = null)
        => tools.CreatePlan(title, operations, risk_level, template_json, parameters_json,
            resource_group_name, location, deployment_name);
}
