using System.Text.Json.Serialization;
using DotNetEnv;
using Azure.Identity;
using Azure.Core;
using Azure.ResourceManager;
using InfraMapper.Services;
using InfraMapper.Services.Agent;
using InfraMapper.Services.Agent.Runtime;
using ModelContextProtocol.Server;

Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<AzureResourceService>();
builder.Services.AddSingleton<ArmExistenceProbe>();
builder.Services.AddSingleton<DiffService>();
builder.Services.AddSingleton<AzureDevOpsService>();
builder.Services.AddSingleton<InfraIntentCompiler>();
builder.Services.AddSingleton<InfraIntentValidator>();

builder.Services.AddSingleton<TokenCredential>(_ =>
{
    if (builder.Environment.IsDevelopment())
    {
        return new AzureCliCredential(new AzureCliCredentialOptions
        {
            TenantId = builder.Configuration["AZURE_TENANT_ID"]
        });
    }

    var options = new DefaultAzureCredentialOptions
    {
        ExcludeVisualStudioCredential = true,
        ExcludeVisualStudioCodeCredential = true,
        ExcludeSharedTokenCacheCredential = true,
        ExcludeInteractiveBrowserCredential = true
    };
    return new DefaultAzureCredential(options);
});

builder.Services.AddSingleton(sp => new ArmClient(sp.GetRequiredService<TokenCredential>()));
builder.Services.AddSingleton<IArmDeploymentService, ArmDeploymentService>();
builder.Services.AddSingleton<IApprovalService, InMemoryApprovalService>();
builder.Services.AddSingleton<IResourceMutationApprovalService, InMemoryResourceMutationApprovalService>();
builder.Services.AddSingleton<IArmGenericResourceService, ArmGenericResourceService>();
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSingleton<PlanStore>();
builder.Services.AddSingleton<QuestionStore>();
builder.Services.AddSingleton<CloudOpsMcpAuditStore>();
builder.Services.AddSingleton<FoundryAgentRunner>();
builder.Services.AddSingleton<IAgentRunner>(sp => sp.GetRequiredService<FoundryAgentRunner>());
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<AgentService>();
builder.Services.AddHostedService<SessionEvictionService>();
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:3000")
     .AllowAnyHeader()
     .AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp"))
    {
        var apiKey = builder.Configuration["CloudOpsMcp:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var provided = context.Request.Headers["x-cloudops-mcp-key"].FirstOrDefault();
            var bearer = context.Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(provided) &&
                !string.IsNullOrWhiteSpace(bearer) &&
                bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                provided = bearer["Bearer ".Length..].Trim();
            }

            if (!string.Equals(provided, apiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
    }

    await next();
});
app.MapMcp("/mcp");
app.MapControllers();

app.Run();
