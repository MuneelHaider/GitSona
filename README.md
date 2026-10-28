<div align="center">

<img src="src/GitPersona.App/Assets/app.png" alt="GitPersona logo" width="96" height="96">

# GitPersona

**Use the right Git account, every time.**<br>
Switch between GitHub, GitLab and other Git accounts automatically. Built for Windows (tested); macOS and Linux support is experimental and untested.

[![Latest release](https://img.shields.io/github/v/release/MuneelHaider/GIT?label=release)](https://github.com/MuneelHaider/GIT/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Tested on Windows](https://img.shields.io/badge/tested%20on-Windows-brightgreen)
![macOS and Linux untested](https://img.shields.io/badge/macOS%20%26%20Linux-untested-orange)

[**Install**](#how-to-install) · [Features](#features) · [Quick start](#quick-start) · [FAQ](#faq) · [Contributing](#contributing)

</div>

> [!WARNING]
> **GitPersona has only been tested on Windows.** The macOS and Linux versions are *not tested* and there are no ready-made downloads for them yet. The source code is cross-platform and you can try building it yourself (see [Build from source](#build-from-source)), but expect rough edges. Bug reports from Mac and Linux users are very welcome.

<!-- TODO: add screenshots, e.g. docs/screenshots/accounts.png, rules.png, checker.png -->

---

## How to Install

**You only need one thing first: [Git](https://git-scm.com/downloads).** (On Windows, install *Git for Windows*.) GitPersona tells you if Git is missing. You do **not** need to install .NET — it is built in.

### Windows (recommended: installer) — tested ✅

1. Install [Git for Windows](https://git-scm.com/download/win) if you don't have it.
2. Go to the [**Releases** page](https://github.com/MuneelHaider/GIT/releases/latest) and download **`GitPersona-Setup.exe`**.
3. Run it. No admin rights needed — it installs for your user only and adds a Start Menu shortcut.
4. If Windows shows **"Windows protected your PC"**, click **More info → Run anyway** ([why?](#windows-smartscreen-warning)).

Prefer not to install anything? Download **`GitPersona-portable.exe`** (Windows x64) or **`GitPersona-arm64.exe`** (Windows on ARM) instead. These are portable: run them from anywhere, including a USB stick. Portable builds don't auto-update.

### macOS and Linux — not tested ⚠️

**GitPersona has only been tested on Windows.** There are no downloads for macOS or Linux yet, because the author has no way to test them. The code is written to be cross-platform (it is built with [Avalonia](https://avaloniaui.net/), and the automated tests are set up to run on all three systems), so you are welcome to try building it yourself:

1. Install Git and the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Get the code and run it:
   ```bash
   git clone https://github.com/MuneelHaider/GIT.git
   cd GIT
   dotnet run --project src/GitPersona.App
   ```
3. On Linux, the tray icon needs a desktop with StatusNotifier/AppIndicator support (on GNOME, install the *AppIndicator* extension).

It might work perfectly, or it might not. If you try it, please [tell us how it went](https://github.com/MuneelHaider/GIT/issues/new) — whether it works or not. That is the quickest way to get real macOS and Linux downloads.

### Verify your download (optional)

GitHub shows a **SHA-256 checksum** next to each file on the release page. To check a file you downloaded, run this in PowerShell and compare the result:

```powershell
Get-FileHash .\GitPersona-Setup.exe -Algorithm SHA256
```

The full source code is in this repository, so you can also read exactly what the app does, or build it yourself.

### Windows SmartScreen warning

Version 1 is **not code-signed** (certificates cost money), and a new app has no download reputation yet — so Windows shows a warning. It does **not** mean the file was found to be malicious. Click **More info → Run anyway**, and compare the checksum (above) with the one on the release page if you want to be sure your download wasn't altered.

---

## Features

- **Many accounts, one app.** GitHub, GitLab, self-hosted GitLab, GitHub Enterprise or any HTTPS Git host — including **two accounts on the same host** (e.g. personal and work on `github.com`).
- **Automatic account choice with rules.** "Everything in `C:\work` uses my work account." "Everything under `https://gitlab.company.com/team/` uses my company account." Anything else uses your default account.
- **One-click switching from the tray.** Change your default account from the system tray without opening the window.
- **Folder checker.** Pick any folder and see exactly which name, email and login Git will use there — and which file decided it. Mismatches are flagged.
- **Clone with an account.** Clone a repository with the right account from the very first sign-in.
- **Easy sign-in.** For GitHub, press *Sign in with GitHub* and sign in through your browser — GitPersona never sees your password. For other hosts, paste a personal access token; GitPersona checks it, fills in your details, and shows its scopes and expiry date.
- **Token expiry warnings.** Find out a token is about to expire *before* a push fails.
- **Safe by design.** Changes to your Git config are limited to one clearly marked block, backed up first, and removable with one click. See [Security & privacy](#security--privacy).
- **No SSH, no telemetry.** Everything is HTTPS; nothing is sent anywhere except to your own Git host.
- **Windows-first.** Tested on Windows, with optional start-at-login and a single running instance. The code also targets macOS and Linux, but those are **untested**.
- **Free and open source** under the MIT license.

---

## Quick start

1. **Start GitPersona.** A short first-run check makes sure Git is installed and has a credential helper, and offers to connect GitPersona to your global Git config. It never changes anything without asking, and backs up your config first.
2. **Add an account.**
   - **GitHub:** press **Sign in with GitHub** and finish signing in in your browser. (A second GitHub account works the same way: type its username first, then sign in.)
   - **GitLab, self-hosted GitLab, or another HTTPS host:** enter the server address (if self-hosted), paste a personal access token and press **Check token**. A *Create a token in your browser* link opens the right page with the scopes pre-selected.
3. **Add rules** *(optional)*: map a folder (`C:\work`) or a remote (`https://gitlab.company.com/mycompany/**`) to an account. Anything that matches no rule uses your **default account**.
4. **Check any folder** with **Check a folder** to see which account Git will use there.
5. **Clone with an account** instead of plain `git clone`, so the first sign-in goes to the right account.

<details>
<summary><b>Which token scopes do I need?</b></summary>

- **GitHub:** a classic token with `repo`, or a fine-grained token with *Contents: read and write* on the repositories you use.
- **GitLab:** a token with `read_repository` and `write_repository` (or `api`). For self-hosted GitLab, enter your server's address, e.g. `https://gitlab.company.com`.

Browser sign-in for GitHub needs [Git Credential Manager](https://github.com/git-ecosystem/git-credential-manager) as your credential helper (it ships with Git for Windows). If it isn't, the app says so and offers the token route.
</details>

---

## Why GitPersona?

If you have a personal GitHub account and a work GitLab account (or two accounts on one host), Git out of the box has three problems:

1. Commits in work repositories silently use your personal email — or the other way round.
2. Git's credential cache keeps **one login per host**, so with two accounts on one host, pushes fail or go to the wrong account.
3. Tokens expire (GitLab ones especially) and you only find out when a push fails.

Tools that only swap your global `user.name` / `user.email` don't touch the login, and fail silently when you forget to click. GitPersona's goal is to **never pick the wrong account**: rules apply automatically, and the built-in checker shows what Git will use in any folder.

It does this with Git's own features — `includeIf`, `credential.<url>.username` and your existing credential helper — rather than inventing its own.

---

## FAQ

**Does GitPersona work with SSH?**
No. GitPersona manages HTTPS only. SSH remotes are ignored and flagged in the checker.

**Will it mess up my existing `.gitconfig`?**
No. It adds one marked block at the very end of your global Git config, makes a timestamped backup before the first change, and can remove everything cleanly (see [Uninstalling](#uninstalling)).

**Where are my tokens stored?**
In your operating system's credential helper (Git Credential Manager, Keychain, libsecret, …) — the same place Git already uses. GitPersona itself never writes tokens to disk.

**My folder rule didn't apply during `git clone`.**
Expected: the repository doesn't exist yet, so Git can't match a folder rule. Use **Clone with an account** in the app, or put your username in the URL yourself.

**Git says the wrong account anyway.**
Open **Check a folder** — it shows which file decided each value. Common causes are a `user.email` set later in `~/.gitconfig` or in the repo's own `.git/config`, or an IDE that uses its own built-in GitHub sign-in and bypasses the credential helper.

**Does it check for updates?**
Only the installed Windows version, once a day if you leave that on, and it only updates when you click. Portable builds never update themselves.

<details>
<summary><b>All known limitations</b></summary>

- `hasconfig:remote` rules (remote URL rules) need **Git 2.36 or newer**. On older Git the option is disabled with a clear message. Folder rules work on any Git.
- **Folder rules don't apply to the initial `git clone`** — see the FAQ above.
- If an **IDE or another tool injects its own credentials**, that can bypass the credential helper and therefore GitPersona.
- A default account only covers the hosts it has a login for; a rule's account only supplies its own host's login. Use the checker when a repository talks to a host the matching account doesn't cover.
- A `user.email`, `user.name` or credential username that **you** set *after* GitPersona's block in `~/.gitconfig`, or inside a repository's `.git/config`, overrides the app. GitPersona warns about the former, and the checker shows the latter.
- **macOS and Linux are untested** and have no prebuilt downloads.
</details>

---

## Security & privacy

- Tokens are **never written to disk by GitPersona, never logged, and never shown again** after you enter them.
- Tokens are passed to Git on **stdin only** — never in a URL or command-line argument. Input is validated so nothing you type can inject extra commands or credential lines.
- Only `https://` hosts are accepted.
- **No telemetry.** The only network requests are (1) token validation against *your own* Git host, and (2) in the installed Windows build, an optional once-a-day check of GitHub Releases for a new version.
- TLS certificate validation is **never disabled**. For a self-hosted server with an internal CA, install the CA in your OS trust store. Redirects aren't followed when validating a token, so a token can't be forwarded to another host.
- The tests never touch your real Git config or credentials.

Found a security problem? Please report it privately through [GitHub's security advisories](https://github.com/MuneelHaider/GIT/security/advisories/new) rather than a public issue.

---

## How it works

<details>
<summary><b>Technical details</b></summary>

GitPersona keeps its own files and adds **one marked block at the very end** of your global Git config (`~/.gitconfig`, or the XDG file if that is the one you use):

```ini
# >>> GitPersona managed — do not edit between markers >>>
[include]
    path = <appdata>/GitPersona/active.gitconfig
    path = <appdata>/GitPersona/rules.gitconfig
# <<< GitPersona managed <<<
```

| File (in the app-data folder) | Purpose |
|---|---|
| `accounts.json` | Account metadata only: id, label, host, username, name, email, auth type, token expiry date. **No secrets.** |
| `profiles/<id>.gitconfig` | One generated fragment per account: `user.name`, `user.email`, and `credential.<host>.username`. |
| `active.gitconfig` | One `include` of the default account's fragment. |
| `rules.json` / `rules.gitconfig` | Your ordered rules / the generated `includeIf` blocks. |
| `settings.json`, `state.json` | Preferences, and where your config backup is. |

App-data folder: `%APPDATA%\GitPersona` (Windows), `~/Library/Application Support/GitPersona` (macOS), `~/.config/GitPersona` (Linux). Set `GITPERSONA_DATA_DIR` to use a different one.

- All fragment writes go through `git config --file`, so Git does the escaping. The marker block is the only hand-written text; it is idempotent and moves itself back to the end if you add settings below it.
- Before the **first** change, your global config is copied to `.gitconfig.gitpersona-backup-<timestamp>`.
- The default account comes first and rules after it, so **rules override the default**.

**Rules: the last match wins.** Rules are Git `includeIf` blocks. When several match one repository, Git applies them in file order and the last one wins. The Rules page is an ordered list you can reorder — put broad rules first and specific ones below.

- *Folder rule:* `includeIf "gitdir/i:C:/work/"` (case-insensitive on Windows and macOS, `gitdir` on Linux).
- *Remote rule:* `includeIf "hasconfig:remote.*.url:https://gitlab.company.com/mycompany/**"` (Git 2.36+). Also matches URLs that contain a login, like `https://me@gitlab.company.com/...`.

**Several accounts on one host.** `credential.<url>.username` makes the credential helper look up the stored login *for that username* on that host, which is what lets two accounts share `gitlab.com`. Tokens are saved with `git credential approve` and removed with `git credential reject`.

**Platform behaviour**

| | Windows | macOS | Linux |
|---|---|---|---|
| Start at login (off by default) | `HKCU\...\Run` value `GitPersona` | `~/Library/LaunchAgents/io.github.gitpersona.plist` | `~/.config/autostart/gitpersona.desktop` |
| Credential helper suggested | `manager` (Git Credential Manager) | `osxkeychain` | `libsecret` or GCM, if installed |

Start-at-login is per-user — no admin rights, Task Scheduler or service. `--minimized` starts straight into the tray. Closing the window hides it to the tray; **Quit** in the tray menu really exits. Only one instance runs; launching again shows the existing window.
</details>

---

## Uninstalling

1. Open **Settings → Remove GitPersona from Git…**. It removes the managed block from your Git config, turns off start-at-login, and can optionally restore your original config backup, remove the logins GitPersona stored, and delete its own data.
2. Then remove the app itself:
   - **Windows installer:** uninstall from *Installed apps*. (Step 1 is done automatically.)
   - **Windows portable, macOS, Linux:** delete the app.

Restoring the backup puts your pre-GitPersona config back exactly and discards any edits you made to that file since. Plain removal leaves everything else in the file untouched.

---

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Git.

```bash
git clone https://github.com/MuneelHaider/GIT.git
cd GIT

dotnet test                                    # run the tests
dotnet run --project src/GitPersona.App        # run the app

# One self-contained file, no .NET needed to run it:
dotnet publish src/GitPersona.App -c Release -r win-x64
```

`./build.ps1 -Version 1.0.0` (Windows) runs the tests and writes `GitPersona.exe`, `GitPersona-arm64.exe`, `GitPersona-Setup.exe` and `SHA256SUMS.txt` into `artifacts/`. On macOS/Linux use `bash build.sh <version> [runtime]`.

The executable is tens of MB because it bundles the .NET runtime. Trimming is deliberately off: it breaks Avalonia's reflection and bindings.

<details>
<summary><b>Project layout</b></summary>

```
src/GitPersona.Core/           accounts, config writing, git runner, rules, checker, provider clients — no UI
src/GitPersona.App/            Avalonia UI (MVVM, compiled bindings) + tray
tests/GitPersona.Core.Tests/   xUnit tests
tools/IconGen/                 regenerates the app icon (png / ico / icns)
```

Tests never touch your real Git config or credentials: they set `GIT_CONFIG_GLOBAL` to a temp file, `GIT_CONFIG_NOSYSTEM=1`, use a temp app-data folder and a temp credential store. Rule ordering is verified with real `git config` lookups in temp repositories; HTTP is mocked.
</details>

<details>
<summary><b>Making a release (maintainers)</b></summary>

Push a tag: `git tag v1.0.0 && git push origin v1.0.0`. The workflow runs the tests on Windows, macOS and Linux, builds every package, writes `SHA256SUMS.txt` and attaches everything to a GitHub Release. The tag is the version shown in the app.

For the in-app update check, `RepositoryUrl` in `src/GitPersona.App/GitPersona.App.csproj` must point at this GitHub repository. Update checks stay off while it is the `OWNER` placeholder.

Windows signing is a pluggable CI step: set the repository secret `GITPERSONA_SIGN_PARAMS` and the installer is signed with no other change. Free signing for open-source projects is available through the [SignPath Foundation](https://signpath.org/).
</details>

---

## Contributing

Contributions are welcome — bug reports, ideas, docs and code.

- **Found a bug or have an idea?** [Open an issue](https://github.com/MuneelHaider/GIT/issues/new). For bugs, please include your OS, your Git version (`git --version`) and what the **Check a folder** screen shows.
- **Want to change code?** Fork the repo, create a branch, make your change, run `dotnet test`, and open a pull request. The repository's CI workflow builds and tests on Windows, macOS and Linux.
- **Testing on macOS or Linux** is especially valuable right now — see below.

### Project status

- **Tested on Windows:** core logic (accounts, config writing, rules, checker, provider validation, clone, startup entries, single instance) is covered by automated tests against real `git` in a sandboxed config. The Windows app was run end to end (add account → set default → connect to Git config).
- **Not yet verified:** the Windows *installer* and uninstaller on a clean machine; the tray menu beyond the app starting; real token validation against live GitHub/GitLab servers (tested with mocked HTTP only); and the **macOS and Linux apps, which have never been run on real machines** (the author only has Windows). Reports from those platforms are very welcome.

---

## License

[MIT](LICENSE) — free to use, modify and share.
