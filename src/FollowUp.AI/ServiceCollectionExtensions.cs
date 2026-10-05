using FollowUp.AI.Heuristics;
using FollowUp.AI.Llm;
using FollowUp.AI.Services;
using FollowUp.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FollowUp.AI;

/// <summary>Dependency-injection registration for the AI layer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAIClassificationService"/>, <see cref="ICommitmentExtractionService"/>,
    /// <see cref="INaturalLanguageService"/> and the <see cref="ILlmClient"/> selected by <c>Ai:Provider</c>
    /// ("Heuristic" — default, "Anthropic", "OpenAICompatible"), all as singletons sharing one circuit breaker.
    /// </summary>
    public static IServiceCollection AddFollowUpAI(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AiOptions.SectionName);
        services.AddOptions<AiOptions>().Bind(section);
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<HeuristicCommitmentExtractor>();
        services.TryAddSingleton(sp => new HeuristicClassifier(sp.GetRequiredService<HeuristicCommitmentExtractor>()));
        services.TryAddSingleton<HeuristicTaskMatcher>();
        services.TryAddSingleton<HeuristicCommandParser>();
        services.TryAddSingleton<HeuristicQuestionParser>();

        var provider = (section["Provider"] ?? AiOptions.HeuristicProvider).Trim();
        if (provider.Equals(AiOptions.AnthropicProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<ILlmClient>(sp => new AnthropicLlmClient(
                sp.GetRequiredService<IOptions<AiOptions>>(), sp.GetService<ILogger<AnthropicLlmClient>>()));
        }
        else if (provider.Equals(AiOptions.OpenAiCompatibleProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient(OpenAiCompatibleLlmClient.HttpClientName, (sp, http) =>
            {
                // The gateway enforces Ai:TimeoutSeconds; the HttpClient timeout is only a backstop.
                var seconds = sp.GetRequiredService<IOptions<AiOptions>>().Value.TimeoutSeconds;
                http.Timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 600) + 5);
            });
            services.TryAddSingleton<ILlmClient>(sp => new OpenAiCompatibleLlmClient(
                sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IOptions<AiOptions>>(), sp.GetService<ILogger<OpenAiCompatibleLlmClient>>()));
        }
        else
        {
            services.TryAddSingleton<ILlmClient>(NullLlmClient.Instance);
        }

        services.TryAddSingleton(sp => new LlmGateway(
            sp.GetRequiredService<ILlmClient>(), sp.GetRequiredService<IOptions<AiOptions>>(),
            sp.GetService<TimeProvider>(), sp.GetService<ILogger<LlmGateway>>()));

        services.TryAddSingleton(sp => new AiClassificationService(
            sp.GetRequiredService<LlmGateway>(), sp.GetRequiredService<HeuristicClassifier>(), sp.GetRequiredService<HeuristicTaskMatcher>()));
        services.TryAddSingleton(sp => new CommitmentExtractionService(
            sp.GetRequiredService<LlmGateway>(), sp.GetRequiredService<HeuristicCommitmentExtractor>()));
        services.TryAddSingleton(sp => new NaturalLanguageService(
            sp.GetRequiredService<LlmGateway>(), sp.GetRequiredService<HeuristicCommandParser>(), sp.GetRequiredService<HeuristicQuestionParser>()));

        services.TryAddSingleton<IAIClassificationService>(sp => sp.GetRequiredService<AiClassificationService>());
        services.TryAddSingleton<ICommitmentExtractionService>(sp => sp.GetRequiredService<CommitmentExtractionService>());
        services.TryAddSingleton<INaturalLanguageService>(sp => sp.GetRequiredService<NaturalLanguageService>());
        return services;
    }
}
