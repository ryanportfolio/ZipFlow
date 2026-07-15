# ZipFlow

**Double-click a ZIP. Get the folder. Move on.**

[![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows)](https://github.com/ryanportfolio/ZipFlow/releases/latest)
[![Latest release](https://img.shields.io/github/v/release/ryanportfolio/ZipFlow?label=release)](https://github.com/ryanportfolio/ZipFlow/releases/latest)
[![MIT License](https://img.shields.io/badge/license-MIT-2ea44f)](LICENSE)

Opening a downloaded archive usually means choosing a destination, extracting it, deleting the old ZIP, and finding the new folder. ZipFlow turns that routine into one double-click.

It runs only when you open a ZIP. No background process. No tray icon. No service. No administrator access.

## Get ZipFlow

1. Download **ZipFlow for Windows** from the [latest release](https://github.com/ryanportfolio/ZipFlow/releases/latest).
2. Extract the downloaded bundle.
3. Open PowerShell in that folder and run:

   ```powershell
   .\Install.ps1
   ```

4. Windows Default Apps opens. Find `.zip` and choose **ZipFlow**.

That Windows choice happens once. Afterward, double-click any `.zip` file.

Prefer portable mode? Drag a ZIP onto `ZipFlow.exe`. Nothing gets installed.

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

First restore `.zip` to File Explorer or another archive app in Windows Default Apps. Then run:

```powershell
.\Uninstall.ps1
```

Uninstall removes only ZipFlow's per-user registration and installed executable. It does not choose a replacement default or recursively delete the install directory.

## License

[MIT](LICENSE)
