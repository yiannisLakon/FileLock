# CLAUDE.md

Guidance for AI agents working in this repository.

## What this is

**FileLock** — a Windows desktop app that locks (encrypts) and unlocks (decrypts) files
with a password, for non-technical users. Drag a file onto the window, type a password,
and the file is locked/unlocked in place. **.NET 11**, WPF, C# (latest language version).

## Architecture

```
FileLock.sln
global.json                # pins the .NET SDK (currently an 11.0 preview)
Directory.Build.props      # shared MSBuild settings for every project
Directory.Packages.props   # Central Package Management — all NuGet versions live here
src/FileLock.Core/         # crypto core — class library, NO UI deps, BCL only
src/FileLock.UI/           # WPF app — drag-drop + PasswordBox, references Core
tests/FileLock.Core.Tests/ # xUnit tests for the core
```

The crypto core is deliberately UI-agnostic so it can be unit-tested without WPF. The UI
(`MainWindow.xaml.cs`) is a thin layer: it decides lock-vs-unlock by file extension, runs
the core on a background thread, and maps exceptions to friendly status messages. All real
logic lives in `FileLock.Core`.

Key files:
- `src/FileLock.Core/FileFormat.cs` — on-disk layout + constants (single source of truth)
- `src/FileLock.Core/FileCryptor.cs` — `Lock` / `Unlock`
- `src/FileLock.Core/KeyDerivation.cs` — PBKDF2 wrapper (span-based, writes into a destination)
- `src/FileLock.Core/Exceptions.cs` — `BadFormatException`, `WrongPasswordException`, `FileTooLargeException`
- `src/FileLock.UI/MainWindow.xaml(.cs)` — the window and drop handling
- `src/FileLock.UI/App.xaml.cs` — Fluent `ThemeMode` + global exception backstop

## Commands

```powershell
dotnet build FileLock.sln          # must be clean: 0 warnings, 0 errors
dotnet test  FileLock.sln          # all tests must pass before any change is "done"
dotnet run --project src/FileLock.UI/FileLock.UI.csproj   # launch the app
```

Targets `net11.0` (Core/Tests) and `net11.0-windows` (UI). The SDK is pinned in
`global.json` to a .NET 11 preview. If the `dotnet` on PATH is older, it will refuse with a
"version required" message — invoke a .NET 11 SDK explicitly, e.g.
`& "C:\Users\<you>\dotnet-preview\dotnet.exe" build FileLock.sln` (set `DOTNET_ROOT` to
that folder so the WindowsDesktop runtime resolves when running the WPF app). Do not lower
`TargetFramework` to make an older SDK work.

## Crypto invariants — DO NOT BREAK

These are hard rules. A change that violates one is wrong even if it compiles and passes.

- **BCL crypto only.** Use `System.Security.Cryptography` (`AesGcm`, `Rfc2898DeriveBytes`,
  `RandomNumberGenerator`). Never add a NuGet crypto package. Never roll custom crypto
  (no custom ciphers, no custom MAC, no XOR "encryption").
- **Fail closed.** Any error on unlock (wrong password, tampered/corrupt file, bad format)
  must reject and write nothing. A wrong password and a tampered file are indistinguishable
  (both fail the GCM tag) and surface as `WrongPasswordException`.
- **Never write a partial/garbage output.** Write to a temp file, flush, then atomically
  `File.Move` into place. Never overwrite (`overwrite: false`); de-duplicate names with
  ` (1)`, ` (2)`, … via `FileCryptor.GetAvailablePath`.
- **Never modify or delete the source file.** Output is written next to the source; the
  user deletes originals themselves.
- **Zero key material** with `CryptographicOperations.ZeroMemory` after use. Keys live in
  `stackalloc` spans; pooled buffers that held plaintext are zeroed before being returned
  to `ArrayPool`. Never cache keys.
- The GCM **auth tag replaces any checksum**. The header (offsets 0–45) is fed in as **AAD**,
  so version/iterations/salt/nonce/date cannot be silently altered.

## Performance conventions (keep these when editing the core)

- Small fixed-size secrets (salt, nonce, tag, key, the 62-byte header) use `stackalloc`.
- Large plaintext/ciphertext buffers are rented from `ArrayPool<byte>.Shared` and returned
  in a `finally`; anything holding plaintext is zeroed first.
- Read file contents with `RandomAccess` directly into the destination span — avoid extra
  `byte[]` copies. PBKDF2 derives straight into the stackalloc key span.

## File format (`.locked`)

62-byte binary header, all multi-byte integers little-endian, then ciphertext. Exact
offsets and constants live in `FileFormat.cs` — read it before touching the format.

```
off  0  4   Magic "FLK1" ("FLK1"u8)   off 26  12  GCM nonce
off  4  2   Version (1)               off 38   8  Encryption date (Unix UTC seconds)
off  6  4   PBKDF2 iterations         off 46  16  GCM auth tag
off 10 16   PBKDF2 salt               off 62  ..  ciphertext of [2-byte name len][name UTF-8][file bytes]
```

AAD = header bytes 0–45. The PBKDF2 iteration count is stored per-file so a future format
version can raise it without breaking existing files — if you change the format, bump
`Version` and keep readers backward-compatible.

## UX rules

- Say **Lock / Unlock**, never Encrypt/Decrypt. No crypto jargon anywhere in the UI.
- Errors shown to users are short and friendly — never a stack trace. The exception
  messages in `Exceptions.cs` are already user-safe; reuse them.
- The window must show the no-recovery warning: "If you forget your password, the file
  cannot be recovered. There is no backdoor."
- The UI uses WPF's Fluent `ThemeMode` (forced Light for a consistent palette; switch to
  `ThemeMode.System` for OS-adaptive light/dark).

## Out of scope for v1 (don't build without being asked)

Streaming for files >500 MB, Argon2id KDF (needs NuGet), folder/batch locking,
secure-delete of originals, cross-platform (Avalonia) port.

## Conventions

- After each change, build and run the tests before calling it done.
- Match the surrounding code's style; keep the core free of UI/WPF references.
- NuGet versions are added in `Directory.Packages.props` (CPM), not inline in csproj files.
- Tests live in `FileLock.Core.Tests` and use xUnit; cover both happy path and the
  fail-closed paths (wrong password, tampering, bad format) for any crypto change.
