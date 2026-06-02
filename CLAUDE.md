# CLAUDE.md

Guidance for AI agents working in this repository.

## What this is

**FileLock** — a Windows desktop app that locks (encrypts) and unlocks (decrypts) files
with a password, for non-technical users. **.NET 10**, WPF, C# (latest language version).

The primary workflow is **drop files onto a desktop shortcut**: Windows hands the paths to
`FileLock.exe` as command-line args, and the app toggles each file **in place** using a
single **default password** set once in the app. A file is locked if it doesn't already
carry the FileLock header and unlocked if it does — the file name/extension never changes.
Before locking, the original is copied into a `.backup` folder and recorded in a ledger.
Running the exe with **no args** opens a small settings window (set the password, open the
backup folder). Results of a shortcut drop are reported via a tray-balloon notification.

The default password is **not preset**: on the first shortcut drop with no password saved,
the user is prompted for one (it is saved, then the drop proceeds). A password can also be
passed on the command line — `FileLock.exe --password <pw> <files…>` (also `-p`,
`--password=<pw>`) — which is used for that run and saved as the default if none exists yet
(`App.ParseArgs` / `App.ResolvePassword`). Note a CLI password is visible to other processes.

## Architecture

```
FileLock.sln
global.json                # pins the .NET SDK (.NET 10)
Directory.Build.props      # shared MSBuild settings for every project
Directory.Packages.props   # Central Package Management — all NuGet versions live here
src/FileLock.Core/         # crypto core — class library, NO UI deps, BCL only
src/FileLock.UI/           # WPF app — drag-drop + PasswordBox, references Core
tests/FileLock.Core.Tests/ # xUnit tests for the core
```

The crypto core is deliberately UI-agnostic so it can be unit-tested without WPF. All real
logic lives in `FileLock.Core`; the UI only stores the default password, decides which
window/CLI path to take, and maps exceptions to friendly messages. Lock-vs-unlock is decided
by **reading the file header** (`FileCryptor.IsLocked`), not by extension.

Key files:
- `src/FileLock.Core/FileFormat.cs` — on-disk layout + constants (single source of truth)
- `src/FileLock.Core/FileCryptor.cs` — `Lock`/`Unlock`, `LockInPlace`/`UnlockInPlace`, `IsLocked`, `ReadHeaderInfo`
- `src/FileLock.Core/LockToggleService.cs` — the toggle orchestrator (detect → back up → lock/unlock in place → ledger)
- `src/FileLock.Core/BackupStore.cs` — `.backup` folder + `ledger.jsonl` (timestamp-prefixed backup names)
- `src/FileLock.Core/LedgerEntry.cs` / `LockedFileInfo.cs` — ledger record (+ STJ source-gen) / header peek result
- `src/FileLock.Core/KeyDerivation.cs` — PBKDF2 wrapper (span-based, writes into a destination)
- `src/FileLock.Core/Exceptions.cs` — `BadFormatException`, `WrongPasswordException`, `FileTooLargeException`
- `src/FileLock.UI/App.xaml.cs` — CLI-vs-window branching, headless processing, tray balloon, Fluent `ThemeMode`
- `src/FileLock.UI/SettingsStore.cs` — DPAPI-protected default password in `settings.json`
- `src/FileLock.UI/MainWindow.xaml(.cs)` — settings window (set password, drop zone, open backup folder)

## Commands

```powershell
dotnet build FileLock.sln          # must be clean: 0 warnings, 0 errors
dotnet test  FileLock.sln          # all tests must pass before any change is "done"
dotnet run --project src/FileLock.UI/FileLock.UI.csproj   # launch the app
```

Targets `net10.0` (Core/Tests) and `net10.0-windows` (UI). The SDK is pinned in
`global.json` to .NET 10. If the `dotnet` on PATH is older, it will refuse with a
"version required" message — install the .NET 10 SDK (the WindowsDesktop runtime is needed
to run the WPF app). Do not lower `TargetFramework` to make an older SDK work.

## Crypto invariants — DO NOT BREAK

These are hard rules. A change that violates one is wrong even if it compiles and passes.

- **BCL crypto only for files.** Use `System.Security.Cryptography` (`AesGcm`,
  `Rfc2898DeriveBytes`, `RandomNumberGenerator`). Never roll custom crypto (no custom
  ciphers, no custom MAC, no XOR "encryption"). **Exception:** the *settings password* (not
  files) is protected with Windows **DPAPI** (`ProtectedData`, `CurrentUser`) in the UI —
  it's framework-provided (no NuGet) and never touches file encryption.
- **Fail closed.** Any error on unlock (wrong password, tampered/corrupt file, bad format)
  must reject and write nothing — the source file is left exactly as it was. A wrong password
  and a tampered file are indistinguishable (both fail the GCM tag) → `WrongPasswordException`.
- **Never write a partial/garbage output.** Write to a temp file in the destination's
  directory, flush, then atomically `File.Move` into place. New-file APIs use
  `overwrite: false` (+ ` (1)`, ` (2)`, … via `FileCryptor.GetAvailablePath`); the in-place
  APIs use `overwrite: true` and **must close the read handle before the move** (Windows
  won't replace a file with an open handle).
- **Back up before replacing in place.** `LockInPlace`/`UnlockInPlace` overwrite the source,
  so the old "never modify the source" rule is replaced by: the toggle service copies the
  original into `.backup` (recorded in the ledger) **before** locking. (Unlock is reversible
  and fail-closed, so it is not backed up — by design.)
- **Zero key material** with `CryptographicOperations.ZeroMemory` after use. Keys live in
  `stackalloc` spans; pooled buffers that held plaintext are zeroed before being returned
  to `ArrayPool`. Never cache keys. (The decrypted settings password is also zeroed.)
- The GCM **auth tag replaces any checksum**. The header up to the tag (bytes `0 .. 48+N`,
  i.e. through the locker user name) is fed in as **AAD**, so version/iterations/salt/nonce/
  date/locker-name cannot be silently altered.

## Performance conventions (keep these when editing the core)

- Small fixed-size secrets (salt, nonce, tag, key) and the variable header (≤ 320 bytes,
  bounded by `MaxUsernameBytes`) use `stackalloc`.
- Large plaintext/ciphertext buffers are rented from `ArrayPool<byte>.Shared` and returned
  in a `finally`; anything holding plaintext is zeroed first.
- Read file contents with `RandomAccess` directly into the destination span — avoid extra
  `byte[]` copies. PBKDF2 derives straight into the stackalloc key span.

## File format

**Variable-length** binary header (it carries the locker's user name), all multi-byte
integers little-endian, then ciphertext. Exact offsets and constants live in `FileFormat.cs`
— read it before touching the format. The file name/extension is **not** part of the format;
detection is by the magic header, not the extension.

```
off  0  4   Magic "FLK1" ("FLK1"u8)   off 38   8  Encryption date (Unix UTC seconds)
off  4  2   Version (1)               off 46   2  User-name length N
off  6  4   PBKDF2 iterations         off 48   N  Locker user name (UTF-8)
off 10 16   PBKDF2 salt               off 48+N 16 GCM auth tag
off 26 12   GCM nonce                 off 64+N ..  ciphertext of [2-byte name len][name UTF-8][file bytes]
```

AAD = everything before the tag (bytes `0 .. 48+N`). The PBKDF2 iteration count is stored
per-file so a future version can raise it without breaking existing files. There is **no
legacy format** — readers accept exactly `Version == 1` and reject anything else. If you ever
change the layout, bump `Version`; backward-compatibility is only required once files exist
in the wild.

## UX rules

- Say **Lock / Unlock**, never Encrypt/Decrypt. No crypto jargon anywhere in the UI.
- Errors shown to users are short and friendly — never a stack trace. The exception
  messages in `Exceptions.cs` are already user-safe; reuse them. A shortcut drop reports via
  a tray balloon; the only hard blocker (no password set yet) uses a MessageBox.
- The window must show the no-recovery warning: "If you forget your password, the file
  cannot be recovered. There is no backdoor."
- The UI uses WPF's Fluent `ThemeMode` (forced Light for a consistent palette; switch to
  `ThemeMode.System` for OS-adaptive light/dark).

## Storage & settings

`settings.json`, `.backup/`, and `.backup/ledger.jsonl` all live under
`AppContext.BaseDirectory` (the install folder), treated as a trusted location — the
password's purpose is to protect files **in transit**, not at rest locally. There is **no
fallback**: if that folder isn't writable (e.g. Program Files), operations fail with a
friendly message. The default password is DPAPI-protected (`CurrentUser`), so a copied
`settings.json` is useless on another machine/user.

## Out of scope for v1 (don't build without being asked)

Streaming for files >500 MB, Argon2id KDF (needs NuGet), folder/recursive locking
(dropping multiple **individual files** on the shortcut is supported; folders are rejected),
secure-delete of originals, cross-platform (Avalonia) port, auto-installing the shortcut /
"Send To" entry.

## Conventions

- After each change, build and run the tests before calling it done.
- Match the surrounding code's style; keep the core free of UI/WPF references.
- NuGet versions are added in `Directory.Packages.props` (CPM), not inline in csproj files.
- Tests live in `FileLock.Core.Tests` and use xUnit; cover both happy path and the
  fail-closed paths (wrong password, tampering, bad format) for any crypto change.
