# Codex 环境管理器

一个 Windows x64 单文件工具，用于创建、验证并启动彼此隔离的 Codex 桌面环境。每个环境固定拥有独立的 `CODEX_HOME` 与 Chromium `app-data`；创建新环境时不会从现有 Codex 数据目录复制磁盘上的登录或会话文件。

内置 **Keysmith 破限** 与自定义 API 中转能力，开箱即可把 Codex 接到第三方兼容网关，并默认强化系统指令（配置层注入，不保证上游 100% 放行）。

## 社区与中转站

想少踩坑、快速跑通「多开环境 + 自定义 API + 破限」整条链路？欢迎加入 **AI98 Pro** 用户交流群，一起聊配置、模型与使用经验：

| 入口 | 信息 |
| --- | --- |
| **QQ 交流群** | `1072183582` |
| **API 中转站** | [https://ai98pro.xyz](https://ai98pro.xyz) |
| **能力亮点** | 兼容 OpenAI 风格接口 · **支持破限** · 适合本启动器一键对接 |

**推荐用法（约 1 分钟）：**

1. 打开 [AI98 Pro 中转站](https://ai98pro.xyz) 获取 Base URL 与 API Key  
2. 在启动器环境页进入 **AI** Tab，粘贴地址与密钥（默认 Base 即为 `https://ai98pro.xyz/`）  
3. 点「测试连接 / 刷新模型」→ 选模型与推理强度 → 保存  
4. 启动 Codex；本工具默认开启 Keysmith / 系统提示词替换，与中转站破限能力可叠加使用  

有问题进群 `1072183582` 反馈即可，比一个人硬啃文档快得多。

## 使用

1. 运行 `CodexProfileLauncher.exe`。
2. 点击“新建环境”，填写名称、环境数据目录和可选工作目录。
3. 点击“启动 Codex”。首次启动的新环境不会复制现有 Codex 的磁盘登录或会话；通常需要单独登录，但系统级 SSO 或 Windows 凭据管理器仍可能影响登录状态（见边界说明）。
4. 同一环境已有实例时，“启动 Codex”会变成“打开 Codex”，只激活现有实例，不会重复使用同一数据目录启动；不同环境可以同时运行。
5. v1.1.6 从本机 Store 包的共享运行缓存启动 Codex，并优先用同一 Windows session 的 `explorer.exe` 作为父进程；该正常路径下关闭启动器不会关闭 Codex，且不再依赖隐藏 broker、Job breakaway 或 WMI。若系统阻止 explorer-parent 创建而启用直接本地副本回退，启动详情会明确提示“独立存活不保证”，不会伪装成同等生命周期。再次运行启动器时会按持久化 receipt 恢复状态。
6. 每个新 Store 包版本首次启动前需要准备一次共享运行缓存，通常会额外占用约 1.8 GiB，并可能等待 30–60 秒；同版本的所有环境随后在完整性校验通过后复用这一份缓存。

## 环境页配置（顶部导航）

选择环境后，主区布局为：**环境标题 → 启动面板（始终可见）→ 顶部配置 Tab**。侧栏仍只列环境，不增加第二套应用导航。

| Tab | 用途 |
| --- | --- |
| **概览** | AI / 技能摘要与快捷入口 |
| **AI** | 自定义 API、默认模型、推理强度、系统提示词（高频入口，直接在页内配置） |
| **技能** | 内置技能库：启用/禁用、编辑 `SKILL.md`、重置为内置、导入、打开目录 |
| **路径** | 环境数据目录、默认工作目录、Codex 应用检测 |
| **高级** | 运行详情与 `config.toml` 编辑 |
| **管理** | 编辑/迁移数据目录、复制、删除、打开数据目录 |

启动主按钮仍在 Tab 外，不与配置入口抢首任务。

## 默认系统提示词（默认替换）

每个环境**默认开启「替换系统提示词」**（`system_prompt_enabled = true`，保存/启动时强制保持开启，UI 不可关闭）。

- 通过 `managed_config.toml` 的 `model_instructions_file` **完全替换** Codex 内置指令
- **Keysmith 默认开启**：生效文件 `codex-home/gpt-unrestricted.md` + `config.toml` 指针 `./gpt-unrestricted.md`
- 关闭 Keysmith 时回退 **operator-core**（`assets/default-system-prompt/default-system-prompt.md`）→ `codex-home/system-prompt.md`
- 编辑源始终在 `.launcher/system-prompt.md`；新建环境自动种子
## Keysmith 执行模式（默认开启）

本启动器**原生集成** [codex-keysmith](https://github.com/Jia-Ethan/codex-keysmith) 的核心机制（不依赖 Python，**默认开启**）：

1. 将内置 `gpt-unrestricted.md` 写入指令文件，并设置 `model_instructions_file`（优先 `CODEX_HOME/gpt-unrestricted.md`）；
2. 将 `hooks.json` 整体隔离为 `hooks.json.disabled`；
3. 新建环境 / 首次加载默认开启；保存 / 启动时再次确保写入。

说明：配置层指令注入无法保证上游模型/网关 100% 不拒绝。

## 明文快捷 API 与系统提示词

在环境页打开 **AI** Tab：

- 可自由填写任意绝对 http/https Base URL；默认精确为 [https://ai98pro.xyz/](https://ai98pro.xyz/)（AI98 Pro 中转站，支持破限；详见上方「社区与中转站」），启动器不会自动追加 /v1。
- API Key 使用普通文本框完整显示，并以明文保存在当前环境的 `.launcher/ai-settings.toml`；不加密、不遮挡。概览 Tab 只显示是否已保存摘要。
- “测试连接 / 刷新模型”使用当前未保存输入**实时**请求 `<base>/models`，解析模型 id（含非 GPT），填充默认模型下拉；显示真实地址、HTTP 状态、耗时与模型数量。测试成功不会自动保存。
- 打开 AI 配置时若已填写地址与 Key，会后台实时拉取模型列表。下拉列表**不以**本地缓存为真值。
- 默认推理强度可选 `minimal` / `low` / `medium` / `high` / `xhigh` / `max` / `ultra`，写入 managed 的 `model_reasoning_effort`，并在 `model_catalog_json` 的 `supported_reasoning_levels` 中完整暴露。
- 保存并启用 API 时会再次实时拉取 `/models`，生成当前环境的 `.launcher/model-catalog.json`，并在 `managed_config.toml` 写入 `model`、`model_catalog_json` 与独立 provider，使 Codex 可选列表包含第三方模型（不仅限于 GPT）。
- 启动 Codex 时若 API 已启用，会再次实时拉取并覆盖 catalog，保证与网关一致；拉取失败则启动前硬失败，不会用过期目录假装成功。
- 可导入、替换或直接编辑 .md/.txt 系统提示词。产品默认**始终**通过 `model_instructions_file` 完全替换默认模型指令（开关强制开启）。
- 环境运行中可以保存，界面会提示“下次启动生效”。切换环境或关闭启动器时，未保存的 AI / 技能 / TOML 更改会分别确认。
- 说明：Codex Desktop 可能仍过滤部分非官方 slug 的显示（上游限制）；CLI/TUI 的 `/model` 与实际请求 slug 以本机 managed 配置为准。

## 环境复制 / 删除 / 数据迁移

- **复制环境**：管理 Tab、环境列表右键，或命令「复制环境」。复制 `config.toml`、API 设置与提示词；登录/会话/app-data 不复制。
- **删除环境**：删除启动器记录，并**永久删除对应数据目录**（校验 `.codex-profile.json` 身份后才删；运行中不可删）。
- **修改数据目录并迁移**：编辑环境 → 更改「环境数据目录」→ 保存。需先关闭该环境 Codex；启动器将整目录迁移到新路径，并改写配置中的绝对路径（同盘 `Move`，跨盘复制后清理源）。选择不存在或确实为空的目录时直接使用；选择普通非空父目录时，启动器会在其中自动创建并预览专属的 `CodexProfile-<32 位无连字符环境 ID>` 受管子目录。已有 `.codex-profile.json` 标记的环境根仍按精确目录校验，不会把非空父目录本身纳入迁移或递归删除范围。

## 内置技能库

仓库 `skills/builtin/` 随应用分发（构建时复制到 EXE 旁 `skills/builtin`）。每个技能是含 `SKILL.md` 的目录。技能列表带独立滚动条，条目较多时仍可浏览。

- **真值路径**：当前环境 `codex-home/skills/<id>/`（由启动器设置的 `CODEX_HOME` 发现；不依赖用户全局 `~/.agents/skills` 做环境隔离）。
- **启用**：从内置库复制到 `codex-home/skills`；**禁用**：移到 `.launcher/skills-disabled/<id>`（可再启用，保留本地修改）。
- **编辑**：改的是本环境副本；「重置为内置」用模板覆盖。
- **新建 / 复制环境**：默认安装全部内置技能（已存在目录则跳过）。
- **勿触碰** `codex-home/skills/.system`（Codex 系统技能）。
- Codex 已在运行时改技能后，需重开会话或重启该环境 Codex 后再索引。

## Store 运行缓存与启动机制

程序会从 Microsoft Store 包 `OpenAI.Codex_2p2nqsd0c76g0` 动态解析并核验当前安装版本。Store/MSIX 安装目录中的 `ChatGPT.exe` 不能可靠地当作普通 Win32 EXE 由 `CreateProcessW` / `Process.Start` 创建；已报告机器会直接返回 `Win32=5（拒绝访问）`。v1.1.6 因此不再围绕原包进程反复叠加 broker、breakaway 或 WMI 回退，而是把本机已安装版本的完整 `app` 目录复制到 `%LOCALAPPDATA%\CodexProfileLauncher\runtime-cache`，再从无 MSIX 包身份的本地副本启动。

运行缓存按 Store 包版本共享，而不是每个环境复制一份。首次准备会先写入独占的 staging 目录，复制完成并核验后才发布给其他启动请求；同版本并发启动共用准备结果，不会看到半成品。当前实测包约 1.8 GiB，普通磁盘首次复制通常需要 30–60 秒；空间不足、源包更新或复制/核验失败都会显式报错。Electron 运行后生成的受限 `Dictionaries/**/*.bdic` 字典缓存会保留并继续复用；链接点、非 `.bdic` 内容、单文件超过 64 MiB 或合计超过 256 MiB 都会使缓存失效并重建。Store 更新产生新包版本后会建立新的缓存目录；旧版本可能仍被正在运行的 Codex 使用，当前版本不会自动淘汰，须在关闭引用进程后由用户手动清理。

缓存准备完成后，启动器通过 `STARTUPINFOEX` 指定同一 Windows session 的 `explorer.exe` 为父进程，传入精确 argv、Unicode 环境块和工作目录。创建时不继承启动器的外层 Job；Electron 运行后自行进入某个 Job 是正常事实，不再被误判为启动失败。每个环境同时使用 `--user-data-dir` 与 `CODEX_ELECTRON_USER_DATA_PATH` 指向自己的 `app-data`，并覆盖 `CODEX_HOME`、`CODEX_SQLITE_HOME`。启动器仍核验根进程身份、精确 argv、窗口、两层数据写入与 app-server；证据不足时保留 receipt、显示“状态待确认”并阻止重复启动，不会静默宣称隔离成功。

本地副本没有 `PackageFullName`，所以 Codex 内置的 MSIX updater 可能记录“该进程没有程序包标识符”。当前真实探针中这条日志不影响窗口、内置 `codex.exe` 或 app-server；更新真值仍是 Microsoft Store 安装的源包，版本变化时由启动器建立新缓存。

## 数据位置

默认启动器数据位于：

```text
%LOCALAPPDATA%\CodexProfileLauncher\
├─ state\profiles.json       环境注册表和进程 receipt
├─ logs\                     本地 JSONL 诊断日志
├─ runtime-cache\            按 Store 包版本共享的完整 app 运行副本（约 1.8 GiB/版本）
└─ profiles\<UUID>\
   ├─ .codex-profile.json    不可变环境身份
   ├─ .launcher\
   │  ├─ ai-settings.toml    API 地址、启停、默认模型、推理强度与完整明文 Key
   │  ├─ model-catalog.json  实时拉取后生成的 Codex 模型目录
   │  ├─ system-prompt.md    系统提示词完整内容
   │  └─ skills-disabled\    已禁用但保留的技能副本
   ├─ codex-home\            CODEX_HOME、配置、凭据、SQLite、会话
   │  ├─ config.toml         用户可编辑配置
   │  ├─ managed_config.toml 隔离关键项锁定层
   │  ├─ skills\             本环境已启用技能（含 .system）
   │  └─ log\                当前环境的 Codex 日志
   └─ app-data\              Codex/Chromium 应用数据
```

新环境的用户 `config.toml` 默认使用文件型凭据存储，并显式锁定原生 Windows 后端：

```toml
cli_auth_credentials_store = "file"
mcp_oauth_credentials_store = "file"
desktop.runCodexInWindowsSubsystemForLinux = false
```

这些配置是正确的隔离配置。旧版的 `PROCESS_CREATE_SUSPENDED_ACCESS_DENIED` 发生在 Windows 创建 Store Codex root 的阶段，早于 Codex 读取 `config.toml`，因此不能通过修改上述三项解决；v1.1.6 改由经过核验的本地运行缓存创建进程。

启动器维护的 `managed_config.toml` 则把凭据存储、SQLite 和日志目录强制锁定在当前环境：

```toml
cli_auth_credentials_store = "file"
mcp_oauth_credentials_store = "file"
sqlite_home = "<当前环境>/codex-home"
log_dir = "<当前环境>/codex-home/log"
```

快捷 API 启用时，受管层还会写入默认 `model`、`model_catalog_json` 与独立 provider，并在启动 Codex 时把当前环境的明文 Key 注入专用环境变量；Key 不进入命令行或 managed_config.toml。每次启动都会同时把 `CODEX_ELECTRON_USER_DATA_PATH` 与 `--user-data-dir` 精确设置为当前环境的 `app-data`。快捷设置关闭后，对应 model/catalog/provider 或提示词覆盖会从受管层移除，原始高级配置重新生效。

启动前还会扫描工作目录的 `config.toml` 以及从工作目录向上的每一级 `.codex/config.toml`。这些项目层若尝试覆盖上述四个隔离关键项、启用 WSL 后端，或 TOML 无法严格解析，启动会显式阻止。普通模型、权限等项目配置不受此限制。

删除环境会在校验目录身份标记后，同时移除启动器记录与对应受管数据目录；运行中或状态无法确认时会拒绝删除。同一 Windows session 且窗口身份核验通过时，“关闭 Codex”会先确认并请求正常退出，最多等待 20 秒；若没有可关闭窗口、实例位于其他 session，或超时后仍有受管成员，程序会显示成员数量并要求第二次明确确认。任何强制结束都必须重新核验 receipt 中的精确 root generation 与进程树身份；发现 PID 复用、路径或 generation 冲突时会拒绝操作并保留 receipt，不按进程名批量结束。

## 构建与测试

仓库锁定 .NET SDK 10.0.301 和 NuGet 内容哈希：

```powershell
dotnet restore .\CodexProfileLauncher.slnx --locked-mode
dotnet test .\CodexProfileLauncher.slnx -c Release --no-restore
dotnet publish .\src\CodexProfileLauncher\CodexProfileLauncher.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o .\artifacts\publish\win-x64
```

完整构建与发布门禁可直接运行（真实 UI 与进程生命周期证据另见验证报告）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Verify-Release.ps1
```

最终发布目录必须包含唯一的自包含 EXE，并保留随包发布的 `Assets` 与 `skills/builtin` 目录。门禁同时生成并校验 `artifacts/CodexProfileLauncher-v1.1.6-win-x64.zip`；向其他电脑分发时优先使用该完整 ZIP。完整架构与真实入口证据见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) 和 [docs/VERIFICATION.md](docs/VERIFICATION.md)。

## 边界与卸载

- 目标平台：Windows 10 22H2（build 19045）或仍受支持的 Windows 11 x64；需要已安装 Microsoft Store 版 Codex。
- 启动 Codex root 时，启动器显式移除父进程的 `OPENAI_API_KEY`、`CODEX_API_KEY` 与 `CODEX_ACCESS_TOKEN`，把 `CODEX_HOME`、`CODEX_SQLITE_HOME` 覆盖为当前环境的 `codex-home`，并把 `CODEX_ELECTRON_USER_DATA_PATH` 覆盖为当前环境的 `app-data`；其余父进程环境变量继承。这项边界不会复制既有磁盘登录/会话，但也不声称隔离 Windows 凭据管理器、系统级单点登录或其他未被移除的环境变量。
- `--user-data-dir` 是当前 Codex 桌面版本实测可用、但未列入 OpenAI 公共配置参考的启动参数，因此每次启动都会重新核验真实 argv、窗口、两层数据写入和 app-server；版本变化时不会静默假成功。
- WSL 后端不受支持，`desktop.runCodexInWindowsSubsystemForLinux` 必须在当前环境的 `config.toml` 中显式且严格为 `false`。当前桌面版启用 WSL 后会把 SQLite 放在 Linux 用户共享目录，且不会加载每个 Windows `CODEX_HOME` 中的托管隔离配置；显式 `false` 优先于旧版全局状态并使其中的遗留开关失效，不依赖清理该遗留值。因此启动器会在创建 root 前强制核验它。旧环境若缺少该行，会被安全阻止，补上后即可继续使用。
- `CodexProfileLauncher.exe` 是自包含单文件，用户无需另装 .NET Runtime；交付包中的 `Assets` 与 `skills/builtin` 是运行时内容，解压后须与 EXE 保持原目录结构。程序会在 `%LOCALAPPDATA%\CodexProfileLauncher` 创建状态、日志与环境数据。
- 此交付件没有 Authenticode 签名，Windows 可能显示未知发布者或 SmartScreen 提示。运行前请用 `Get-FileHash .\CodexProfileLauncher.exe -Algorithm SHA256` 与 `artifacts\release-metadata.json` 中的最终 SHA-256 核对，不要沿用旧发布件哈希。
- v1.1.6 的正常启动不再依赖 broker、`CREATE_BREAKAWAY_FROM_JOB` 或 WMI。即使启动器从微信/QQ/浏览器/压缩软件等受外层 Job 约束的宿主打开，也会从共享本地运行缓存创建 Codex，并优先指定同 session `explorer.exe` 为父进程。启动前会核验 explorer 的 session、缓存来源、目标映像、argv、环境和工作目录；失败会保留真实错误，绝不回退到直接执行 Store `ChatGPT.exe`。只有 explorer-parent 在创建前失败时才允许直接启动已核验的本地副本，详情会明确标注其生命周期边界。
- 运行缓存不是发布目录自带的 Codex 副本，而是首次使用时从本机已安装且已核验的 Store 包生成。每个 Store 版本约需 1.8 GiB 额外空间；新版本缓存建立后旧版本仍会保留，当前版本不自动淘汰。手动清理旧缓存前必须先关闭引用它的 Codex。
- 异常堆栈中的源码路径来自编译时调试元数据，不表示运行机正在访问开发机磁盘。旧发布件可能显示开发机绝对路径；v1.1.6 Release 继续通过 `PathMap` 把物理路径映射为 `/_/<项目名>/...`，同时保留源码文件名与行号用于诊断。
- 卸载前先关闭所有受管 Codex。删除 EXE 不会自动删除环境数据或 `runtime-cache`；如确需清除，应在确认不再需要账号、会话、配置及旧版本运行缓存后手动处理上述数据目录。

第三方许可材料位于 [licenses](licenses)。
