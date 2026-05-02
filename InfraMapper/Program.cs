using System.Text.Json.Serialization;
using DotNetEnv;
using Azure.Identity;
using Azure.Core;
using Azure.ResourceManager;
using InfraMapper.Services;
using InfraMapper.Services.Agent;

Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddSingleton<AzureResourceService>();
builder.Services.AddSingleton<DiffService>();
builder.Services.AddSingleton<AzureDevOpsService>();

builder.Services.AddSingleton<TokenCredential>(_ =>
{
    var options = new DefaultAzureCredentialOptions
    {
        // Local dev: these frequently fail/noise on macOS, and managed identity probes can add delay.
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

var anthropicApiKey = builder.Configuration["Anthropic:ApiKey"]
    ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException(
        "Anthropic API key not configured. Set Anthropic:ApiKey in config or ANTHROPIC_API_KEY env var.");

builder.Services.AddHttpClient("anthropic", client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    client.DefaultRequestHeaders.Add("x-api-key", anthropicApiKey);
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
});

builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<PlanStore>();
builder.Services.AddSingleton<AgentTools>();
builder.Services.AddSingleton<AgentService>();
builder.Services.AddHostedService<SessionEvictionService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:3000")
     .AllowAnyHeader()
     .AllowAnyMethod()));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseHttpsRedirection();
app.MapControllers();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
