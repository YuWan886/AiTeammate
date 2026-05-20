using Godot;
using AiTeammate.AiTeammateCode.Personality;

namespace AiTeammate.AiTeammateCode.UI;

public partial class PersonalityEditorPanel : Control
{
    private OptionButton _personalitySelect = null!;
    private TextEdit _backstoryEdit = null!;
    private TextEdit _philosophyEdit = null!;
    private TextEdit _styleEdit = null!;
    private TextEdit _principlesEdit = null!;
    private Label _statusLabel = null!;

    private PersonalityManager _manager = null!;

    public void Initialize()
    {
        _manager = AiTeammateManager.Instance.PersonalityManager;
        SetupUI();
        LoadPersonalities();
    }

    private void SetupUI()
    {
        var mainVbox = new VBoxContainer();
        mainVbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        mainVbox.AddThemeConstantOverride("separation", 8);
        AddChild(mainVbox);

        var titleLabel = new Label();
        titleLabel.Text = "AI 人格编辑器";
        mainVbox.AddChild(titleLabel);

        // Personality selector
        var selectHbox = new HBoxContainer();
        _personalitySelect = new OptionButton();
        _personalitySelect.ItemSelected += OnPersonalitySelected;
        selectHbox.AddChild(_personalitySelect);

        var newBtn = new Button { Text = "新建" };
        newBtn.Pressed += OnNewPersonality;
        selectHbox.AddChild(newBtn);

        mainVbox.AddChild(selectHbox);

        // Edit areas
        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        var editVbox = new VBoxContainer();

        _backstoryEdit = CreateEditSection(editVbox, "背景故事");
        _philosophyEdit = CreateEditSection(editVbox, "战斗哲学");
        _styleEdit = CreateEditSection(editVbox, "交流风格");
        _principlesEdit = CreateEditSection(editVbox, "决策原则");

        scroll.AddChild(editVbox);
        mainVbox.AddChild(scroll);

        // Buttons
        var btnHbox = new HBoxContainer();
        var saveBtn = new Button { Text = "保存" };
        saveBtn.Pressed += OnSave;
        btnHbox.AddChild(saveBtn);

        mainVbox.AddChild(btnHbox);

        _statusLabel = new Label();
        mainVbox.AddChild(_statusLabel);
    }

    private TextEdit CreateEditSection(VBoxContainer parent, string label)
    {
        var lbl = new Label { Text = label };
        parent.AddChild(lbl);

        var edit = new TextEdit();
        edit.SizeFlagsVertical = SizeFlags.ExpandFill;
        edit.CustomMinimumSize = new Vector2(0, 60);
        parent.AddChild(edit);

        return edit;
    }

    private void LoadPersonalities()
    {
        _personalitySelect.Clear();
        foreach (var p in _manager.All)
        {
            var label = p.IsPreset ? $"{p.Name} (预设)" : p.Name;
            _personalitySelect.AddItem(label);
        }
        if (_manager.All.Count > 0)
        {
            _personalitySelect.Select(0);
            LoadProfile(_manager.All[0]);
        }
    }

    private void OnPersonalitySelected(long index)
    {
        if (index < _manager.All.Count)
            LoadProfile(_manager.All[(int)index]);
    }

    private void LoadProfile(PersonalityProfile profile)
    {
        _backstoryEdit.Text = profile.Backstory;
        _philosophyEdit.Text = profile.CombatPhilosophy;
        _styleEdit.Text = profile.CommunicationStyle;
        _principlesEdit.Text = profile.DecisionPrinciples;
    }

    private void OnSave()
    {
        var idx = (int)_personalitySelect.Selected;
        if (idx < 0 || idx >= _manager.All.Count) return;

        var profile = _manager.All[idx];
        profile.Backstory = _backstoryEdit.Text;
        profile.CombatPhilosophy = _philosophyEdit.Text;
        profile.CommunicationStyle = _styleEdit.Text;
        profile.DecisionPrinciples = _principlesEdit.Text;

        _manager.Update(profile);
        _statusLabel.Text = "已保存!";
    }

    private void OnNewPersonality()
    {
        var name = $"自定义人格{_manager.All.Count + 1}";
        var id = $"custom_{System.Guid.NewGuid().ToString("N")[..6]}";
        _manager.CreateNew(id, name);
        LoadPersonalities();
    }
}
