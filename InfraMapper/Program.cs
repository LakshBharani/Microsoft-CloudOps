using System.Text.Json.Serialization;
using DotNetEnv;
using Azure.Identity;
using Azure.Core;
using Azure.ResourceManager;
using InfraMapper.Services;
using InfraMapper.Services.Agent;
using InfraMapper.Services.Agent.Memory;
using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.SubAgents;
using Microsoft.SemanticKernel;

Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);
AgentRegistry.Configure(builder.Configuration);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<AzureResourceService>();
builder.Services.AddSingleton<DiffService>();
builder.Services.AddSingleton<AzureDevOpsService>();
builder.Services.AddSingleton<InfraIntentCompiler>();

builder.Services.AddSingleton<TokenCredential>(_ =>
{
    var options = new DefaultAzureCredentialOptions
    {
        ExcludeManagedIdentityCredential = builder.Environment.IsDevelopment(),
        ExcludeVisualStudioCredential = true,
        ExcludeVisualStudioCodeCredential = true
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

builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var endpoint = configuration["AzureAI:Endpoint"]
        ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
        ?? throw new InvalidOperationException(
            "Azure OpenAI endpoint not configured. Set AzureAI:Endpoint or AZURE_OPENAI_ENDPOINT to the resource endpoint, e.g. https://<resource>.openai.azure.com/ or https://<resource>.cognitiveservices.azure.com/.");
    var deploymentName = configuration["AzureAI:DeploymentName"]
        ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")
        ?? "gpt-4.1-mini";
    var modelId = configuration["AzureAI:ModelId"]
        ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL_ID")
        ?? deploymentName;
    var apiKey = configuration["AzureAI:ApiKey"]
        ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

    var kernelBuilder = Kernel.CreateBuilder();
    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        kernelBuilder.AddAzureOpenAIChatCompletion(
            deploymentName: deploymentName,
            endpoint: endpoint,
            apiKey: apiKey,
            modelId: modelId);
    }
    else
    {
        kernelBuilder.AddAzureOpenAIChatCompletion(
            deploymentName: deploymentName,
            endpoint: endpoint,
            credentials: sp.GetRequiredService<TokenCredential>(),
            modelId: modelId);
    }

    return kernelBuilder.Build();
});

builder.Services.AddSingleton<PlanStore>();
builder.Services.AddSingleton<QuestionStore>();
builder.Services.AddSingleton<ILessonsStore, JsonFileLessonsStore>();
builder.Services.AddSingleton<SkAgentRunner>();
builder.Services.AddSingleton<SkAgentFactory>();
builder.Services.AddSingleton<InvestigatorAgent>();
builder.Services.AddSingleton<PlannerAgent>();
builder.Services.AddSingleton<CriticAgent>();
builder.Services.AddSingleton<QuestionerAgent>();
builder.Services.AddSingleton<ExecutorAgent>();
builder.Services.AddSingleton<ReflectorAgent>();
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
