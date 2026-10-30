## Short Description

AI Teammate adds an LLM-controlled player to Slay the Spire 2 — a cooperating teammate in multiplayer and a solo climber in singleplayer you can chat with and interrupt, plus Bilibili live danmaku and an optional external-agent (dsh) takeover. Requires RitsuLib and your own LLM API key.
---
AI 队友为《杀戮尖塔 2》加入一个由大语言模型操控的玩家——多人模式下是协作队友，单人模式下能独自爬塔、随时可以打断并聊天；还支持 B 站直播弹幕与外部 Agent（dsh）接管。需要 RitsuLib 与自备大模型 API Key。

## Full Description

[h1]AI 队友 - 由大模型操控的《杀戮尖塔 2》玩家[/h1]

在《杀戮尖塔 2》里加入一个由大语言模型操控的玩家。多人模式中它是你的队友；单人模式中它可以[b]独自爬塔[/b]，你随时能打断它并和它说话。

[h2]核心特色一览[/h2]

[list]
[*][b]👥 多人联机 AI 队友[/b]：拥有独立人格（战术家 / 激进 / 谨慎 / 混沌，也可自定义），会读整幕地图、血量、金币与构筑后[b]自主投票选路[/b]，而不是复制人类投的节点；战斗按阶段协作出牌（药水 → 铺垫 → 集火 → 剩余手牌）；能在聊天里交流；可只读查看你的手牌、牌堆、金币、遗物与药水。

[*][b]🏔️ 独自爬塔（单人模式）[/b]：AI 接管本地玩家，自动完成选路、战斗出牌与用药水、奖励与选牌、商店、篝火、事件、宝箱和进入下一幕。发消息即可打断，点 ⏸/▶ 暂停或继续，还能下达「先打左边」这类战术指令。

[*][b]🧠 看得懂牌桌[/b]：战斗提示包含手牌、抽牌堆、弃牌堆、消耗堆内容（[b]永远不暴露牌序[/b]）、地图连线图，以及选牌奖励与商店的完整牌组；需要时可调用只读工具查询牌堆、单卡、遗物、药水、玩家状态、局内状态与牌组统计。

[*][b]👁️ 视觉模型[/b]：可选截图工具，把当前画面截成 JPEG 附加到下一次请求，支持图像输入的模型就能「看到」游戏。

[*][b]💬 B 站直播弹幕[/b]：匿名只读连接直播间弹幕并滚动显示在画面上，速度、字号、不透明度、方向与头像均可调；登录后可让 AI 用当前人格回复观众弹幕。

[*][b]🤖 外部 Agent 接管（dsh）[/b]：可选的本地桥接（只监听 127.0.0.1），让外部 dsh（DeepSeek Harness）Agent 读取完整局面并操作游戏，可以打完整局。

[*][b]🔧 游戏内设置[/b]：后端、API Key、模型列表、超时、工具开关、人格、聊天与日志全部在 RitsuLib 设置页完成，无需编辑 JSON。
[/list]

[hr]
[h2]安装[/h2]

[list]
[*]订阅/安装本模组；
[*]安装与游戏版本匹配的 [b]RitsuLib[/b]；
[*]启动游戏，在「设置 → 模组设置 → AI 队友」里填入大模型 API Key；
[*]在选人界面点「添加 AI」加入 AI 队友，或打开「AI 独自爬塔」。
[/list]

[h2]支持版本[/h2]
[list]
[*]0.107.1（正式版）—— 实机验证
[*]0.111.0（beta）—— 编译/打包验证
[/list]
本模组以[b]多版本 bundle[/b]形式发布：加载器会自动选择匹配当前游戏版本的逻辑。

[hr]
[h2]前置依赖[/h2]

[list]
[*][url=https://github.com/BAKAOLC/STS2-RitsuLib][b]RitsuLib[/b][/url] —— 必需，设置页与提示由它提供
[*]一个大模型 API Key —— DeepSeek、OpenAI 或本地 Ollama
[/list]

[hr]
[h2]相关链接[/h2]

[list]
[*][url=https://github.com/YuWan886/AiTeammate]GitHub 仓库[/url]
[*][url=https://github.com/YuWan886/dsh-sts2]dsh-sts2 外部 Agent 插件[/url]
[*][url=https://space.bilibili.com/438381132]作者的 B 站主页[/url]
[/list]

[hr]
[h2]交流与反馈[/h2]

[list]
[*][url=https://qm.qq.com/q/ohYkDqbe7K][b]QQ 群：752913553[/b][/url]
[*][url=https://discord.gg/tJT3a95Y8y][b]Discord 服务器[/b][/url]
[/list]

[hr]
[h2]数据与隐私[/h2]

本模组[b]不收集任何统计数据[/b]，没有遥测。API Key 与 B 站 Cookie 仅保存在本机设置文件中；只有开启弹幕功能时才连接 B 站服务器，只有开启桥接时才在本机 127.0.0.1 上监听。

[hr]
[h2]致谢[/h2]

[list]
[*]框架、设置页与提示 —— [url=https://github.com/BAKAOLC/STS2-RitsuLib]RitsuLib[/url]
[*]游戏本体 —— Slay the Spire 2（Mega Crit）
[*]大模型后端 —— [url=https://www.deepseek.com/]DeepSeek[/url] / [url=https://openai.com/]OpenAI[/url] / [url=https://ollama.com/]Ollama[/url]
[*]外部 Agent 插件 —— [url=https://github.com/YuWan886/dsh-sts2]dsh-sts2[/url]
[/list]

[quote][b]注意：[/b] 本模组使用 AI 辅助开发，可能存在错误。[/quote]

---

[h1]AI Teammate - an LLM-controlled player for Slay the Spire 2[/h1]

Adds a player driven by a large language model to Slay the Spire 2. In multiplayer it is your teammate; in singleplayer it can [b]climb the Spire alone[/b] while you chat with it and interrupt at any time.

[h2]Core Features[/h2]

[list]
[*][b]👥 Multiplayer AI Teammate[/b]: Its own personality (tactician / aggressive / cautious / chaos-seeker, or custom), [b]independent map votes[/b] based on the whole act map, HP, gold and deck instead of copying a human vote, coordinated card play in phases (potions → setup → focused attacks → leftovers), chat, and read-only views of your hand, piles, gold, relics and potions.

[*][b]🏔️ Solo Climb (Singleplayer)[/b]: The AI takes over the local player and handles routing, combat cards and potions, rewards and card picks, shops, rest sites, events, chests and act transitions. Send a chat message to interrupt it, press ⏸/▶ to pause or resume, and give tactical orders like "kill the left one first".

[*][b]🧠 Board Awareness[/b]: Combat prompts include hand, draw, discard and exhaust pile contents ([b]draw order is never revealed[/b]), the map graph, and the full master deck for card rewards and shops; read-only tools expose piles, card info, relics, potions, player/run state and deck stats on demand.

[*][b]👁️ Vision Models[/b]: Optional screenshot tool that attaches the current screen as JPEG to the next request, so image-capable models can literally see the game.

[*][b]💬 Bilibili Live Danmaku[/b]: Anonymous read-only connection to a live room's danmaku, scrolled over the game with configurable speed, size, opacity, direction and avatars; after login the AI can reply to viewers with its current personality.

[*][b]🤖 External Agent Takeover (dsh)[/b]: An opt-in localhost-only bridge that lets the external dsh (DeepSeek Harness) agent read the full game state and play a whole run.

[*][b]🔧 In-Game Settings[/b]: Backend, API key, model list, timeouts, tool toggles, personalities, chat and logs are all configured in the RitsuLib settings page — no JSON editing.
[/list]

[hr]
[h2]Installation[/h2]

[list]
[*]Subscribe to / install this mod;
[*]Install the [b]RitsuLib[/b] runtime matching your game version;
[*]Launch the game and enter your LLM API key under Settings → Mod Settings → AI Teammate;
[*]Click "Add AI" on the character select screen, or enable "AI solo climb".
[/list]

[h2]Supported Versions[/h2]
[list]
[*]0.107.1 (stable) — verified in game
[*]0.111.0 (beta) — compile/package verified
[/list]
The mod ships as a [b]multi-version bundle[/b]: the loader automatically picks the logic matching your game version.

[hr]
[h2]Requirements[/h2]

[list]
[*][url=https://github.com/BAKAOLC/STS2-RitsuLib][b]RitsuLib[/b][/url] — required; provides the settings page and toasts
[*]An LLM API key — DeepSeek, OpenAI or a local Ollama
[/list]

[hr]
[h2]Related Links[/h2]

[list]
[*][url=https://github.com/YuWan886/AiTeammate]GitHub Repository[/url]
[*][url=https://github.com/YuWan886/dsh-sts2]dsh-sts2 external agent plugin[/url]
[*][url=https://space.bilibili.com/438381132]Author's Bilibili homepage[/url]
[/list]

[hr]
[h2]Community & Feedback[/h2]

[list]
[*][url=https://qm.qq.com/q/ohYkDqbe7K][b]QQ Group: 752913553[/b][/url]
[*][url=https://discord.gg/tJT3a95Y8y][b]Discord Server[/b][/url]
[/list]

[hr]
[h2]Data & Privacy[/h2]

This mod [b]collects no statistics[/b] and has no telemetry. API keys and Bilibili cookies stay in the local settings file; Bilibili servers are contacted only when the danmaku feature is on, and the bridge only listens on 127.0.0.1 when enabled.

[hr]
[h2]Credits[/h2]

[list]
[*]Framework, settings page and toasts — [url=https://github.com/BAKAOLC/STS2-RitsuLib]RitsuLib[/url]
[*]Game — Slay the Spire 2 (Mega Crit)
[*]LLM backends — [url=https://www.deepseek.com/]DeepSeek[/url] / [url=https://openai.com/]OpenAI[/url] / [url=https://ollama.com/]Ollama[/url]
[*]External agent plugin — [url=https://github.com/YuWan886/dsh-sts2]dsh-sts2[/url]
[/list]

[quote][b]Note:[/b] This mod is developed with AI assistance, there may be errors.[/quote]
