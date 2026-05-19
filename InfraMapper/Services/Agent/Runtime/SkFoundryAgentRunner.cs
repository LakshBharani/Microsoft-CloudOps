#pragma warning disable SKEXP0001
#pragma warning disable SKEXP0110

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Azure;
using Azure.AI.OpenAI;
using InfraMapper.Services.Agent.Plugins;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using SKAgent = Microsoft.SemanticKernel.Agents.Agent;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class SkFoundryAgentRunner : IAgentRunner
{
    private readonly AzureResourceService _resourceService;
    private readonly IArmGenericResourceService _genericResources;
    private readonly IArmDeploymentService _deployments;
    private readonly PlanStore _planStore;
    private readonly QuestionStore _questionStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SkFoundryAgentRunner> _logger;

    public SkFoundryAgentRunner(
        AzureResourceService resourceService,
        IArmGenericResourceService genericResources,
        IArmDeploymentService deployments,
        PlanStore planStore,
        QuestionStore questionStore,
        IConfiguration configuration,
        ILogger<SkFoundryAgentRunner> logger)
    {
        _resourceService = resourceService;
        _genericResources = genericResources;
        _deployments = deployments;
        _planStore = planStore;
        _questionStore = questionStore;
        _configuration = configuration;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        string message,
        string sessionId,
        string subscriptionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var tools = new AzureCrudTools(
            _resourceService, _genericResources, _deployments,
            _planStore, _questionStore, sessionId, subscriptionId);

        var events = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var filter = new ToolEventFilter(events.Writer);

        var readAgent = BuildAgent("ReadAgent", "read-agent.yaml", ReadModel,
            filter, new AzureReadPlugin(tools));

        var planAgent = BuildAgent("PlanAgent", "plan-agent.yaml", PlanModel,
            filter, new AzurePlanPlugin(tools));

        var executeAgent = BuildAgent("ExecuteAgent", "execute-agent.yaml", ExecuteModel,
            filter, new AzureExecutePlugin(tools));

        var chat = BuildGroupChat(readAgent, planAgent, executeAgent);
        chat.AddChatMessage(new ChatMessageContent(AuthorRole.User, message));

        _ = Task.Run(async () =>
        {
            try
            {
                var agentTexts = new Dictionary<string, System.Text.StringBuilder>(StringComparer.OrdinalIgnoreCase);
                var currentAgent = "";
                var currentActivityId = "";
                var lastAgent = "";

                await foreach (var chunk in chat.InvokeStreamingAsync(ct))
                {
                    var agentName = chunk.AuthorName ?? "agent";

                    if (agentName != currentAgent)
                    {
                        if (!string.IsNullOrEmpty(currentActivityId))
                            await events.Writer.WriteAsync(new AgentStreamEvent.Activity(
                                "end", currentActivityId, null,
                                "agent", currentAgent, null, null, "success",
                                $"{currentAgent} completed"), ct);

                        currentAgent = agentName;
                        currentActivityId = Guid.NewGuid().ToString("N");
                        await events.Writer.WriteAsync(new AgentStreamEvent.Activity(
                            "start", currentActivityId, null,
                            "agent", currentAgent, null, null, "running",
                            $"{currentAgent} working"), ct);
                    }

                    if (!string.IsNullOrEmpty(chunk.Content))
                    {
                        if (!agentTexts.ContainsKey(agentName))
                            agentTexts[agentName] = new System.Text.StringBuilder();
                        agentTexts[agentName].Append(chunk.Content);
                        lastAgent = agentName;
                    }
                }

                if (!string.IsNullOrEmpty(currentActivityId))
                    await events.Writer.WriteAsync(new AgentStreamEvent.Activity(
                        "end", currentActivityId, null,
                        "agent", currentAgent, null, null, "success",
                        $"{currentAgent} completed"), CancellationToken.None);

                // Only the last speaking agent's text is user-visible.
                // ReadAgent outputs internal JSON for PlanAgent — never show it directly.
                var finalText = lastAgent != "" && agentTexts.TryGetValue(lastAgent, out var sb)
                    ? sb.ToString()
                    : "";

                await events.Writer.WriteAsync(
                    new AgentStreamEvent.Done(finalText), CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await events.Writer.WriteAsync(new AgentStreamEvent.Activity(
                    "end", "cancelled", null, "agent", "agent",
                    null, null, "cancelled", "Cancelled"), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SK agent group chat failed for session {SessionId}", sessionId);
                await events.Writer.WriteAsync(
                    new AgentStreamEvent.Error(ex.Message), CancellationToken.None);
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var evt in events.Reader.ReadAllAsync(ct))
            yield return evt;
    }

    // -- Agent factory -------------------------------------------------------

    private ChatCompletionAgent BuildAgent(
        string name, string skillFile, string deployment,
        IFunctionInvocationFilter filter, params object[] plugins)
    {
        var aoaiClient = new AzureOpenAIClient(
            new Uri(AoaiEndpoint),
            new AzureKeyCredential(AoaiApiKey),
            new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_04_01_Preview));

        var kernel = Kernel.CreateBuilder()
            .AddAzureOpenAIChatCompletion(deployment, aoaiClient)
            .Build();

        kernel.FunctionInvocationFilters.Add(filter);

        foreach (var plugin in plugins)
            kernel.Plugins.AddFromObject(plugin, plugin.GetType().Name);

        return new ChatCompletionAgent
        {
            Name = name,
            Instructions = LoadSkill(skillFile),
            Kernel = kernel
        };
    }

    // -- Group chat ----------------------------------------------------------

    private static AgentGroupChat BuildGroupChat(
        ChatCompletionAgent readAgent,
        ChatCompletionAgent planAgent,
        ChatCompletionAgent executeAgent)
    {
        return new AgentGroupChat(readAgent, planAgent, executeAgent)
        {
            ExecutionSettings = new AgentGroupChatSettings
            {
                SelectionStrategy = new InfraSelectionStrategy(readAgent, planAgent, executeAgent),
                TerminationStrategy = new InfraTerminationStrategy { MaximumIterations = 6 }
            }
        };
    }

    // -- Config helpers ------------------------------------------------------

    private string AoaiEndpoint =>
        _configuration["SemanticKernel:AzureOpenAI:Endpoint"]
        ?? throw new InvalidOperationException("SemanticKernel:AzureOpenAI:Endpoint not configured.");

    private string AoaiApiKey =>
        _configuration["SemanticKernel:AzureOpenAI:ApiKey"]
        ?? throw new InvalidOperationException("SemanticKernel:AzureOpenAI:ApiKey not configured.");

    private string ReadModel =>
        _configuration["SemanticKernel:AzureOpenAI:ReadDeployment"] ?? "gpt-4.1-mini";

    private string PlanModel =>
        _configuration["SemanticKernel:AzureOpenAI:PlanDeployment"] ?? "gpt-5.1";

    private string ExecuteModel =>
        _configuration["SemanticKernel:AzureOpenAI:ExecuteDeployment"] ?? "gpt-5.1";

    // -- Skill loader --------------------------------------------------------

    private static string LoadSkill(string fileName)
    {
        var dirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Services", "Agent", "Skills"),
            Path.Combine(Directory.GetCurrentDirectory(), "Services", "Agent", "Skills"),
        };

        foreach (var dir in dirs)
        {
            var path = Path.Combine(dir, fileName);
            if (!File.Exists(path)) continue;

            var yaml = File.ReadAllText(path);
            var idx = yaml.IndexOf("instructions: |", StringComparison.Ordinal);
            if (idx < 0) return yaml;

            var raw = yaml[(idx + "instructions: |".Length)..].TrimStart('\n', '\r');
            return string.Join('\n', raw.Split('\n')
                .Select(l => l.StartsWith("  ", StringComparison.Ordinal) ? l[2..] : l));
        }

        throw new FileNotFoundException(
            $"Skill file not found: {fileName}. Searched: {string.Join(", ", dirs)}");
    }

    // -- Selection strategy --------------------------------------------------

    private sealed class InfraSelectionStrategy(
        ChatCompletionAgent readAgent,
        ChatCompletionAgent planAgent,
        ChatCompletionAgent executeAgent) : SelectionStrategy
    {
        protected override Task<SKAgent> SelectAgentAsync(
            IReadOnlyList<SKAgent> agents,
            IReadOnlyList<ChatMessageContent> history,
            CancellationToken ct)
        {
            // First user message — find who has spoken
            var agentNames = new HashSet<string>(
                history.Select(m => m.AuthorName ?? "").Where(n => n != ""),
                StringComparer.OrdinalIgnoreCase);

            // If user explicitly approves plan and ReadAgent has already spoken, go straight to execute
            var lastUserMessage = history
                .LastOrDefault(m => m.Role == AuthorRole.User)?.Content ?? "";
            var isApproval = lastUserMessage.Contains("approved plan", StringComparison.OrdinalIgnoreCase)
                          || lastUserMessage.Contains("execute", StringComparison.OrdinalIgnoreCase);

            if (isApproval && agentNames.Contains("ReadAgent"))
                return Task.FromResult<SKAgent>(executeAgent);

            if (!agentNames.Contains("ReadAgent"))
                return Task.FromResult<SKAgent>(readAgent);

            if (!agentNames.Contains("PlanAgent"))
                return Task.FromResult<SKAgent>(planAgent);

            // Both spoke — terminate (termination strategy handles, but safety fallback)
            return Task.FromResult<SKAgent>(planAgent);
        }
    }

    // -- Termination strategy ------------------------------------------------

    private sealed class InfraTerminationStrategy : TerminationStrategy
    {
        protected override Task<bool> ShouldAgentTerminateAsync(
            SKAgent agent, IReadOnlyList<ChatMessageContent> history, CancellationToken ct)
        {
            // Each agent runs exactly one turn then stops
            var terminate = agent.Name switch
            {
                "PlanAgent" => true,
                "ExecuteAgent" => true,
                _ => false
            };
            return Task.FromResult(terminate);
        }
    }

    // -- Tool event filter ---------------------------------------------------

    private sealed class ToolEventFilter(ChannelWriter<AgentStreamEvent> writer) : IFunctionInvocationFilter
    {
        public async Task OnFunctionInvocationAsync(
            FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            var callId = Guid.NewGuid().ToString("N");
            await writer.WriteAsync(new AgentStreamEvent.ToolCall(context.Function.Name, callId));
            try
            {
                await next(context);
                var result = context.Result?.GetValue<string>() ?? "";
                await writer.WriteAsync(new AgentStreamEvent.ToolResult(
                    context.Function.Name, callId, true, result));
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(new AgentStreamEvent.ToolResult(
                    context.Function.Name, callId, false, ex.Message));
                throw;
            }
        }
    }
}
