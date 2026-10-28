# GitPersona

**Use the right Git account every time — on Windows, macOS and Linux.**

GitPersona manages several Git accounts (GitHub, GitLab, self-hosted GitLab, any HTTPS Git host) and controls which one Git uses, both automatically (by rules) and with one click from the tray. It uses Git's own mechanisms — `includeIf`, `credential.<url>.username` and your credential helper — and never reinvents them. **No SSH anywhere; everything is HTTPS.**

> Screenshots: _(placeholder — add `docs/screenshots/accounts.png`, `rules.png`, `checker.png`)_

## The problem it solves

You have a personal GitHub account and a company GitLab account (or two accounts on the same host). Out of the box:

1. Commits in company repos silently use your personal email, or the other way round.
2. Git's credential cache returns **one login per host**, so with two accounts on one host pushes fail or go to the wrong account.
3. Tokens expire (GitLab PATs especially) and you find out when a push fails.

Tools that only swap global `user.name`/`user.email` don't touch the login, and fail silently when you forget to click. GitPersona's job is to **never pick the wrong account**: rules apply automatically, and a built-in checker shows (and flags a mismatch) exactly what Git will use in any folder.

## Download

Get the latest build from the [Releases](../../releases) page.

| Platform | Download | Notes |
|---|---|---|
| Windows x64 | `GitPersona-Setup.exe` | Installer. Per-user, **no admin rights**. Start Menu shortcut, appears in *Installed apps*, optional auto-update. |
| Windows x64 | `GitPersona.exe` | Portable. Runs from anywhere (Downloads, a USB stick). No .NET needed. No auto-update. |
| Windows ARM64 | `GitPersona-arm64.exe` | Portable. |
| macOS | `GitPersona-osx-*-Portable.zip` / `.pkg` | Apple Silicon (`osx-arm64`) and Intel (`osx-x64`). Unsigned — see below. |
| Linux x64 | `GitPersona-linux-x64.tar.gz` / AppImage | Needs a desktop with tray (StatusNotifier/AppIndicator) support for the tray icon. |

The only thing you must have installed is **Git** (Git for Windows on Windows). GitPersona detects it and tells you if it's missing.

### Windows SmartScreen warning

v1 ships **unsigned**. When you run a downloaded `.exe`, Windows SmartScreen may show **"Windows protected your PC"**. This happens because the file has no code-signing certificate and not enough download reputation yet — it does not mean the file was found to be malicious.

To run it anyway: click **More info → Run anyway**.

Removing the warning needs a paid code-signing certificate. Until then you can verify what you downloaded:

- The full source is in this repository, and every release is built by the public workflow in `.github/workflows/release.yml`.
- Each release includes **`SHA256SUMS.txt`**. Check your download:
  - Windows: `Get-FileHash .\GitPersona.exe -Algorithm SHA256` and compare with the line in `SHA256SUMS.txt`.
  - macOS/Linux: `sha256sum -c --ignore-missing SHA256SUMS.txt`

Signing is a pluggable, secret-driven CI step: set the repository secret `GITPERSONA_SIGN_PARAMS` and the Windows installer is signed with no other change. For free signing of open-source projects, the [SignPath Foundation](https://signpath.org/) program is worth investigating.

### macOS Gatekeeper

The macOS build is not signed or notarised either. If macOS says the app can't be opened, right-click it → **Open**, or run `xattr -dr com.apple.quarantine /Applications/GitPersona.app`.

## Quick start

1. **Start GitPersona.** The first-run screen checks Git, checks that Git has a credential helper (it offers to set one — it never changes anything without asking), and offers to connect GitPersona to your global Git config (it backs up your config first).
2. **Add an account** (everything is in the app; no files to edit). Pick the service:
   - **GitHub:** press **Sign in with GitHub**. Git Credential Manager (bundled with Git for Windows) opens GitHub's own sign-in in your browser; GitPersona never sees your password. When you're back, it fills in your username and name from GitHub. A second GitHub account works the same way: type its username first, then sign in.
   - **GitLab, self-hosted GitLab, GitHub Enterprise, or another HTTPS Git host:** enter your server address (for self-hosted), paste a personal access token and press *Check token*. A **Create a token in your browser** link opens the right page with the scopes pre-selected. GitPersona asks the server who the token belongs to, fills in username, name and email where available, shows the scopes and expiry, and warns if a required scope is missing. You can also use a token for GitHub via *Use a personal access token instead*.

   Browser sign-in needs Git Credential Manager as your credential helper. If it isn't, the app says so and offers the token route.
3. **Add rules** (optional): a folder (`C:\work`) or a remote (`https://gitlab.company.com/mycompany/**`) maps to an account. Anything matching no rule uses your **default account**, which you switch from the tray.
4. **Check any folder** with *Check a folder* to see which name, email and login Git will use there, and which file decided each.
5. **Clone with an account** instead of plain `git clone`, so the first sign-in goes to the right account.

### Tokens you need

- **GitHub:** classic token with `repo`, or a fine-grained token with *Contents: read and write* on the repositories you use.
- **GitLab:** token with `read_repository` and `write_repository` (or `api`). Self-hosted GitLab works; enter your server's address (`https://gitlab.company.com`).

## How it works

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

- All fragment writes go through `git config --file`, so Git does the escaping. The marker block is the only hand-written text, and it is idempotent: re-running never duplicates it, and it moves itself back to the end if you add settings below it.
- Before the **first** change your global config is copied to `.gitconfig.gitpersona-backup-<timestamp>`.
- The default account comes first and rules after it, so **rules override the default**.

### Rules: the last match wins

Rules are Git `includeIf` blocks. When several match one repository Git applies them in file order and **the last one wins**. The Rules page is an ordered list you can reorder, and says so. Put broad rules first and specific ones below.

- **Folder rule:** `includeIf "gitdir/i:C:/work/"`. Windows paths use forward slashes and end in `/`. `gitdir/i` (case-insensitive) on Windows and macOS, `gitdir` on Linux.
- **Remote rule:** `includeIf "hasconfig:remote.*.url:https://gitlab.company.com/mycompany/**"`, which needs **Git 2.36+**. Older Git: the option is disabled with a clear message. Remote rules also match URLs that contain a login (`https://me@gitlab.company.com/...`).

### Several accounts on one host

`credential.<url>.username` makes the credential helper look up the stored login **for that username** on that host, which is what lets two accounts share `gitlab.com`. Tokens are saved with `git credential approve` and removed with `git credential reject`; the app never stores them.

## Security

- Tokens are **never written to disk by GitPersona, never logged, never shown after entry** (password box). "Test login" only reports whether a login exists.
- Tokens travel on **stdin only** — never in a URL or a command-line argument. Values are validated so a newline cannot inject extra credential-protocol lines.
- Only `https://` hosts are accepted. Anything you type (paths, hosts, usernames) is treated as data and validated or escaped.
- **No telemetry.** The only network requests are token validation to the account's own host, and — in the installed build, if you leave it on — a once-a-day check of GitHub Releases for a new version. Updates are applied only when you click. The portable build never updates itself.
- Certificate validation is **never disabled**. For a self-hosted server with an internal CA, install the CA in your OS trust store; if validation fails you can still save the account without checking the token. Redirects are not followed when validating, so a token can't be forwarded to another host.

## Platform behaviour

| | Windows | macOS | Linux |
|---|---|---|---|
| Start at login (off by default) | `HKCU\...\Run` value `GitPersona` = `"<exe>" --minimized` | `~/Library/LaunchAgents/io.github.gitpersona.plist` | `~/.config/autostart/gitpersona.desktop` |
| Credential helper suggested | `manager` (Git Credential Manager) | `osxkeychain` | `libsecret` or GCM, if installed |
| Single instance | one running instance; launching again shows its window (lock file + named pipe, all OSes) | same | same |

- The login entry is per-user — no admin rights, no Task Scheduler, no service. If a portable exe is moved, the entry is repointed the next time you run it; if the exe is deleted, the OS just skips the entry.
- `--minimized` starts straight to the tray with no window.
- Closing the window hides it to the tray; **Quit** in the tray menu really exits. You are told once.

## Uninstalling

Use **Settings → Remove GitPersona from Git** (it removes the managed block, and optionally restores the backup, removes the logins GitPersona stored, and deletes its data). It also turns off start-at-login.

- **Windows installer:** uninstalling from *Installed apps* removes the managed block and the startup entry automatically.
- **Windows portable / macOS / Linux:** run *Remove GitPersona from Git* first, then delete the app. (Automatic cleanup on uninstall is only possible with the Windows installer.)
- Restoring the backup puts your pre-GitPersona config back exactly and discards any edits you made to the file since. Plain removal leaves everything else in the file untouched.

## Limits (please read)

- Rules use Git's `includeIf`; **`hasconfig:remote` needs Git 2.36+**.
- **Folder rules don't apply to the initial `git clone`** (the repository doesn't exist yet). Use *Clone with an account* or put the username in the URL yourself.
- If an **IDE or another tool injects its own credentials** (for example a built-in GitHub sign-in), that can bypass the credential helper and GitPersona.
- GitPersona manages **HTTPS only**. SSH remotes are ignored and flagged in the checker.
- A default account only covers the hosts it has a login for; a rule's account only supplies its own host's login. Use the checker when a repo talks to a host the matching account doesn't cover.
- A `user.email`, `user.name` or credential username that **you** set after GitPersona's block in `~/.gitconfig`, or inside a repo's own `.git/config`, overrides the app. GitPersona warns about the former and the checker shows the latter.
- Linux: the tray needs StatusNotifier/AppIndicator support (on GNOME install the AppIndicator extension). The window works without it.
- The macOS and Linux packages are built by CI and are not code-signed. See _Project status_ below for what has been verified.

## Build from source

Requires the .NET 10 SDK and Git.

```bash
dotnet test                                      # run the tests
dotnet run --project src/GitPersona.App          # run the app

# One self-contained file, no .NET needed to run it:
dotnet publish src/GitPersona.App -c Release -r win-x64
```

`./build.ps1 -Version 1.0.0` runs the tests and writes `GitPersona.exe`, `GitPersona-arm64.exe`, `GitPersona-Setup.exe` (Velopack) and `SHA256SUMS.txt` into `artifacts/`. On macOS/Linux use `bash build.sh <version> [runtime]`.

The exe is tens of MB because it bundles the .NET runtime. Trimming is deliberately off: it breaks Avalonia's reflection and bindings.

### Releasing

Push a tag: `git tag v1.0.0 && git push origin v1.0.0`. The workflow runs the tests on all three OSes, builds every package, writes `SHA256SUMS.txt` and attaches everything to a GitHub Release. The tag is the version shown in the app.

For the update check to work, set `RepositoryUrl` in `src/GitPersona.App/GitPersona.App.csproj` to your GitHub repository (it ships as a placeholder, and update checks stay off until you do).

## Project layout

```
src/GitPersona.Core/           accounts, config writing, git runner, rules, checker, provider clients — no UI
src/GitPersona.App/            Avalonia UI (MVVM, compiled bindings) + tray
tests/GitPersona.Core.Tests/   xUnit
tools/IconGen/                 regenerates the app icon (png / ico / icns)
```

Tests never touch your real Git config or credentials: they set `GIT_CONFIG_GLOBAL` to a temp file, `GIT_CONFIG_NOSYSTEM=1`, use a temp app-data folder, and use `credential.helper = store --file=<temp>`. Rule ordering is verified with real `git config` lookups in temp repositories; HTTP is mocked.

## Project status

What has been verified, and how:

- **Core logic** (accounts, config writing, rules, checker, provider validation, clone/fix-remote, startup entries, single instance): automated tests, run against real `git` in sandboxed temp config.
- **Windows app:** the published single-file `GitPersona.exe` was launched with .NET removed from `PATH`; the UI was driven end to end (add account → set default → connect to Git config), and `--minimized` and the single-instance wake-up were checked.
- **Not yet verified:** running the Windows *installer* and its uninstall hook on a clean machine; the tray menu on any OS beyond the app starting without error; real token validation against live GitHub/GitLab servers (covered with mocked HTTP only); and the **macOS and Linux apps, which have been compiled and published but not run** on real machines. Their OS-specific file formats (LaunchAgent, autostart) are unit-tested, and CI runs the whole suite on Windows, macOS and Linux. Please report issues.

## License

MIT — see [LICENSE](LICENSE).
