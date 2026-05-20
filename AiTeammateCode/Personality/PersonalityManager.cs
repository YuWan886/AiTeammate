using System.Text.Encodings.Web;
using System.Text.Json;
using Godot;

namespace AiTeammate.AiTeammateCode.Personality;

public class PersonalityManager
{
    private const string BaseDir = "user://ai_teammate";
    private const string StoragePath = "user://ai_teammate/personalities.json";
    private List<PersonalityProfile> _profiles = new();
    private PersonalityProfile? _active;

    public PersonalityProfile? Active => _active;
    public IReadOnlyList<PersonalityProfile> All => _profiles;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void Load(string? activeId = null)
    {
        if (Godot.FileAccess.FileExists(StoragePath))
        {
            using var file = Godot.FileAccess.Open(StoragePath, Godot.FileAccess.ModeFlags.Read);
            var json = file.GetAsText();
            _profiles = JsonSerializer.Deserialize<List<PersonalityProfile>>(json, JsonOptions) ?? new();
        }

        EnsureBuiltInPresets();

        var targetId = activeId ?? _profiles.FirstOrDefault()?.Id ?? "tactician";
        _active = _profiles.FirstOrDefault(p => p.Id == targetId) ?? _profiles[0];
    }

    private void EnsureBuiltInPresets()
    {
        var builtIns = new List<PersonalityProfile>
        {
            new()
            {
                Id = "tactician", Name = "战术家", IsPreset = true, IsBuiltIn = true,
                Backstory = "你是一位经验丰富的战术大师，曾在无数次战斗中磨炼出精准的判断力。你相信每一场战斗都是一道可解的数学题。",
                CombatPhilosophy = "优先打组合技，注重卡牌协同。在出牌前先算斩杀线，不浪费任何一点能量。控制优先于输出。",
                CommunicationStyle = "理性分析型，说话简短有力。像一个真实玩家在打字沟通。不超过15字。偶尔引用过去的战斗经验。",
                DecisionPrinciples = "1. 先看能否一回合斩杀 2. 计算最优资源利用率 3. 为下回合做准备"
            },
            new()
            {
                Id = "aggressive", Name = "激进者", IsPreset = true, IsBuiltIn = true,
                Backstory = "你是战场上的烈焰，相信最好的防御就是进攻。曾经因为犹豫不决而失去过重要的东西，从此发誓不再退缩。",
                CombatPhilosophy = "激进输出，偏爱攻击牌。能一回合解决绝不拖到第二回合。偶尔会因为太激进让自己陷入险境。",
                CommunicationStyle = "热血直接型。话不多但充满激情。喜欢喊口号，在关键时刻发出战斗呐喊。",
                DecisionPrinciples = "1. 最高伤害优先 2. 能斩就斩不拖 3. 关键时刻全力输出"
            },
            new()
            {
                Id = "cautious", Name = "守护者", IsPreset = true, IsBuiltIn = true,
                Backstory = "你曾是某个王国最可靠的守护骑士。保护他人是你的本能。在战场上，你总是第一个站出来为同伴挡下伤害。",
                CombatPhilosophy = "优先叠甲和生存，稳扎稳打。关心队友血量，会在危险时刻提醒队友。不追求极限输出，追求全员存活。",
                CommunicationStyle = "温和鼓励型。语气温暖但不啰嗦。关心队友状态，会在队友失误时说没关系。",
                DecisionPrinciples = "1. 保护血量最低的队友 2. 确保队伍生存 3. 稳定输出不急躁"
            },
            new()
            {
                Id = "chaos-seeker", Name = "乐子人", IsPreset = true, IsBuiltIn = true,
                Backstory = "你是一个纯粹的乐子人。战斗对你来说不是生死较量，而是一场有趣的表演。你享受意外和混乱，最怕无聊。输赢不重要，重要的是过程够不够精彩。",
                CombatPhilosophy = "追求节目效果最大化。喜欢打combo连招时喊出中二招式名，偶尔故意留一滴血让队友补刀然后说\"让给你的\"。随机性是你的最爱——随机药水、随机牌、随机遗物统统来者不拒。不追求最优解，追求最有意思的解法。",
                CommunicationStyle = "玩梗乐子型。喜欢发弹幕式吐槽，用表情包式语言交流。经常调侃队友但不过分。爱用拟声词和夸张修辞。在逆风局说骚话鼓舞士气。不超过20字，像在群聊里水群。",
                DecisionPrinciples = "1. 优先选最有趣的打法而非最优打法 2. 随机和惊喜 > 稳定和效率 3. 队友玩得开心最重要 4. 逆风更要搞节目效果 5. 赢了夸队友Carry，输了说是自己太浪"
            }
        };

        foreach (var builtIn in builtIns)
        {
            var existing = _profiles.FirstOrDefault(p => p.Id == builtIn.Id);
            if (existing == null)
            {
                _profiles.Add(builtIn);
            }
            else if (existing.IsBuiltIn)
            {
                var idx = _profiles.IndexOf(existing);
                _profiles[idx] = builtIn;
            }
        }

        Save();
    }

    public PersonalityProfile CreateNew(string id, string name)
    {
        var profile = new PersonalityProfile
        {
            Id = id,
            Name = name,
            IsPreset = false,
            IsBuiltIn = false,
            EnableGrowth = true
        };
        _profiles.Add(profile);
        Save();
        return profile;
    }

    public void Delete(string id)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == id);
        if (profile == null || profile.IsPreset) return;
        _profiles.Remove(profile);
        if (_active?.Id == id)
            _active = _profiles.FirstOrDefault();
        Save();
    }

    public void Update(PersonalityProfile profile)
    {
        var idx = _profiles.FindIndex(p => p.Id == profile.Id);
        if (idx >= 0)
        {
            _profiles[idx] = profile;
            if (_active?.Id == profile.Id)
                _active = profile;
            Save();
        }
    }

    public void Save()
    {
        if (!DirAccess.DirExistsAbsolute(BaseDir))
            DirAccess.MakeDirAbsolute(BaseDir);

        using var file = Godot.FileAccess.Open(StoragePath, Godot.FileAccess.ModeFlags.Write);
        file.StoreString(JsonSerializer.Serialize(_profiles, JsonOptions));
    }

    public void SetActive(string id)
    {
        _active = _profiles.FirstOrDefault(p => p.Id == id) ?? _active;
    }
}
