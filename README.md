# FileLock

A simple Windows desktop app that **locks** (encrypts) and **unlocks** (decrypts)
files with a password. Set one password, then **drop files onto a desktop shortcut** —
each file is locked or unlocked **in place**, keeping its name. Built for non-technical
users — no crypto jargon in the UI.

> ⚠️ **If you forget your password, locked files cannot be recovered. There is no backdoor.**

## How it works

- You set **one default password** once (it's stored encrypted in the app's own folder).
- **Drop files onto a shortcut to `FileLock.exe`.** Each file **toggles**: a normal file is
  **locked**, an already-locked file is **unlocked**. Lock state is detected by reading the
  file's header — **the name and extension never change**.
- Files are replaced **in place**. Before locking, a copy of the original is saved to a
  `.backup` folder next to the app, and a line is written to `.backup/ledger.jsonl` recording
  the original name, source path, date, and who locked it. (Backup-folder name clashes are
  avoided by appending a random token to the backup file; the real name lives in the ledger.)
- A locked file's header also stores the **user name of whoever locked it** (tamper-evident),
  so you can tell who locked a file later.
- Results of a shortcut drop pop up as a **tray notification**. Running the app with no file
  (just double-clicking it) opens a small window to set the password or open the backup folder.

## Cryptography

Uses only the .NET BCL (`System.Security.Cryptography`) — no third-party crypto.

| Step            | Algorithm                                             |
|-----------------|-------------------------------------------------------|
| Key derivation  | PBKDF2-HMAC-SHA256, 600,000 iterations, 16-byte salt  |
| Encryption      | AES-256-GCM, 12-byte nonce, 16-byte auth tag          |
| Integrity       | The GCM auth tag authenticates the ciphertext and the whole header (version, salt, nonce, date, locker name) as AAD |

A wrong password and a tampered file are cryptographically indistinguishable: both fail
the GCM tag check and are rejected with a friendly message. No partial/garbage output is
ever written — the result is written to a temp file and only atomically moved into place
once fully written and verified, and the original is backed up before any lock.

The PBKDF2 iteration count is stored in each file's header, so a future version can raise
it without breaking files locked today. See `FileFormat.cs` for the exact on-disk layout.

> The default password is stored in `settings.json` in the app folder, encrypted with
> Windows **DPAPI** (current-user scope). The app folder is treated as trusted — the
> password's job is to protect files **in transit** (USB, email, network), not at rest on
> your own machine.

### Limits (v1)

- Max file size: **500 MB** (the file is processed in memory).
- Individual files only — dropping several files at once works; folders are not supported.

## Tech stack

- **.NET 11**, WPF, C# (latest language version).
- Crypto hot paths avoid heap churn: small secrets (salt/nonce/tag/key) and the variable
  header use `stackalloc`, large plaintext/ciphertext buffers come from `ArrayPool<byte>`
  (and are zeroed before return), the file is read straight into the payload buffer via
  `RandomAccess`, and the magic bytes are a `"FLK1"u8` UTF-8 literal.
- The WPF UI opts into the modern **Fluent theme** (`ThemeMode`); a `NotifyIcon` tray
  balloon reports shortcut-drop results (the one reason WinForms is enabled).
- Repo-wide build settings live in `Directory.Build.props`; NuGet versions are managed
  centrally in `Directory.Packages.props` (Central Package Management); the SDK is pinned
  in `global.json`.

## Project layout

```
FileLock.sln
global.json               # pins the .NET SDK version
Directory.Build.props     # shared MSBuild settings for all projects
Directory.Packages.props  # central NuGet package versions
src/
  FileLock.Core/          # UI-agnostic core: crypto, toggle service, backup + ledger (BCL only)
  FileLock.UI/            # WPF app: shortcut/CLI handling, settings window, tray balloon
tests/
  FileLock.Core.Tests/    # xUnit tests for the crypto core
```

## Build & test

Requires the **.NET 11 SDK** (currently a preview release; `global.json` pins the exact
version). The Windows Desktop runtime is needed to run the WPF app.

```powershell
dotnet build FileLock.sln
dotnet test  FileLock.sln
```

If `dotnet` on your PATH is an older SDK, it will report that the version in `global.json`
is required — install the .NET 11 SDK, or point at an existing install, e.g.:

```powershell
& "C:\path\to\dotnet11\dotnet.exe" build FileLock.sln
```

## Run

```powershell
dotnet run --project src/FileLock.UI/FileLock.UI.csproj
```

Or launch the built executable directly:

```
src/FileLock.UI/bin/Debug/net11.0-windows/FileLock.exe
```

## Usage

**One-time setup**

1. Launch FileLock (double-click `FileLock.exe`, no file).
2. Set and confirm your password, then **Save password**.
3. Create a shortcut to `FileLock.exe` (right-click → *Create shortcut*) and put it on the
   Desktop. (Optional: drop the shortcut into `shell:sendto` to get a "Send To → FileLock"
   entry.)

**Everyday use**

- **Drag one or more files onto the shortcut.** Each file toggles in place — normal files
  are **locked**, already-locked files are **unlocked** — keeping its original name.
- A tray notification confirms what happened (e.g. *🔒 Locked report.pdf*).
- Originals are copied to the `.backup` folder next to the app before locking; open it any
  time from the app window via **Open backup folder**.

> The app folder must be **writable** (so it can store settings and backups). If you install
> under `Program Files`, run it from a writable location instead, or it will report an error.

Plain-language end-user instructions in Greek: [ΟΔΗΓΙΕΣ.pdf](ΟΔΗΓΙΕΣ.pdf).
