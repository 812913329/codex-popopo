# 验证报告

## v1.1.6 Store 运行缓存与 explorer 父进程启动（当前主机最终验证 2026-07-20）

本节记录 v1.1.6 的当前主机最终发布门禁与真实 Store 启动证据。v1.1.5 及更早章节仅保留为历史记录；最初报告失败的另一台电脑仍需用本节精确产物复测，当前主机结果不外推为目标机已通过。

| 项目 | 最终结果 | 证据 |
| --- | --- | --- |
| 原始失败 | 部分机器不能把 Store/MSIX 安装目录内的 `ChatGPT.exe` 当普通 Win32 EXE 通过 `CreateProcessW` / `Process.Start` 创建，返回 `Win32=5（拒绝访问）`；旧版还把 explorer-parent 创建后“属于任意 Job”误判为失败 | 目标机截图及错误详情；旧路径已从正常启动链移除 |
| AUMID 边界 | `IApplicationActivationManager::ActivateApplication` 能激活 Store 包，但启动后的 PEB 不继承逐环境 `CODEX_HOME`、`CODEX_SQLITE_HOME`、marker 或 cwd，不能满足隔离契约 | 真实运行探针；未进入生产路径 |
| 生产启动链 | 从本机已安装且已核验的 Store 包建立完整版本缓存，从无 MSIX 包身份的本地副本启动；正常路径指定同 session `explorer.exe` 为父进程，不再使用 broker、breakaway 或 WMI | Release 生产代码整链测试连续 2 次通过 |
| 缓存粒度与并发 | 每个 Store 包版本共享一份副本；`Global` SID mutex 串行准备，staging 完成校验并最后写 marker 后才原子发布；严格清理遗留 staging/replaced，拒绝遍历 reparse point | `CodexRuntimeMirrorManagerTests` 17 / 17；Windows 全量门禁包含该组 |
| 磁盘与首次时间 | 当前 `OpenAI.Codex_26.715.4045.0_x64__2p2nqsd0c76g0` 的源 `app` 为 9,494 个文件、1,924,346,539 bytes（约 1.792 GiB）；首次完整复制探针约 42 秒，用户提示为 30–60 秒 | 本机 Store 包与复制探针 |
| 初始副本完整性 | 初次发布时 payload 的路径、长度和时间元数据与源快照一致；关键文件执行 SHA-256。源与副本 `ChatGPT.exe` SHA-256 均为 `305B25FA057C35241C2C27BCB1112450F35EEE12C1D4B1E4D74C073454914346` | artifact-confirmed |
| 运行态字典复用 | 仅当源包本身没有顶层 `Dictionaries` 时，允许副本生成无 reparse 的 `Dictionaries/**/*.bdic`；单文件上限 64 MiB、总计 256 MiB。其他额外内容会使缓存失效并重建 | 合法 `.bdic` 复用与非法 DLL 重建直接回归通过；真实 Release 连续运行 9.95 秒、10.20 秒，marker 时间戳 `639201389820237463` 与 SHA-256 `6A272282D595D83F66D9B7AFFFAEA77CAC25763CB66756E89331849BD5AB2DDD` 前后不变 |
| explorer 父进程 | `STARTUPINFOEX` / `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` 指定同 session explorer，以 suspended 状态创建副本 root；恢复前从 PEB 精确核验目标映像、父 PID、argv、完整 Unicode 环境块和 cwd | 两次真实 Release 整链均返回 `SuspendedProcessParametersVerified=True` |
| 包身份与 Job | 副本 root 的 `GetPackageFullName` 返回 `APPMODEL_ERROR_NO_PACKAGE (15700)`。恢复后 Electron 进入某个 Job 是正常运行事实，不再作为失败条件 | 两次真实 Release 整链通过；直接 Store EXE 不再被执行 |
| 环境隔离 | PEB 精确包含 `CODEX_ELECTRON_USER_DATA_PATH=<profile app-data>`、`CODEX_HOME=<profile codex-home>`、`CODEX_SQLITE_HOME=<profile codex-home>` 与唯一 marker；argv 同时含 `--user-data-dir`、`--new-window`；app-data、codex-home 和 app-server 均命中临时 profile | 两次真实 Release 整链通过；每次使用独立临时 profile 并精确清理 |
| 创建器退出 | 创建 TestHost 退出后，root 仍存活、窗口可见且响应；等待 5 秒后镜像内 `codex.exe` 和 app-server 仍正常 | 两次真实 Release 整链通过；按 exact PID/start/path/lineage 回收 |
| 非空目录 | 新环境选择普通非空父目录时使用 `CodexProfile-<32 位无连字符环境 ID>` 受管子目录；不存在/空目录和已有 marker 根保持精确路径；旧版裸 GUID 子目录继续沿用，避免升级后误迁移。父目录本身永不进入递归删除或迁移范围 | Core 选择/迁移回归 8 / 8；ViewModel 回归 4 / 4；全量门禁通过 |
| Release 源路径 | Release `PathMap` 把编译机物理目录映射为 `/_/<项目名>/...`，因此运行机异常中的旧开发盘符只是旧发布件调试元数据，不是运行时磁盘访问 | v1.1.6 Release 回归随 Windows 全量门禁通过 |
| Release 构建与测试 | 锁定还原成功；构建 0 warning、0 error；Core 102 / 102；Windows 90 passed、1 skipped（真实 Store 测试默认 opt-in）；随后显式启用真实 Store 测试并连续通过 2 / 2 | `.NET SDK 10.0.301`；`artifacts/test-results/*.trx`；在线 NuGet audit 为确定性离线门禁显式关闭，不声称完成外部漏洞审计 |
| 发布目录 | `artifacts/publish/win-x64/CodexProfileLauncher.exe`，71,737,473 bytes（68.41 MiB），版本 1.1.6 / 1.1.6.0，SHA-256 `4807B95A33E04D2B9CA601A962D69CD51FC9987B74876C99802DAC2277423B49`，Authenticode `NotSigned` | `artifacts/release-metadata.json`；publish 共 131 个文件、14 个内置技能。正式 ZIP 为 `artifacts/CodexProfileLauncher-v1.1.6-win-x64.zip`，67,078,592 bytes、131 个文件条目、SHA-256 `175C35F2A0EBCEF41EC2C70E99C2D79420A09E479971959B6B73296F3EB31AD5`；中文别名 `code内置破限1.16.zip` 与其字节完全一致 |
| updater 日志 | 本地副本没有包身份，MSIX updater 可能记录“该进程没有程序包标识符”；当前探针中窗口、内置 `codex.exe` 和 app-server 均不受影响，更新真值由 Store 源包版本接管 | 已知非致命边界；日志保留，不吞掉其他错误 |
| 问题目标机 | 用户给出的旧错误已证明 explorer-parent 分支曾成功创建 PID，只是被旧版“属于任意 Job”门禁拒绝；新架构同时移除了该门禁和直接 Store EXE 创建。但 EDR/WDAC 若进一步禁止本地副本或 explorer-parent，仍只能在目标机显式暴露 | **待最初问题电脑用本节 SHA-256 产物复测** |

### v1.1.6 验证矩阵

| 验证面 | 当前最终证据 | 边界 / 后续 |
| --- | --- | --- |
| Store 源包 → 完整版本缓存 | 17 / 17 专项回归覆盖 staging、并发复用、损坏重建、遗留清理、reparse、版本保留和受限字典；真实缓存已建立 | 磁盘竞争发生在预检查之后仍会显式抛出真实 IO 错误 |
| Release 生产启动入口 | 真实 Store + Release 生产 manager/desktop-parent 连续 2 / 2；窗口、PEB、无包身份、app-server、创建器退出后存活全部通过 | 通过 TestHost 调用生产入口，不等同于人工点击发布 EXE 的 UI；ViewModel 路由由自动化回归覆盖 |
| 缓存后续复用 | 两次最终 Release 真实运行分别为 9.95 秒、10.20 秒，marker 时间戳和哈希完全不变 | Store 版本或已校验内容变化时会建立/重建对应版本缓存 |
| profile 数据隔离 | 每次真实运行的 env/argv、app-data、codex-home 与 app-server 均命中唯一临时路径 | 本轮未重新做两个真实 profile 同时并行；跨 profile 路径/重复 root 由现有回归门禁覆盖 |
| Store 更新 | 合成新版本测试证明不会删除旧版本；当前实现不自动淘汰旧缓存 | 关闭引用旧缓存的 Codex 后由用户手动清理，不能在启动路径中强删 |
| 非空父目录 | 受管子目录、隐藏/系统条目、空子目录、marker、迁移及旧裸 GUID 兼容均通过 | 目标机仍需按真实用户目录复测 |
| 最初问题电脑 | 当前主机已经命中并通过同类 Store 策略与外层 Job 反例 | 仍须安装本节精确哈希产物，验证首次缓存、窗口、两层写入和 app-server |

兼容与磁盘风险：运行缓存从用户本机已安装的 Store 包生成，不随发布目录分发；每次 Store 更新可能再增加约 1.8 GiB。当前版本不会自动淘汰旧缓存，关闭引用进程后才可手动清理。正常 explorer-parent 路径保证创建器退出后存活；若系统在创建前阻止该路径，程序只允许直接启动已核验的本地副本，并在详情中明确“独立存活不保证”。任何路径都不会回退到 WindowsApps 原始 `ChatGPT.exe`，也不会把不完整缓存标记为成功。
契约依据：[CreateProcessW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)、[UpdateProcThreadAttribute / PROC_THREAD_ATTRIBUTE_PARENT_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)、[GetPackageFullName](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getpackagefullname)、[IApplicationActivationManager::ActivateApplication](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication)、[C# PathMap](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/advanced)。

## v1.1.5 Store Codex 挂起创建拒绝访问兼容启动（最终验证 2026-07-20）

本节记录已发布 v1.1.5 的历史行为与最终证据；当前候选架构见上方 v1.1.6 章节。

| 项目 | 结果 |
| --- | --- |
| 报告入口 | 部分机器在 detached broker 已成功后，对 Store 包 `OpenAI.Codex_26.715.4045.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe` 执行严格 `CreateProcessW(CREATE_SUSPENDED)` 返回 `Win32=5（拒绝访问）`，原错误码为 `PROCESS_CREATE_SUSPENDED_FAILED` |
| 配置裁决 | 截图中的 `model_instructions_file`、两个 `credentials_store = "file"` 与 `desktop.runCodexInWindowsSubsystemForLinux = false` 均不是该失败原因；拒绝发生在 Windows 创建 root 的阶段，早于 Codex 读取环境内 `config.toml` |
| 根因边界 | 已确定阻断点是“broker + Store EXE + 挂起/严格 Job 创建组合”；WindowsApps ACL、应用控制、EDR/杀软或 creator token 中究竟哪项策略造成机器差异，现有截图不足以进一步唯一归因 |
| 错误专门化 | `CreateProcessW` 返回 false 后立即保存 native error；仅 `5`、`0x80070005`、`0xC0070005` 被分类为 `PROCESS_CREATE_SUSPENDED_ACCESS_DENIED`，不解析本地化 Details 文本 |
| 自动路由 | 兼容模式只接受 `JOB_BROKER_BREAKAWAY_INCOMPLETE` 与上述专用 AccessDenied code；generic suspended create、Job assign、resume、身份核验和持久化错误均保持 fail-closed |
| 事务安全 | 专用错误只在 `CreateProcessW` 返回 false 的唯一产生点生成，此时 `createdProcess=false`、没有 strict root、fresh Job 仍为空；严格事务与 broker 完整退出后，调用层先把同一 pending receipt 原子转换并持久化为兼容 intent，再启动 direct root，不会双启动 |
| 兼容启动契约 | 复用原始 `ProcessStartInfo`，保留精确 `ArgumentList`、清理/覆盖后的环境块与 `WorkingDirectory`；固定并核验 exact native handle、PID/start/path、当前 SID、session 与存活状态 |
| 隔离完成门禁 | 仍要求精确 `--user-data-dir`、可见窗口、当前 app-data 写入、当前 `CODEX_HOME` 写入、Codex app-server 及无进程树检查错误；任一证据不足都会显示“状态待确认”并阻止重复启动，不伪造成功 |
| 生命周期边界 | 兼容模式不承诺 dedicated Job、`KILL_ON_JOB_CLOSE` 或独立存活；UI、运行详情和日志持续标注该边界。数据隔离证据不因此放宽 |
| 真实启动探针 | 当前 Store Codex 包在全新临时 `CODEX_HOME`、`CODEX_SQLITE_HOME`、`--user-data-dir` 与指定 cwd 下用普通 `Process.Start(UseShellExecute=false)` 成功创建；探针随后按 exact handle 终止并确认无残留 |
| 被否决替代 | `PROC_THREAD_ATTRIBUTE_JOB_LIST` 原子非挂起实测返回 `0xC0070005`；package debug/dummy debugger 在当前主机不可用（`E_NOTIMPL`），且有包级持久状态、并发、cwd 与恢复风险，未进入生产路径 |
| Release 构建 | 0 warning，0 error（.NET SDK 10.0.301，`System.Management` 10.0.10） |
| Core tests | 91 / 91 |
| Windows tests | 65 / 65；新增 AccessDenied 分类与路由、generic/assign/other fail-closed、receipt 转换回归均通过 |
| 发布物回归 | 最终 self-contained v1.1.5 EXE 作为 `CPL_PUBLISHED_BROKER_PROBE_PATH`，真实 WMI broker 生命周期测试通过；测试内部连续 2 次验证 ready、empty inner Job、cancel 与回收 |
| 发布堆栈 | Release `PathMap` 回归通过：实际异常源位置为 `/_/<项目名>/...` 且不含编译机物理根；运行机显示开发机路径的问题不会在 v1.1.5 发布件复现 |
| 发布 EXE | `artifacts/publish/v1.1.5-win-x64/CodexProfileLauncher.exe`，71,712,203 bytes，版本 1.1.5 / 1.1.5.0 |
| EXE SHA-256 | `8EEF1BB8AF02A301C2AF8191F54BE68A86AC98257798E9E957E57B34D94CE856` |
| Authenticode | `NotSigned` |
| 完整 ZIP | `artifacts/CodexProfileLauncher-v1.1.5-win-x64.zip`，67107495 bytes；141 个文件条目、唯一根 EXE、14 个内置技能；中文别名 `codex破限版本1.15.zip` 与其字节完全一致 |
| ZIP SHA-256 | `F4929B0D3D5167C8D48AAAE07C8E3BFC96FC8A33BEE81579E9F4220932232F51` |
| NuGet 门禁边界 | 锁定还原与内容哈希通过；本次为确定性离线门禁关闭在线 `NuGetAudit`，不声称完成外部漏洞审计 |

当前主机已覆盖失败点源码、精确回退边界、真实 direct Store Codex 探针、全量测试与最终单文件 broker 生命周期，但不能替代报告问题的另一台电脑。目标机必须使用本节精确哈希的 v1.1.5 包复测：应自动显示“Codex 正在运行（兼容模式）”，并确认窗口、`--user-data-dir`、当前 app-data / `CODEX_HOME` 写入与 app-server 全部通过。若普通启动也被该机策略拒绝，程序会保留 strict 与 compatibility 两段真实错误并显式失败，不会继续扩大静默回退。

契约依据：[CreateProcessW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)、[PROC_THREAD_ATTRIBUTE_JOB_LIST](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)、[IPackageDebugSettings::EnableDebugging](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ipackagedebugsettings-enabledebugging)、[IApplicationActivationManager::ActivateApplication](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication)。

## v1.1.4 外层 Job 兼容启动（最终验证 2026-07-20）

本节是当前 v1.1.4 发布件的权威增量记录；v1.1.3 及更早章节保留为历史证据。

| 项目 | 结果 |
| --- | --- |
| 目标机事实 | v1.1.3 仍返回 `JOB_BROKER_BREAKAWAY_INCOMPLETE`，且 breakaway、explorer-parent、带 `CreateFlags=0x01000000` 的 WMI 三条路径都已创建 PID、但最终仍属于任意 Job；这证明阻断点是目标机持续强制 containment，而不是 WMI 参数或服务不可用 |
| 方向变更 | 不再叠加同类用户态“脱离 Job”技巧；正常机器继续使用完整 detached broker + fresh Job，只有最终错误码精确为 `JOB_BROKER_BREAKAWAY_INCOMPLETE` 时自动切换外层 Job 兼容模式，其他 broker、路径、身份、配置或持久化错误仍显式失败 |
| 兼容模式契约 | 在当前系统 containment 内用 `Process.Start` 创建 Codex，固定 `Process.Start` 返回的 exact native handle，并核验 PID/start/path、当前用户 SID、Windows session 与存活状态；`IsInAnyJob` 只记录诊断事实，不再阻断可用性 |
| 隔离完成门禁 | 兼容模式仍要求精确 `--user-data-dir`、根进程身份、可见窗口、当前 app-data 写入、当前 `CODEX_HOME` 写入、Codex app-server 及无进程树检查错误；不把单一 PID 或窗口当作成功 |
| 生命周期边界 | UI、运行详情与日志持续显示“兼容模式”；数据隔离可验证，但不承诺 Codex 脱离外层 Job，进程可能随启动宿主或安全策略退出。若必须同时保证独立存活，需要另行安装位于外层 Job 之外的受限 Windows service/keeper |
| 崩溃恢复 | Windows Job pending intent 会先原子转换并保存为 legacy pending，再创建进程并持久化 exact PID/start/path；中途退出可按唯一 profile argv 恢复，日志写入失败不会让已验证且已持久化的进程变成孤儿 |
| 目标失效回归 | synthetic outer Job 启用 `KILL_ON_JOB_CLOSE` 且不允许 breakaway；TestHost 与兼容子进程均通过 `IsProcessInJob(child, exactOuterJob)` 确认属于同一外层 Job，兼容启动成功并完成精确回收 |
| 自动路由回归 | 精确 breakaway incomplete 会启用兼容模式；其他 Job 错误不会降级；Windows Job receipt → legacy pending 的字段转换与 launch/profile/path 保留均有直接测试 |
| Release 构建 | 0 warning，0 error（.NET SDK 10.0.301，`System.Management` 10.0.10） |
| Core tests | 91 / 91 |
| Windows tests | 61 / 61 |
| 发布物回归 | 最终 self-contained v1.1.4 EXE 连续 2 次走真实 WMI broker 生命周期；ready、empty inner Job 与 cancel 回收通过 |
| 发布堆栈 | Release `PathMap` 回归通过：源码位置保留为 `/_/<项目名>/...`，不包含编译机物理根 |
| 发布 EXE | `artifacts/publish/win-x64/CodexProfileLauncher.exe`，71,712,363 bytes，版本 1.1.4 / 1.1.4.0 |
| EXE SHA-256 | `31ECBE01E0956E0A3ACDF66AA5C4C9A651EB466FB59BDA1183FA924EE09609C1` |
| Authenticode | `NotSigned` |
| 完整 ZIP | `artifacts/CodexProfileLauncher-v1.1.4-win-x64.zip`，67,105,055 bytes；141 个文件条目、唯一根 EXE、14 个内置技能 |
| ZIP SHA-256 | `B5868957D5989C9237CDFA992AB18C92919EC0C5D05E527354ECA89E948897C6` |
| NuGet 门禁边界 | 锁定还原与内容哈希通过；当前环境无法访问 NuGet 漏洞索引，因此发布脚本显式关闭在线 `NuGetAudit`，本节不声称完成外部漏洞审计 |

当前主机已覆盖源码、自动降级判定、receipt 转换、原始 non-breakaway Job 失效机制、全量测试与最终单文件 broker 生命周期；它不能替代报告问题的另一台电脑本身。目标机验收应以 v1.1.4 是否显示“Codex 正在运行（兼容模式）”、窗口是否出现以及两层隔离证据是否通过为准。兼容模式退出风险是显式能力边界，不再作为“无法打开”的硬阻断。

契约依据：[Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)、[AssignProcessToJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject)、[Nested Jobs](https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs)、[IsProcessInJob](https://learn.microsoft.com/en-us/windows/win32/api/jobapi/nf-jobapi-isprocessinjob)。

## v1.1.3 WMI breakaway 参数与发布堆栈路径修复（最终验证 2026-07-20）

本节是当前 v1.1.3 发布件的权威增量记录；v1.1.2 及更早章节保留为历史证据。

| 项目 | 结果 |
| --- | --- |
| 目标机复测入口 | v1.1.2 的 direct、explorer-parent、WMI 三条路径均成功创建 PID，但三者 `IsProcessInJob(..., NULL)` 仍为 true；WMI PID=40880 证明 WMI 服务与 `Win32_Process.Create` 调用本身可用 |
| v1.1.2 设计缺口 | WMI 调用把 `ProcessStartupInformation` 留空，没有请求 WMI provider 创建子进程时 breakaway；这能解释目标机结果，但是否为目标机唯一约束仍须用 v1.1.3 在该机复测 |
| v1.1.3 修复 | 创建 `Win32_ProcessStartup`，只设置官方支持的 `CreateFlags = CREATE_BREAKAWAY_FROM_JOB (0x01000000)`，并传入 `Win32_Process.Create`；返回 PID 后继续核验映像、用户 SID、session、存活状态及不属于任何 Job |
| 失败语义 | 若 WMI 已创建 PID 但仍在 Job，错误明确说明系统/安全策略仍在强制 containment；不再错误提示“WMI 服务不可用”，也不放宽为假成功 |
| 发布堆栈 | Release-only `PathMap` 把物理项目目录映射为 `/_/<项目名>`，保留源文件名和行号；回归断言堆栈不含工作区物理根 |
| Release 构建 | 0 warning，0 error（.NET SDK 10.0.301，`System.Management` 10.0.10） |
| Core tests | 91 / 91 |
| Windows tests | 57 / 57 |
| 发布物回归 | 最终 self-contained v1.1.3 EXE 连续 2 次走真实 WMI broker 生命周期；ready、empty inner Job 与 cancel 回收通过 |
| 发布 EXE | `artifacts/publish/win-x64/CodexProfileLauncher.exe`，71,708,113 bytes，版本 1.1.3 / 1.1.3.0 |
| EXE SHA-256 | `A85E2600741CEE25130D0988947B22577125D84BAF86071E2379F348538EDF5E` |
| Authenticode | `NotSigned` |
| 完整 ZIP | `artifacts/CodexProfileLauncher-v1.1.3-win-x64.zip`；140 个文件条目、唯一根 EXE、14 个内置技能 |
| ZIP SHA-256 | `255EC2AE1E662CE4CC8FEE0279951956A98AC14453E4021273EC8440B9B63A80` |

当前主机已验证实现、全量测试与最终单文件 EXE，但不能替代报告问题的另一台电脑。如果该机使用 v1.1.3 后仍返回 `JOB_BROKER_WMI_BREAKAWAY_INCOMPLETE` 且 details 含 `CreateFlags=0x01000000`，则说明仍有不可由当前进程修改的祖先 Job 或安全软件重新分配；Windows 没有通用、无安装、非特权的第四条用户态脱离 API。此时必须保留 fail-closed，或另行设计并安装位于外层 Job 之外的受限 Windows service/keeper，不能把可能随原宿主退出的进程伪装成已完全脱离。

契约依据：[Win32_ProcessStartup](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-processstartup)、[Process creation flags](https://learn.microsoft.com/en-us/windows/win32/procthread/process-creation-flags)、[IsProcessInJob](https://learn.microsoft.com/en-us/windows/win32/api/jobapi/nf-jobapi-isprocessinjob)、[Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)、[C# PathMap](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/advanced)。

## v1.1.2 外层 Job 脱离修复（最终验证 2026-07-20）

本节是当前 v1.1.2 发布件的权威增量记录；下方 v1.3/v1.2 功能记录与 v1.1.0/v1.0 生命周期记录保留为历史证据。

| 项目 | 结果 |
| --- | --- |
| 报告入口 | `JOB_BROKER_BREAKAWAY_INCOMPLETE`：direct breakaway 与 explorer-parent 创建的 broker 均仍属于外层 Job |
| 根因 | `CREATE_BREAKAWAY_FROM_JOB` 受外层 Job 的 breakaway 策略约束；`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` 会继承指定 explorer 的 Job 属性，故 explorer 自身受管时第二条路径也不能脱离 |
| 修复 | 新增第三条本机 WMI `Win32_Process.Create` 路径，使用 `System.Management` 10.0.10；创建后仍以原生 API 核验映像、当前用户 SID、Windows session、存活状态及 `IsProcessInJob(..., NULL) == false` |
| Release 构建 | 0 warning，0 error（.NET SDK 10.0.301） |
| Core tests | 91 / 91 |
| Windows tests | 56 / 56 |
| 发布物回归 | 最终 self-contained EXE 连续 2 次启动真实 `--job-broker`；ready、`KILL_ON_JOB_CLOSE`、空 inner Job、cancel 后 broker/Job 回收全部通过 |
| 发布 EXE | `artifacts/publish/win-x64/CodexProfileLauncher.exe`，71,707,748 bytes，版本 1.1.2 / 1.1.2.0 |
| EXE SHA-256 | `EC40C55D2A838C3963A961F05BF37DDEB889E2F0D40CAAD8B60D9AB16DB248CB` |
| Authenticode | `NotSigned` |
| 完整 ZIP | `artifacts/CodexProfileLauncher-v1.1.2-win-x64.zip`；包含唯一根 EXE、Assets、14 个内置技能及使用说明 |
| ZIP SHA-256 | `44582D6D10042CD715B3DA839D231136C5782880B64A7B459911CDFC49018F55` |

WMI 调用在真正进入 `Create` 前允许一次有界重试；进入创建调用后不重试，避免不确定结果造成重复 broker。WMI 返回 PID 后，任何身份/Job 核验失败都会显式报错；仅当已确认是目标映像时才执行精确终止清理。

当前主机已覆盖原始失效机制与最终发布物，但无法代替报告问题的另一台电脑本身；若该机器禁用了 Windows Management Instrumentation，第三条路径会保留真实 WMI 错误并继续以 `JOB_BROKER_BREAKAWAY_INCOMPLETE` 拒绝启动，而不会伪装成功。

## v1.3 环境 Tab + 内置技能库（开发验证 2026-07-20）

| 项目 | 结果 |
| --- | --- |
| 范围 | 环境页顶部导航（概览/AI/技能/路径/高级/管理）；AI 上移为 Tab；内置技能库入库与按环境安装 |
| Core 全量测试 | 85 / 85 通过 |
| 技能单元测试 | 4 / 4（frontmatter、启用/禁用、InstallAll 跳过已有、导入校验） |
| Release 构建 | 成功；输出目录 `skills/builtin` 含 14 个技能 |
| 仓库内置库 | `skills/builtin` 约 2.4 MiB，已排除 `__pycache__` |

关键行为证据（开发机）：

- `ProfileSkillsService`：启用写入 `codex-home/skills`；禁用迁入 `.launcher/skills-disabled`；`.system` 受保护；重置覆盖为内置 `SKILL.md`。
- 新建环境路径调用 `InstallAllBuiltin`（单测：已有目录不覆盖）。
- UI 源码：`MainWindow.xaml` 含 `EnvironmentTabList`、`AiSettingsPanel`、`SkillsPanel`；启动面板仍在 Tab 外。
- 本机「日常开发」profile 已种子安装 14 个用户技能（不含 `.system`）。

未在本轮完成（残余）：

- 未重新跑完整 `Verify-Release.ps1` / 单文件 publish 哈希更新。
- 未重新捕获 UI 截图（`artifacts/screenshots/*` 仍为 v1.0/v1.1 布局基线，**不能**证明 Tab/技能 UI）。
- 未在真实 Codex Desktop 会话内逐条核验技能 `$` 选择器索引时机（依赖重开会话）。

## v1.2 模型目录实时拉取（开发验证 2026-07-20）

| 项目 | 结果 |
| --- | --- |
| 范围 | 第三方 `/models` 实时拉取 → Codex `model_catalog_json` + 默认 `model`；推理阶梯含 max/ultra |
| Core 测试（含 AI） | 通过（并入后续 85 全量） |
| 关键行为 | 打开配置/测试/刷新/保存/启动 resolve 均实时 `GET /models`；非 GPT id 写入 catalog；SelectedModel 不在当次列表则保存失败；拉取失败硬失败；catalog `supported_reasoning_levels` 含 max/ultra |

自动化覆盖：

- 解析 `id` / `model` / `model_id`，去重保序
- 保存时写 `.launcher/model-catalog.json` 与 managed 中的 `model` / `model_catalog_json` / provider
- SelectedModel 为空时自动取实时列表首项
- SelectedModel 不在实时列表、HTTP 拉取失败时拒绝保存
- 停用 API 后 managed 移除 model/catalog/provider
- ViewModel：测试连接填充非 GPT 列表并保留已选模型

残余风险：Codex Desktop 上游可能仍过滤非官方 slug 的**显示**；CLI/TUI 与实际请求 slug 以 managed 为准。本轮未重做完整 publish 与真实网关联调截图。

## v1.1.0 增量验证（2026-07-19）

本节是当前发布件的权威增量记录；下方 v1.0 生命周期记录仍作为原有隔离/Job 行为证据保留。

| 项目 | 结果 |
| --- | --- |
| 文件 | artifacts/publish/win-x64/CodexProfileLauncher.exe |
| 大小 | 71,513,543 bytes（68.20 MiB） |
| SHA-256 | C4E5C0E74F99A6647D60BD19BFCDC9C979C740E1D60F613869DDBC4C7091DBDF |
| Authenticode | NotSigned |
| 产品版本 | 1.1.0（文件版本 1.1.0.0） |
| Core tests | 76 / 76 |
| Windows tests | 54 / 54 |
| 合计 | 130 / 130，0 failed，0 skipped |

新增自动化覆盖明文设置文件往返、默认地址字节级一致且无 /v1、RevisionToken 外部冲突、停用/清空、系统提示词完全替换、受管 provider 生成、父进程变量清除、当前 profile Key 注入和多环境隔离。Windows ViewModel 测试覆盖明文 Key、未保存输入测试、冲突保留、运行中“下次启动生效”及提示词撤销。

真实端点验证：

- 通过最终 UI 使用的 ProfileAiSettingsService 对 https://ai98pro.xyz/models 发起真实请求：HTTP 200，模型数组非空，请求地址精确无 /v1。
- 对 https://ai98pro.xyz/responses 发送最小 Responses 请求：HTTP 200。
- 运行 `tools/Verify-AiProvider.ps1`，由本机真实 Codex CLI（0.144.0）加载同构 provider 配置；本地模拟服务先响应模型发现，再捕获到 `POST /responses HTTP/1.1`，并确认专用环境变量中的探针 Key 进入 Bearer 请求头。脚本运行后删除自身临时目录，结果可独立回放。
- 测试 Key 只作为进程环境/内存输入使用，未写入源码、截图、日志、发布物或本报告。

真实桌面入口已观察最终发布线的主窗口与 AI 配置窗口：主操作保持首屏唯一填充按钮；主窗口仅显示 AI 摘要；默认地址精确；普通 TextBox 完整显示无效示例 Key；API/系统提示词双 Tab 与固定页脚可达；连接测试为次按钮，保存为唯一主按钮。已检查默认 680×600 对话框与当前桌面可用区域下的缩放表现。150%/200% DPI、高对比度和所有声明尺寸未在本轮逐一重新持久化截图，因此不从当前观察外推为全覆盖；原 v1.0 响应式截图只证明未改变的主框架基线。

本报告记录 `CodexProfileLauncher.exe` 最终发布件在真实 Windows 桌面入口上的构建、隔离、并行运行、恢复、关闭和 UI 验收证据。验证日期为 2026-07-15（Asia/Taipei）。

## 最终发布件

| 项目 | 结果 |
| --- | --- |
| 文件 | `artifacts/publish/win-x64/CodexProfileLauncher.exe` |
| 大小 | 71,484,285 bytes（68.17 MiB） |
| SHA-256 | `2D424B8AA10BD3D9B06B66E0C88F3A055E335EC51D5FA5C844A8C5C7B7F05646` |
| Authenticode | `NotSigned` |
| 产品版本 | `1.0.0`（文件版本 `1.0.0.0`） |
| 目标运行时 | `win-x64`，self-contained 单文件 |
| .NET SDK | `10.0.301` |
| 已安装 Codex Store 包 | `OpenAI.Codex 26.707.9981.0` |
| 实测 Codex CLI | `codex-cli 0.144.2` |

`artifacts/publish/win-x64` 最终只包含上述一个 EXE；完整机器可读记录见 `artifacts/release-metadata.json`。该哈希是本报告唯一有效的最终发布哈希，旧构建哈希不得用于核对。

## 自动化门禁

最终发布脚本 `tools/Verify-Release.ps1` 在发布前完成锁定还原、Release 构建、测试、单文件发布、目录结构和元数据检查，结果为 0 warning、0 error：

| 测试集 | 通过 | 失败 | 结果文件 |
| --- | ---: | ---: | --- |
| Core | 69 / 69 | 0 | `artifacts/test-results/core-release.trx` |
| Windows | 47 / 47 | 0 | `artifacts/test-results/windows-release.trx` |
| 合计 | 116 / 116 | 0 | — |

Windows 集成测试覆盖 Job/broker 身份、进程创建与恢复边界；Core 测试覆盖配置隔离、项目配置扫描、凭据环境变量移除、持久化和状态机等逻辑。测试没有通过弱化断言或 mock 真实 UI 来替代下面的实机验证。

## 实机环境

- Windows x64 build `26200.8655`，注册表 `DisplayVersion=25H2`。
- Microsoft Store Codex 包 `26.707.9981.0`。
- 最终 EXE 时间戳 2026-07-15 04:11:55；普通 Windows 桌面入口的成功生命周期从 04:13:16 开始。
- 测试同时保留一个与本启动器无关的既有 Codex 根进程 PID `23120`，用于反证关闭操作不会误杀其他 Codex。

## 严格隔离与失败显性化

### WSL 后端在创建 root 前被阻止

隔离核心在最终发布前的同一代码线上完成实机探针；此后唯一源代码变更是危险按钮的 XAML 视觉样式，最终发布件又重新通过全部 116 项测试。旧环境最初缺少 `desktop.runCodexInWindowsSubsystemForLinux = false`：点击启动后，程序显示自定义“配置未通过隔离检查”对话框并记录 `PROFILE_ISOLATION_AUDIT_FAILED`，且没有创建 Codex root。随后通过内置配置编辑器补入精确布尔值 `false`，程序记录 `CONFIG_SAVED` 并显示“配置已保存 / 此环境仍满足严格隔离要求”。最终两个环境的 `config.toml` 均且仅有一条精确 `false`。

对当前安装版本做了两组独立契约探针：

1. 原生 Windows `codex app-server` 在受信任项目配置中故意加入四个恶意覆盖值后，最终解析结果仍由 profile 的 `managed_config.toml` 锁定为文件型凭据、当前 `codex-home` SQLite 和当前 `codex-home/log`。
2. 同版本 WSL 路径不会加载 Windows profile 内的托管配置，并会使用 Linux 用户共享的 `$HOME/.codex/sqlite`。因此程序不允许先启动 Linux app-server 再事后判错，而是要求用户配置显式为 `false` 并在 root 创建前 fail-closed。

工作目录的 `config.toml` 与向上每一级 `.codex/config.toml` 也会在启动前严格解析；隔离关键项覆盖、WSL `true`/非布尔值或畸形 TOML 都会阻止启动。

### 凭据与状态路径

启动 root 前显式移除父进程的 `OPENAI_API_KEY`、`CODEX_API_KEY` 和 `CODEX_ACCESS_TOKEN`，并覆盖 `CODEX_HOME`、`CODEX_SQLITE_HOME` 为当前环境目录。`managed_config.toml` 还锁定：

- `cli_auth_credentials_store = "file"`
- `mcp_oauth_credentials_store = "file"`
- `sqlite_home = <当前环境>/codex-home`
- `log_dir = <当前环境>/codex-home/log`

托管路径会拒绝 reparse point/junction；用户配置和项目配置无法静默把上述状态导向环境外。

## 双环境并行运行

通过最终 EXE 同时启动两个既有环境：

| 环境 | Profile ID | Broker PID | Root PID | 独立根目录 |
| --- | --- | ---: | ---: | --- |
| 日常开发 | `ebcceb82-26e7-4571-9ea9-f8e92a283fc4` | 33572 | 29884 | `C:\Users\Administrator\AppData\Local\CodexProfileLauncher\profiles\ebcceb8226e745719ea9f8e92a283fc4` |
| 客户项目 A | `8801f830-bb27-455e-b842-8ed2c353a065` | 3872 | 28176 | `C:\Users\Administrator\AppData\Local\CodexProfileLauncher\profiles\8801f830bb27455eb8428ed2c353a065` |

捕获的两个 root 命令行分别精确包含：

```text
ChatGPT.exe --user-data-dir=C:\Users\Administrator\AppData\Local\CodexProfileLauncher\profiles\ebcceb8226e745719ea9f8e92a283fc4\app-data --new-window
ChatGPT.exe --user-data-dir=C:\Users\Administrator\AppData\Local\CodexProfileLauncher\profiles\8801f830bb27455eb8428ed2c353a065\app-data --new-window
```

两份持久化 receipt 均为 `isIsolationVerified=true`；客户项目 A 与日常开发分别在 04:13:47、04:14:08 记录 `LAUNCH_VERIFIED`。事件明细给出了各自不同的 `codexHomePath` 与 `appDataPath`，启动器同时核验真实 argv、窗口、Codex 两层写入和 app-server，结果不是仅创建空目录或 mock 成功。

## 启动器退出与恢复

1. 两个环境均运行时关闭启动器 PID `30884`。
2. 两个 broker 与两个 root 继续存活，既有无关 Codex PID `23120` 也继续存活。
3. 重新打开最终 EXE（PID `36072`），程序按持久化 receipt、精确 broker/root generation 和 Job membership 恢复两个环境为“运行中”。
4. 截图 `artifacts/screenshots/running-recovered.png` 记录了恢复后的双环境状态。

这验证了“关闭管理器不关闭已启动 Codex”，也验证了恢复不是仅凭 PID 猜测。

## 精确关闭与不误杀

### 第一个环境

- 先显示普通关闭确认，向已核验窗口请求正常退出。
- 20 秒后同一 pinned Job 仍有 8 个成员，记录 `JOB_STABLE_EMPTY_TIMEOUT`，再显示“强制关闭 Codex”并明确列出成员数量。
- 用户确认后通过同一已核验 Job generation 调用 `TerminateJobObject`，04:22:42 记录 `JOB_STOP_FORCED_VERIFIED`。
- PID `33572` / `29884` 及该 Job 成员全部消失；第二个环境 PID `3872` / `28176` 与无关 PID `23120` 仍存活。

### 第二个环境

- 相同步骤中正常关闭超时后仍有 9 个成员。
- 用户二次确认后，04:26:31 记录第二条 `JOB_STOP_FORCED_VERIFIED`。
- PID `3872` / `28176` 及该 Job 成员全部消失；无关 PID `23120` 仍存活。

最终 `profiles.json` 中两个 `activeInstance` 都为 `null`，四个受管 generation PID 均不存在，同一最终 EXE 的 launcher/broker 进程数为 0。关闭测试后再次实时查询，PID `23120` 仍为已安装包的 `ChatGPT.exe`。

主 Codex 数据在验证前后哈希保持不变：

| 文件 | SHA-256 |
| --- | --- |
| `C:\Users\Administrator\.codex\config.toml` | `A1B886C975E6CB12841AED14998600EE651D1CD05C3BACAF5D2EDD4AE38282B6` |
| `C:\Users\Administrator\.codex\auth.json` | `C0686A95F0B92B8925269DCF3CEB6AD7A4C361FE578E2B45F45D0D7B256BCCEE` |

## 日志审计

权威日志为 `%LOCALAPPDATA%\CodexProfileLauncher\logs\launcher-20260715.jsonl`。成功桌面生命周期按 `timestamp >= 2026-07-15T04:13:00+08:00` 统计：

| Event ID | 数量 |
| --- | ---: |
| `APP_INITIALIZED` | 2 |
| `LAUNCH_VERIFIED` | 2 |
| `JOB_STABLE_EMPTY_TIMEOUT` | 2 |
| `JOB_STOP_FORCED_VERIFIED` | 2 |

这 8 条记录中 `Information=4`、`Warning=4`、`Error=0`、事件 ID 含 `UNHANDLED` 的记录为 0。

最终 EXE 还真实命中一次预期的 fail-closed 分支：04:12:39 从受外层自动化 Job 约束的宿主启动时，broker 无法完整 breakaway，程序记录 `LAUNCH_FAILED` / `JOB_BROKER_BREAKAWAY_INCOMPLETE`，精确终止该 broker 且未创建 Codex root。随后通过普通 Windows Explorer 桌面入口启动，同一最终 EXE 完成上表的成功生命周期。该错误不混入成功窗口，也不被隐藏。

## UI 与响应式验收

| 截图 | 尺寸 | SHA-256 | 验收面 |
| --- | --- | --- | --- |
| `ready.png` | 1180×760 | `031FD3A59D148D8A1B296DCB74373B9B17277438635A4DA345DE48022BC8E950` | 就绪态与首要操作 |
| `running-recovered.png` | 1180×760 | `A4AA2BD9213AAE96B16C5FAE7D1FE42FBA08FD1E5566399EC9305B8AC1A40095` | 双环境恢复为运行中 |
| `close-confirmation.png` | 1180×760 | `01ACF695B140F6E9CB9C839E3C73958A578B40EBF0A317EBA85590EBD0B0A5EF` | 普通关闭确认 |
| `force-close-confirmation.png` | 1180×760 | `D3102FE1843DD54ABE114B19FCBD83EAF2A56E449EF03697653D1B3B3FD95E2A` | 强制关闭与成员数提示 |
| `transition-window.png` | 1040×686 | `D0BC202D8D775045DFF09DD39F3709E75AD4EB2B9DAEA3532C3B1AF5BCDD50D9` | 中间宽度布局 |
| `awkward-window.png` | 960×660 | `B12CF1C07703660F40D66BFECDF8EB704E01C02EBDAF38316AF8EF84DA01CB39` | 窄窗口路径截断 |
| `minimum-window.png` | 900×620 | `6CE8F8C858DE895801055D1A45FFEB9EA1F7F963AE401C39928EBEFC114D82A5` | 声明的最小窗口 |

最小窗口仍完整保留环境切换、编辑入口、状态说明和“启动 Codex”主操作；长路径使用省略号，不产生水平溢出。确认弹窗使用明确的中文动作标签，不依赖含糊的系统 Yes/No。

## 已知边界与残余风险

- 发布件未做 Authenticode 签名；Windows 可能显示未知发布者或 SmartScreen，必须先核对本报告 SHA-256。
- `--user-data-dir` 已在当前 Codex 桌面版本真实验证，但它不是 OpenAI 公共配置参考中的稳定配置项；程序因此每次启动都重新核验 argv、窗口、两层写入和 app-server。若版本变化导致该参数或任一隔离证据失效，启动会显式失败，不会静默成功。
- 若启动器由不允许 Windows Job breakaway 的宿主启动，broker 会先尝试直接 breakaway，再尝试同会话 `explorer.exe` 父进程；若 explorer 自身也属于 Job，则通过 `System.Management` 调用本机 WMI `Win32_Process.Create`。每条成功路径都要求反查 broker 不属于任何 Job；三条均失败才以 `JOB_BROKER_BREAKAWAY_INCOMPLETE` 显式拒绝，并在 details 中分别保留失败原因。
- 仅支持原生 Windows 后端。WSL 已在创建 root 前硬阻止，不声称支持 Linux/WSL 状态隔离。
- 项目配置扫描故意保守；较高层或不受信任目录中出现隔离关键项时可能产生“宁可阻止启动”的误拒绝。
- 已有仓库、原子写入与 launch-intent 回滚测试，但尚无一个专门 fault-inject ViewModel/Repository 的端到端测试来模拟“磁盘已提交后立刻抛异常”。真实双环境生命周期和恢复已通过，仍保留此测试缺口记录。
- 本报告覆盖当前主机、当前 Store 包版本、当前 Windows session 和一次完整双环境生命周期；未声称覆盖所有未来 Codex/Windows 版本、重启后的跨 boot 恢复或企业级 SSO/Windows 凭据管理器隔离。

## 参考契约

- OpenAI Codex 配置参考：<https://learn.chatgpt.com/docs/config-file/config-reference>
- OpenAI 非交互认证：<https://learn.chatgpt.com/docs/non-interactive-mode#use-api-key-auth>
- Microsoft `CreateProcessW`：<https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw>
- Microsoft `AssignProcessToJobObject`：<https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject>
- Microsoft Job Objects：<https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects>
