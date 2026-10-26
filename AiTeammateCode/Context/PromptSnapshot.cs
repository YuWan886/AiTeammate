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

    public void Add(string key, string label, string value, bool essential = true)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        _facts.Add(new PromptFact(key, label, value.Trim(), essential));
    }

    public string RenderEssentials(int maxFacts = int.MaxValue)
    {
        return RenderInternal(_facts.Where(f => f.Essential).Take(maxFacts));
    }

    private static string RenderInternal(IEnumerable<PromptFact> facts, string bulletPrefix = "")
    {
        var sb = new StringBuilder();
        foreach (var fact in facts)
            sb.AppendLine($"{bulletPrefix}{fact.Label}: {fact.Value}");
        return sb.ToString().TrimEnd();
    }
}
