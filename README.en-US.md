# Codex Environment Manager

A Windows x64 single-file tool used to create, verify, and launch isolated Codex desktop environments. Each environment has its own independent `CODEX_HOME` and Chromium `app-data`; creating a new environment does not copy login or session files from existing Codex data directories on disk.

Built-in **Keysmith jailbreak** and custom API proxy capabilities allow Codex to be connected to third-party compatible gateways out of the box, with default enhancements to system instructions (injected at the configuration layer; 100% upstream allowance is not guaranteed).

## Community & Proxy Station

Want to avoid pitfalls and quickly set up the "multi-environment + custom API + jailbreak" pipeline? Join the **AI98 Pro** user community to discuss configurations, models, and usage experiences:

| Entry | Information |
| --- | --- |
| **QQ Group** | `1072183582` |
| **API Proxy** | [https://ai98pro.xyz](https://ai98pro.xyz) |
| **Highlights** | OpenAI-style interface compatibility · **Jailbreak support** · One-click integration via this launcher |

**Recommended Setup (approx. 1 minute):**

1. Visit the [AI98 Pro Proxy Station](https://ai98pro.xyz) to get the Base URL and API Key.
2. In the launcher's environment page, go to the **AI** Tab and paste the address and key (Default Base is `https://ai98pro.xyz/`).
3. Click "Test Connection / Refresh Models" $\rightarrow$ select a model and reasoning intensity $\rightarrow$ Save.
4. Launch Codex; this tool enables Keysmith / system prompt replacement by default, which can be stacked with the proxy's jailbreak capabilities.

If you encounter issues, provide feedback in the group `1072183582`; it's much faster than grinding through documentation alone.

## Usage

1. Run `CodexProfileLauncher.exe`.
2. Click "New Environment," and fill in the name, environment data directory, and optional working directory.
3. Click "Launch Codex." Newly created environments will not copy existing Codex disk logins or sessions; usually, a separate login is required, though system-level SSO or Windows Credential Manager may still affect login status (see Boundaries).
4. If an instance of the same environment is already running, "Launch Codex" changes to "Open Codex," which only activates the existing instance without attempting to start another using the same data directory. Different environments can run simultaneously.
5. v1.1.6 launches Codex from a shared runtime cache of the local Store package and prioritizes `explorer.exe` of the same Windows session as the parent process. Under normal conditions, closing the launcher will not close Codex, and it no longer relies on hidden brokers, Job breakaway, or WMI. If the system blocks explorer-parent creation and enables a direct local copy fallback, the launch details will explicitly state "Independent survival not guaranteed" rather than pretending to have the same lifecycle. When the launcher is run again, the state will be restored via the persisted receipt.
6. Each new Store package version requires a one-time preparation of the shared runtime cache before the first launch, which typically occupies an additional 1.8 GiB and may take 30–60 seconds. All environments of the same version subsequently reuse this cache after integrity verification.

## Environment Page Configuration (Top Navigation)

After selecting an environment, the main area layout is: **Environment Title $\rightarrow$ Launch Panel (always visible) $\rightarrow$ Top Configuration Tabs**. The sidebar only lists environments and does not add a second set of application navigation.

| Tab | Purpose |
| --- | --- |
| **Overview** | AI / Skill summaries and quick entry points |
| **AI** | Custom API, default model, reasoning intensity, system prompt (high-frequency entry, configured directly on page) |
| **Skills** | Built-in skill library: Enable/Disable, edit `SKILL.md`, reset to built-in, import, open directory |
| **Paths** | Environment data directory, default working directory, Codex application detection |
| **Advanced** | Runtime details and `config.toml` editing |
| **Management** | Edit/migrate data directory, copy, delete, open data directory |

The main launch button remains outside the tabs to ensure it doesn't compete with configuration entries.

## Default System Prompt (Default Replacement)

Every environment **enables "Replace System Prompt" by default** (`system_prompt_enabled = true`; forced ON during save/launch; cannot be disabled via UI).

- Completely replaces built-in Codex instructions via `model_instructions_file` in `managed_config.toml`.
- **Keysmith enabled by default**: Uses `codex-home/gpt-unrestricted.md` as the active file and `./gpt-unrestricted.md` as the pointer in `config.toml`.
- When Keysmith is disabled, it falls back to **operator-core** (`assets/default-system-prompt/default-system-prompt.md`) $\rightarrow$ `codex-home/system-prompt.md`.
- The editing source is always at `.launcher/system-prompt.md`; new environments are automatically seeded.

## Keysmith Execution Mode (Default Enabled)

This launcher **natively integrates** the core mechanism of [codex-keysmith](https://github.com/Jia-Ethan/codex-keysmith) (no Python dependency, **enabled by default**):

1. Writes the built-in `gpt-unrestricted.md` to the instructions file and sets `model_instructions_file` (prioritizing `CODEX_HOME/gpt-unrestricted.md`).
2. Isolates `hooks.json` by renaming it to `hooks.json.disabled`.
3. Enabled by default upon creating a new environment or first load; re-verified and written during save/launch.

Note: Instruction injection at the configuration layer cannot guarantee that upstream models/gateways will not refuse requests.

## Plaintext Quick API and System Prompts

On the environment page, open the **AI** Tab:

- Enter any absolute http/https Base URL. The default is exactly [https://ai98pro.xyz/](https://ai98pro.xyz/) (AI98 Pro Proxy, jailbreak supported; see "Community & Proxy Station"). The launcher does not automatically append `/v1`.
- API Keys are displayed in full in a standard text box and saved as plaintext in the environment's `.launcher/ai-settings.toml`; they are not encrypted or masked. The Overview Tab only shows whether a summary has been saved.
- "Test Connection / Refresh Models" sends a **real-time** request to `<base>/models` using current unsaved inputs, parses model IDs (including non-GPT), and populates the default model dropdown. It displays the actual address, HTTP status, latency, and model count. Successful tests are not automatically saved.
- If an address and key are present when opening AI configuration, the model list is fetched in the background. The dropdown **does not** rely on local cache as the source of truth.
- Default reasoning intensity options: `minimal` / `low` / `medium` / `high` / `xhigh` / `max` / `ultra`. This is written to `model_reasoning_effort` in the managed config and fully exposed in the `supported_reasoning_levels` of `model_catalog_json`.
- When saving and enabling the API, `/models` is fetched again to generate the environment's `.launcher/model-catalog.json`. The `model`, `model_catalog_json`, and an independent provider are written to `managed_config.toml`, allowing Codex's selection list to include third-party models (not just GPT).
- When launching Codex, if the API is enabled, the catalog is fetched and overwritten again to ensure consistency with the gateway. Failure to fetch results in a "hard failure" before launch rather than pretending to succeed with an expired directory.
- You can import, replace, or directly edit `.md/.Sxt` system prompts. The product **always** completely replaces default model instructions via `model_instructions_file` (toggle forced ON).
- Changes can be saved while the environment is running; a prompt will indicate "Effective on next launch." Switching environments or closing the launcher will trigger confirmation prompts for unsaved AI / Skill / TOML changes.
- Note: Codex Desktop may still filter the display of some non-official slugs (upstream limitation); CLI/TUI `/model` and actual request slugs follow the local managed configuration.

## Environment Copy / Delete / Migration

- **Copy Environment**: Via Management Tab, right-clicking the environment list, or the "Copy Environment" command. Copies `config.toml`, API settings, and prompts; login/session/app-data are not copied.
- **Delete Environment**: Removes the launcher record and **permanently deletes the corresponding data directory** (deleted only after verifying `.codex-profile.json` identity; cannot be deleted while running).
- **Modify Data Directory & Migrate**: Edit Environment $\rightarrow$ change "Environment Data Directory" $\rightarrow$ Save. The associated Codex instance must be closed first. The launcher will migrate the entire directory to the new path and update absolute paths in the configuration (Move on the same drive, Copy then Clean on different drives). If a non-existent or empty directory is chosen, it is used directly; if a non-empty parent directory is chosen, the launcher automatically creates and previews a dedicated managed subdirectory `CodexProfile-<32-char-no-hyphen-ID>`. Environment roots with an existing `.codex-profile.json` marker are validated by exact directory; non-empty parent directories themselves will not be included in migration or recursive deletion.

## Built-in Skill Library

The `skills/builtin/` repository is distributed with the application (copied to `skills/builtin` next to the EXE during build). Each skill is a directory containing `SKILL.md`. The skill list features an independent scrollbar for easy browsing of large lists.

- **Source of Truth Path**: `codex-home/skills/<id>/` of the current environment (discovered via `CODEX_HOME` set by the launcher; does not rely on the global `~/.agents/skills` for environment isolation).
- **Enable**: Copy from built-in library to `codex-home/skills`.
- **Disable**: Move to `.launcher/skills-disabled/<id>` (can be re-enabled, preserving local modifications).
- **Edit**: Modifies the copy for the current environment; "Reset to Built-in" overwrites with the template.
- **New / Copy Environment**: Installs all built-in skills by default (skips existing directories).
- **Do Not Touch** `codex-home/skills/.system` (Codex system skills).
- If skills are modified while Codex is running, you must restart the session or the environment's Codex instance for re-indexing.

## Store Runtime Cache & Launch Mechanism

The program dynamically parses and verifies the currently installed version from the Microsoft Store package `OpenAI.Codex_2p2nqsd0c76g0`. `ChatGPT.exe` inside Store/MSIX installation directories cannot be reliably created as a standard Win32 EXE via `CreateProcessW` / `Process.Start` (reported machines may return `Win32=5 (Access Denied)`). Consequently, v1.1.6 no longer layers brokers, breakaway, or WMI fallbacks around the original package process; instead, it copies the entire `app` directory of the installed version to `%LOCALAPPDATA%\CodexProfileLauncher\runtime-cache` and launches from this local copy without MSIX package identity.

The runtime cache is shared by Store package version rather than copied per environment. Initial preparation writes to a dedicated staging directory; once copied and verified, it is published for other launch requests. Concurrent launches of the same version share the preparation result to avoid partial copies. The current package is approximately 1.8 GiB, and initial copying on standard disks typically takes 30–60 seconds. Insufficient space, source package updates, or copy/verification failures result in explicit errors. Restricted `Dictionaries/**/*.bdic` dictionary caches generated after Electron starts are preserved and reused; symlinks, non-`.bdic` content, single files exceeding 64 MiB, or a total exceeding 256 MiB will invalidate and trigger a cache rebuild. New Store versions create new cache directories; old versions may still be used by running Codex instances and are not automatically purged; users must manually clean them after closing referencing processes.

Once the cache is ready, the launcher uses `STARTUPINFOEX` to specify `explorer.exe` of the same Windows session as the parent process, passing precise argv, Unicode environment blocks, and the working directory. It does not inherit the launcher's outer Job. Electron entering its own Job after launch is normal and no longer flagged as a launch failure. Each environment uses both `--user-data-dir` and `CODEX_ELECTRON_USER_DATA_PATH` to point to its own `app-data`, and overrides `CODEX_HOME` and `CODEX_SQLITE_HOME`. The launcher verifies the root process identity, precise argv, window, two-layer data writes, and app-server; if evidence is insufficient, it retains the receipt, displays "Status pending confirmation," and prevents duplicate launches rather than silently claiming isolation success.

The local copy has no `PackageFullName`, so the built-in Codex MSIX updater may log "this process has no package identifier." In actual probing, this log does not affect the window, built-in `codex.exe`, or app-server. The source of truth for updates remains the Microsoft Store installation; the launcher creates a new cache when the version changes.

## Data Locations

Default launcher data is located at:

```text
%LOCALAPPDATA%\CodexProfileLauncher\
├─ state\profiles.json       Environment registry and process receipts
├─ logs\                     Local JSONL diagnostic logs
├─ runtime-cache\            Shared app runtime copies per Store version (~1.8 GiB/version)
└─ profiles\<UUID>\
   ├─ .codex-profile.json    Immutable environment identity
   ├─ .launcher\
   │  ├─ ai-settings.toml    API address, toggle, default model, reasoning intensity, and plaintext Key
   │  ├─ model-catalog.json  Codex model catalog generated after real-time fetch
   │  ├─ system-prompt.md    Full content of the system prompt
   │  └─ skills-disabled\    Copies of disabled skills
   ├─ codex-home\            CODEX_HOME, config, credentials, SQLite, sessions
   │  ├─ config.toml         User-editable configuration
   │  ├─ managed_config.toml Isolation key locking layer
   │  ├─ skills\             Skills enabled for this environment (including .system)
   │  └─ log\                Codex logs for the current environment
   └─ app-data\              Codex/Chromium application data
```

User `config.toml` for new environments defaults to file-based credential storage and explicitly locks the native Windows backend:

```toml
cli_auth_credentials_store = "file"
mcp_oauth_credentials_store = "file"
desktop.runCodexInWindowsSubsystemForLinux = false
```

These are the correct isolation configurations. The `PROCESS_CREATE_SUSPENDED_ACCESS_DENIED` error seen in older versions occurred during the Windows Store Codex root creation phase, before `config.toml` was read, and thus could not be solved by modifying these three items; v1.1.6 solves this by creating the process from the verified local runtime cache.

The `managed_config.toml` maintained by the launcher forces credentials storage, SQLite, and log directories to the current environment:

```toml
cli_auth_credentials_store = "file"
mcp_oauth_credentials_store = "file"
sqlite_home = "<Current Environment>/codex-home"
log_dir = "<Current Environment>/codex-home/log"
```

When Quick API is enabled, the managed layer writes the default `model`, `model_catalog_json`, and independent provider, and injects the current environment's plaintext Key into a dedicated environment variable during launch; the Key does not enter the command line or `managed_config.toml`. Every launch precisely sets `CODEX_ELECTRON_USER_DATA_PATH` and `--user-data-dir` to the current environment's `app-data`. When Quick settings are disabled, corresponding model/catalog/provider or prompt overrides are removed from the managed layer, and original advanced configurations take effect again.

Before launching, the tool scans the `config.toml` in the working directory and every `.codex/config.toml` up the directory tree. If a project-level config attempts to override the four critical isolation items, enables the WSL backend, or contains unparseable TOML, the launch is explicitly blocked. Ordinary project configurations (models, permissions, etc.) are not restricted.

Deleting an environment removes both the launcher record and the managed data directory after verifying the directory identity marker. Deletion is refused if the environment is running or the status cannot be confirmed. If the same Windows session and window identity are verified, "Close Codex" will first request a normal exit, waiting up to 20 seconds. If no closable window is found, the instance is in another session, or members persist after timeout, the program displays the member count and requests a second explicit confirmation. Any forced termination requires re-verification of the exact root generation and process tree identity from the receipt; if PID reuse, path, or generation conflicts are found, the operation is refused and the receipt is preserved rather than terminating by process name.

## Build & Test

The repository locks .NET SDK 10.0.301 and NuGet content hashes:

```powershell
dotnet restore .\CodexProfileLauncher.slnx --locked-mode
dotnet test .\CodexProfileLauncher.slnx -c Release --no-restore
dotnet publish .\src\CodexProfileLauncher\CodexProfileLauncher.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o .\artifacts\publish\win-x64
```

Full build and release gates can be run directly (refer to verification reports for actual UI and process lifecycle evidence):

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Verify-Release.ps1
```

The final distribution directory must contain a unique self-contained EXE and retain the `Assets` and `skills/builtin` directories. The gate also generates and verifies `artifacts/CodexProfileLauncher-v1.1.6-win-x64.zip`; use this full ZIP when distributing to other computers. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/VERIFICATION.md](docs/VERIFICATION.md) for full architecture and entry evidence.

## Boundaries & Uninstallation

- Target Platform: Windows 10 22H2 (build 19045) or supported Windows 11 x64; requires Microsoft Store version of Codex.
- When launching the Codex root, the launcher explicitly removes the parent process's `OPENAI_API_KEY`, `CODEX_API_KEY`, and `CODEX_ACCESS_TOKEN`, overrides `CODEX_HOME` and `CODEX_SQLITE_HOME` to the current environment's `codex-home`, and overrides `CODEX_ELECTRON_USER_DATA_PATH` to the current environment's `app-data`; other parent environment variables are inherited. This boundary does not copy existing disk logins/sessions, nor does it claim to isolate Windows Credential Manager, system-level SSO, or other non-removed environment variables.
- `--user-data-dir` is tested as functional in the current Codex desktop version but is not listed in OpenAI's public config reference. Therefore, every launch re-verifies the actual argv, window, two-layer data writes, and app-server; version changes will not silently result in a "fake success."
- WSL backend is not supported; `desktop.runCodexInWindowsSubsystemForLinux` must be explicitly and strictly `false` in the current environment's `config.toml`. Enabling WSL in the current desktop version places SQLite in a shared Linux user directory and fails to load the managed isolation config from each Windows `CODEX_HOME`. An explicit `false` takes priority over old global states and invalidates legacy switches without needing to clean them. The launcher forces this check before creating the root. Old environments missing this line will be safely blocked and can continue use after adding it.
- `CodexProfileLauncher.exe` is a self-contained single file; users do not need to install .NET Runtime. `Assets` and `skills/builtin` in the delivery package are runtime content and must maintain their original directory structure relative to the EXE. The program creates state, logs, and environment data in `%LOCALAPPDATA%\CodexProfileLauncher`.
- This deliverable is not Authenticode signed; Windows may show "Unknown Publisher" or SmartScreen warnings. Before running, verify using `Get-FileHash .\CodexProfileLauncher.exe -Algorithm SHA256` against the final SHA-256 in `artifacts\release-metadata.json`; do not use hashes from old releases.
- Normal startup in v1.1.6 no longer depends on brokers, `CREATE_BREAKAWAY_FROM_JOB`, or WMI. Even if the launcher is opened from a host constrained by an outer Job (e.g., WeChat, QQ, browser, archive software), it creates Codex from the shared local runtime cache and prioritizes `explorer.exe` of the same session as the parent. It verifies explorer session, cache source, target image, argv, environment, and working directory before launch; failures preserve the real error and will never fall back to directly executing the Store `ChatGPT.exe`. Only when explorer-parent fails before creation is the direct launch of the verified local copy allowed, with its lifecycle boundaries explicitly noted.
- The runtime cache is not a Codex copy bundled with the distribution, but is generated from the installed and verified Store package on the local machine. Each Store version requires ~1.8 GiB of additional space; new version caches are created and old versions are retained, with the current version not being automatically purged. Codex referencing an old cache must be closed before manual cleanup.
- Source paths in exception stacks come from compile-time debug metadata and do not indicate that the running machine is accessing the developer's disk. Old releases may show absolute paths from the dev machine; v1.1.6 Release continues to use `PathMap` to map physical paths to `/_/<ProjectName>/...` while retaining source filenames and line numbers for diagnostics.
- Close all managed Codex instances before uninstalling. Deleting the EXE does not automatically delete environment data or the `runtime-cache`; if a complete wipe is required, manually delete these directories after confirming that accounts, sessions, configurations, and old runtime caches are no longer needed.

Third-party licensing materials are located in [licenses](licenses).
