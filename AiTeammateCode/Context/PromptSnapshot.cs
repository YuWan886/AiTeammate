using System.Text;

namespace AiTeammate.AiTeammateCode.Context;

public sealed class PromptFact
{
    public PromptFact(string key, string label, string value, bool essential = true)
    {
        Key = key;
        Label = label;
        Value = value;
        Essential = essential;
    }

    public string Key { get; }
    public string Label { get; }
    public string Value { get; }
    public bool Essential { get; }
}

public sealed class PromptSnapshot
{
    private readonly List<PromptFact> _facts = new();

    public IReadOnlyList<PromptFact> Facts => _facts;

    public void Add(string key, string label, string value, bool essential = true)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        _facts.Add(new PromptFact(key, label, value.Trim(), essential));
    }

    public string RenderAll(int maxFacts = int.MaxValue)
    {
        return RenderInternal(_facts.Take(maxFacts));
    }

    public string RenderEssentials(int maxFacts = int.MaxValue)
    {
        return RenderInternal(_facts.Where(f => f.Essential).Take(maxFacts));
    }

    public string RenderDelta(PromptSnapshot? previous, int maxChanges = 8)
    {
        if (previous == null)
            return RenderAll(maxChanges);

        var previousMap = previous.Facts.ToDictionary(f => f.Key, f => f.Value);
        var changes = new List<PromptFact>();

        foreach (var fact in _facts)
        {
            if (!previousMap.TryGetValue(fact.Key, out var oldValue) || !string.Equals(oldValue, fact.Value, StringComparison.Ordinal))
                changes.Add(fact);
        }

        if (changes.Count == 0)
            return "- 关键状态无明显变化";

        return RenderInternal(changes.Take(maxChanges), bulletPrefix: "- ");
    }

    private static string RenderInternal(IEnumerable<PromptFact> facts, string bulletPrefix = "")
    {
        var sb = new StringBuilder();
        foreach (var fact in facts)
            sb.AppendLine($"{bulletPrefix}{fact.Label}: {fact.Value}");
        return sb.ToString().TrimEnd();
    }
}
