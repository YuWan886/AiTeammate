using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using AiTeammate.AiTeammateCode.Backend;
using AiTeammate.AiTeammateCode.Cache;
using AiTeammate.AiTeammateCode.Context;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Personality;

namespace AiTeammate.AiTeammateCode.Suggestion;

public class SuggestionService
{
    private readonly BackendManager _backend;
    private readonly PersonalityPromptBuilder _promptBuilder;
    private readonly ContextBuilder _contextBuilder;
    private readonly ConversationContextManager _conversationContext;

    public event Action<List<CardSuggestion>>? OnSuggestionsReady;

    public SuggestionService(BackendManager backend, PersonalityManager personality, ContentQueryService query, ConversationContextManager conversationContext)
    {
        _backend = backend;
        _promptBuilder = new PersonalityPromptBuilder(personality);
        _contextBuilder = new ContextBuilder(query);
        _conversationContext = conversationContext;
    }

    public async Task<List<CardSuggestion>> AnalyzeAsync(Player player, CancellationToken ct)
    {
        var context = _contextBuilder.BuildSuggestionContext(player);
        if (string.IsNullOrWhiteSpace(context)) return new();

        var systemPrompt = _promptBuilder.BuildSystemPrompt(requireJson: true);
        var prelude = _conversationContext.BuildRunPrelude(player, _contextBuilder);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(prelude))
        {
            sb.AppendLine(prelude);
            sb.AppendLine();
        }
        sb.AppendLine(context);
        sb.AppendLine();
        sb.AppendLine("请以 JSON 格式返回卡牌建议（json output required）：");
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"highlights\": [");
        sb.AppendLine("    {\"card_name\": \"卡牌名\", \"priority\": 1, \"reason\": \"简短理由\", \"glow_color\": \"gold\"},");
        sb.AppendLine("    {\"card_name\": \"卡牌名\", \"priority\": 2, \"reason\": \"简短理由\", \"glow_color\": \"green\"}");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"avoid\": [");
        sb.AppendLine("    {\"card_name\": \"卡牌名\", \"reason\": \"简短理由\"}");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");

        var request = new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = sb.ToString(),
            Type = AiRequestType.Suggestion,
            MaxTokens = 1024,
            Temperature = 0.5f,
            EnableJsonMode = true,           // DeepSeek JSON Output
            TopP = 0.9f,
            FrequencyPenalty = 0.1f
        };

        using var timeoutCts = new CancellationTokenSource(8000);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var response = await _backend.SendAsync(request, linkedCts.Token);
        if (!response.IsSuccess) return new();

        var suggestions = ParseSuggestions(response.Content);
        OnSuggestionsReady?.Invoke(suggestions);
        return suggestions;
    }

    private List<CardSuggestion> ParseSuggestions(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var results = new List<CardSuggestion>();

            if (root.TryGetProperty("highlights", out var highlights))
            {
                foreach (var h in highlights.EnumerateArray())
                {
                    results.Add(new CardSuggestion
                    {
                        CardName = h.TryGetProperty("card_name", out var cn) ? cn.GetString() ?? "" : "",
                        Priority = h.TryGetProperty("priority", out var pr) ? pr.GetInt32() : 5,
                        Reason = h.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                        GlowColor = h.TryGetProperty("glow_color", out var gc) ? gc.GetString() ?? "gold" : "gold",
                        IsAvoid = false
                    });
                }
            }

            if (root.TryGetProperty("avoid", out var avoids))
            {
                foreach (var a in avoids.EnumerateArray())
                {
                    results.Add(new CardSuggestion
                    {
                        CardName = a.TryGetProperty("card_name", out var cn) ? cn.GetString() ?? "" : "",
                        Reason = a.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                        GlowColor = "red",
                        IsAvoid = true
                    });
                }
            }

            return results;
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

public class CardSuggestion
{
    public string CardName { get; set; } = "";
    public int Priority { get; set; }
    public string Reason { get; set; } = "";
    public string GlowColor { get; set; } = "gold";
    public bool IsAvoid { get; set; }
}
