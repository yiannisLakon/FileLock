# FileLock

A simple Windows desktop app that **locks** (encrypts) and **unlocks** (decrypts)
files with a password. Drag a file onto the window, type a password, and the file is
locked or unlocked in place. Built for non-technical users — no crypto jargon in the UI.

> ⚠️ **If you forget your password, the file cannot be recovered. There is no backdoor.**

## How it works

- A locked file gets a `.locked` extension (e.g. `report.pdf` → `report.pdf.locked`).
  The original name is stored *inside* the encrypted payload and restored on unlock.
- Drop a normal file → it gets **locked**. Drop a `.locked` file → it gets **unlocked**.
- The output is written next to the source. **The source file is never modified or deleted.**
- If the output name already exists, a ` (1)`, ` (2)`, … suffix is added — nothing is overwritten.

## Cryptography

Uses only the .NET BCL (`System.Security.Cryptography`) — no third-party crypto.

| Step            | Algorithm                                             |
|-----------------|-------------------------------------------------------|
| Key derivation  | PBKDF2-HMAC-SHA256, 600,000 iterations, 16-byte salt  |
| Encryption      | AES-256-GCM, 12-byte nonce, 16-byte auth tag          |
| Integrity       | The GCM auth tag authenticates both ciphertext and the file header (as AAD) |

A wrong password and a tampered file are cryptographically indistinguishable: both fail
the GCM tag check and are rejected with a friendly message. No partial/garbage output is
ever written — the result is written to a temp file and only atomically moved into place
once fully written and verified.

The PBKDF2 iteration count is stored in each file's header, so a future version can raise
it without breaking files locked today. See `FileFormat.cs` for the exact on-disk layout.

### Limits (v1)

- Max file size: **500 MB** (the file is processed in memory).
- Single files only — no folders or batch locking.

## Tech stack

- **.NET 11**, WPF, C# (latest language version).
- Crypto hot paths avoid heap churn: small secrets (salt/nonce/tag/key/header) use
  `stackalloc`, large plaintext/ciphertext buffers come from `ArrayPool<byte>` (and are
  zeroed before return), the file is read straight into the payload buffer via
  `RandomAccess`, and the magic bytes are a `"FLK1"u8` UTF-8 literal.
- The WPF UI opts into the modern **Fluent theme** (`ThemeMode`).
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
  FileLock.Core/          # UI-agnostic crypto core (class library, BCL only)
  FileLock.UI/            # WPF app (drag-drop + password box)
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

1. Launch FileLock.
2. Type a password.
3. Drag a file onto the window.
   - A normal file is **locked** to `<name>.locked`.
   - A `.locked` file is **unlocked** back to its original name.
4. On success the result is revealed in File Explorer.

Plain-language end-user instructions in Greek: [ΟΔΗΓΙΕΣ.pdf](ΟΔΗΓΙΕΣ.pdf).
