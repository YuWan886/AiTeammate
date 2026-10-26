namespace AiTeammate.AiTeammateCode.Personality;

/// <summary>
/// Read-only facade over <see cref="PersonalityStore"/> used by the prompt builders and services.
/// Editing/creation lives in the RitsuLib settings page.
/// </summary>
public sealed class PersonalityManager
{
    public PersonalityProfile? Active => PersonalityStore.Active;

    /// <summary>Restores built-ins and validates the persisted active profile. 加载人格数据。</summary>
    public void Load()
    {
        PersonalityStore.EnsureSeeded();
    }
}
