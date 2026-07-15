# ZipFlow

**Double-click a ZIP. Get the folder. Move on.**

[![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows)](https://github.com/ryanportfolio/ZipFlow/releases/latest)
[![Latest release](https://img.shields.io/github/v/release/ryanportfolio/ZipFlow?label=release)](https://github.com/ryanportfolio/ZipFlow/releases/latest)
[![MIT License](https://img.shields.io/badge/license-MIT-2ea44f)](LICENSE)

Opening a downloaded archive usually means choosing a destination, extracting it, deleting the old ZIP, and finding the new folder. ZipFlow turns that routine into one double-click.

It runs only when you open a ZIP. No background process. No tray icon. No service. No administrator access.

## Get ZipFlow

1. Download [ZipFlow.exe](https://github.com/ryanportfolio/ZipFlow/releases/latest/download/ZipFlow.exe).
2. Double-click it once.
3. Read the short setup message and click **OK**.
4. Windows Default Apps opens. Choose **ZipFlow** for `.zip`.

That Windows choice happens once. Afterward, double-click ZIP files normally.

Prefer portable mode? Drag a ZIP onto the downloaded `ZipFlow.exe` instead of opening the EXE by itself. ZipFlow processes that archive without installing itself.

## What setup changes

Opening `ZipFlow.exe` by itself performs the setup work automatically:

- Copies the executable to `%LOCALAPPDATA%\ZipFlow\ZipFlow.exe`
- Registers ZipFlow as an available `.zip` app for your Windows account
- Opens the most specific Default Apps page Windows supports

Setup uses no administrator access, service, startup task, tray icon, or background process. It does not replace Windows' protected default-app choice. [Windows requires that one approval to happen in its own interface](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/default-apps-platform#security-considerations-for-the-app-defaults-platform).

The release bundle still includes `Install.ps1` for scripted setup and `Uninstall.ps1` for removal. Normal setup does not require PowerShell.

## What one double-click does

1. Reads and validates the archive.
2. Extracts into a hidden staging folder on the Desktop.
3. Publishes the completed folder as `Archive`, `Archive (2)`, `Archive (3)`, and so on.
4. Sends the original ZIP to the Recycle Bin.
5. Opens the completed folder in File Explorer.

The original ZIP is not touched until extraction finishes and every file passes validation.

## Failure guarantees

| Failure point | Original ZIP | Completed folder |
|---|---|---|
| Validation, extraction, or publishing | Kept | Not published |
| Moving the ZIP to Recycle Bin | Kept | Kept |
| Opening File Explorer | Already recycled | Kept |

If staging cleanup fails, the error names the retained staging path. Every error also reports whether the source ZIP remains, was recycled, or no longer exists at the original path.

## Why it is careful

ZIP files can contain broken paths, duplicate names, misleading metadata, and huge compressed payloads. ZipFlow checks before it cleans up after you.

- Verifies file sizes and CRC-32 before publishing output
- Blocks path traversal, alternate data streams, Windows device names, and observed reparse points
- Rejects duplicate destinations and file-directory conflicts
- Never overwrites an existing Desktop folder
- Rechecks the source file's Windows identity before recycling it
- Extracts with no overwrite and keeps partial work hidden

Default resource limits:

- 10,000 entries
- 1 GiB uncompressed per file
- 4 GiB uncompressed total
- 1000:1 maximum compression ratio
- 32 path components including the filename
- 240-character maximum output path

Encrypted, ZIP64, and multidisk archives are not supported. They fail without recycling the source ZIP.

## Build and test

ZipFlow targets the .NET Framework included with current Windows 10 and Windows 11 installations. No package restore or third-party runtime is required.

```powershell
.\build.ps1 -RunTests
```

The build produces `dist\ZipFlow.exe` and runs the dependency-free archive behavior suite. Installer and registration tests run separately:

```powershell
.\tests\Registration.Tests.ps1
```

## Threat boundary

ZipFlow defends against paths and content controlled by an archive. Its Desktop operations use normal path-based Windows APIs, so another process running as the same user could still race filesystem objects between a check and a write. Do not run ZipFlow elevated.

The final source move uses the Windows Recycle Bin API. A narrow path race remains between the last source identity check and that path-based move.

## Uninstall

Download and extract the **Windows bundle** from the [latest release](https://github.com/ryanportfolio/ZipFlow/releases/latest). First restore `.zip` to File Explorer or another archive app in Windows Default Apps. Then run:

```powershell
.\Uninstall.ps1
```

Uninstall removes only ZipFlow's per-user registration and installed executable. It does not choose a replacement default or recursively delete the install directory.

## License

[MIT](LICENSE)
