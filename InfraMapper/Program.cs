using System.Text.Json.Serialization;
using DotNetEnv;
using Azure.Identity;
using Azure.Core;
using Azure.ResourceManager;
using InfraMapper.Services;
using InfraMapper.Services.Agent;
using InfraMapper.Services.Agent.Runtime;

Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);
AgentRegistry.Configure(builder.Configuration);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<AzureResourceService>();
builder.Services.AddSingleton<ArmExistenceProbe>();
builder.Services.AddSingleton<DiffService>();
builder.Services.AddSingleton<AzureDevOpsService>();
builder.Services.AddSingleton<InfraIntentCompiler>();

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
builder.Services.AddSingleton<SkAgentRunner>();
builder.Services.AddSingleton<SkAgentFactory>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<AgentService>();
builder.Services.AddHostedService<SessionEvictionService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:3000")
     .AllowAnyHeader()
     .AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();
app.UseHttpsRedirection();
app.MapControllers();

app.Run();
