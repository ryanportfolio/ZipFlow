# ZipFlow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use test-driven development while implementing each behavior. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a lightweight Windows tool whose selected `.zip` default action safely extracts to the Desktop, removes the source only after verified success, and opens the output folder.

**Architecture:** A small C# `winexe` targets the inbox .NET Framework 4.8 runtime and is invoked by Windows file association, so no resident process or PowerShell handler is needed. The core preflights a ZIP, validates central-directory CRC metadata and Windows paths, extracts into a same-volume hidden staging directory under the Desktop, verifies byte counts and CRC-32, atomically publishes to a collision-free folder, sends the source to Recycle Bin, then opens the folder. PowerShell installer scripts register ZipFlow only as a per-user candidate and open Windows Default Apps; they never write protected `UserChoice` state.

**Tech Stack:** C# 5-compatible source, .NET Framework 4.8 inbox compiler/runtime, `System.IO.Compression`, Windows Forms for concise error UI, Microsoft.VisualBasic FileIO for Recycle Bin, Windows PowerShell 5.1 installer/build scripts, dependency-free console test harness.

---

## File map

- `src/ZipFlow.Core.cs` — validation, central-directory CRC parser, quotas, staging extraction, publish/delete/open sequence.
- `src/ZipFlow.Program.cs` — windowless entry point, Desktop resolution, Recycle Bin remover, Explorer launcher, error dialog.
- `tests/ZipFlow.Tests.cs` — dependency-free real-archive and failure-order tests.
- `tests/Registration.Tests.ps1` — pure registration-plan safety tests.
- `installer/ZipFlow.Registration.ps1` — pure registry plan plus apply/remove helpers and Shell notification.
- `Install.ps1` — build/copy/register and open Default Apps.
- `Uninstall.ps1` — warning, unregister owned state only, remove installed files.
- `build.ps1` — locate inbox `csc.exe`, compile tests and windowless application, run tests.
- `.gitignore` — ignore transient `artifacts/`; retain distributable `dist/ZipFlow.exe`.
- `README.md` — install, one-time default selection, use, limits, recovery, uninstall.
- `LICENSE` — MIT license.

## Safety contract

- Never write `...Explorer\\FileExts\\.zip\\UserChoice` or set the default `.zip` ProgID directly.
- Never write outside randomized Desktop staging before publish.
- Reject rooted/traversal/ADS/device/ambiguous names, duplicate destinations, file-directory collisions, unsupported ZIP64/multidisk/encryption, quota violations, and CRC mismatch.
- Default limits: 10,000 entries, 1 GiB per file, 4 GiB total expanded bytes, 1,000:1 compression ratio, 32 path segments, 240-character full destination path.
- Existing folders are immutable; final names use `name`, `name (2)`, `name (3)`, and so on.
- Extraction or publish failure: delete staging best-effort, preserve ZIP, do not open.
- Delete failure: preserve ZIP and completed output, do not open; show error.
- Open failure: ZIP is already in Recycle Bin and completed output remains; show error containing output path.

### Task 1: Establish RED core tests

**Files:**
- Create: `tests/ZipFlow.Tests.cs`
- Create: `build.ps1`
- Create: `.gitignore`

- [ ] **Step 1: Write a console test harness before production source exists**

The harness creates every archive beneath a randomized `%TEMP%` root and exposes named tests with nonzero exit on any failure. Include these cases:

```text
safe_nested_archive_is_verified_removed_then_opened
existing_output_is_preserved_and_suffix_is_used
traversal_is_rejected_and_source_is_preserved
ads_device_and_trailing_dot_names_are_rejected
case_duplicate_and_file_directory_conflicts_are_rejected
quota_violations_are_rejected_before_publish
crc_mismatch_is_rejected_and_source_is_preserved
corrupt_archive_cleans_staging_and_preserves_source
delete_failure_keeps_source_and_suppresses_open
open_failure_keeps_published_output_after_source_removal
empty_archive_publishes_an_empty_folder
```

Use real `ZipArchive` files, a recording `ISourceRemover`, and a recording `IFolderLauncher`. Every failure test asserts: source presence, no unexpected final folder, no `.zipflow-*.partial` folder, and callback order.

- [ ] **Step 2: Add a deterministic build driver**

`build.ps1` accepts `-RunTests` and `-TestsOnly`, locates `C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe` then 32-bit fallback, creates `artifacts/` and `dist/`, compiles tests from `tests/ZipFlow.Tests.cs` plus `src/ZipFlow.Core.cs`, and compiles the app from both `src` files. Treat warnings as errors.

- [ ] **Step 3: Run RED**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -TestsOnly
```

Expected: nonzero compile result because `src/ZipFlow.Core.cs` and its required types do not exist yet. Confirm the failure names the missing feature, not a test syntax error.

### Task 2: Implement safe extraction and destructive ordering

**Files:**
- Create: `src/ZipFlow.Core.cs`
- Modify: `tests/ZipFlow.Tests.cs`

- [ ] **Step 1: Define the wished-for API used by tests**

```csharp
namespace ZipFlow
{
    public interface ISourceRemover { void Remove(string path); }
    public interface IFolderLauncher { void Open(string path); }

    public sealed class ArchivePolicy
    {
        public readonly int MaxEntries;
        public readonly long MaxEntryBytes;
        public readonly long MaxTotalBytes;
        public readonly long MaxCompressionRatio;
        public readonly int MaxDepth;
        public readonly int MaxFullPath;
        public static ArchivePolicy Default { get; }
    }

    public sealed class ZipProcessor
    {
        public ZipProcessor(ArchivePolicy policy, ISourceRemover remover, IFolderLauncher launcher);
        public string Process(string archivePath, string destinationRoot);
    }
}
```

- [ ] **Step 2: Implement central-directory parsing and CRC-32**

Find a structurally valid EOCD ending at file length, reject multidisk and ZIP64 sentinels, bounds-check central-directory offsets/sizes, then parse exactly the advertised entry count. Capture flags, method, CRC-32, compressed size, expanded size, and raw/decoded name in central order. Reject encryption. During extraction, calculate standard ZIP CRC-32 (`0xEDB88320`) over emitted bytes and require equality with central metadata before publish.

- [ ] **Step 3: Implement pure preflight planning**

Normalize `/` and `\\` to Windows separators. Reject empty/rooted/UNC/drive/device paths; empty interior, `.`, or `..` segments; controls/invalid filename characters; colons; leading/trailing spaces; trailing dots; reserved DOS basenames including superscript COM/LPT suffixes. Enforce case-insensitive uniqueness and ancestor file-directory consistency. Enforce all limits with checked `long` addition and verify every resolved full path starts with staging root plus separator.

- [ ] **Step 4: Implement two-phase extraction**

Keep source open with `FileShare.Read`; preflight before staging. Create `.zipflow-<guid>.partial` under destination root. Create directories from plan; write files with `FileMode.CreateNew`; count actual bytes, enforce live limits, compare declared length and CRC, flush and close. Choose collision-free final folder, then `Directory.Move(staging, final)` on the same volume.

- [ ] **Step 5: Implement final ordering and cleanup**

After archive/output handles close and publish succeeds: recheck source length and last-write time, call remover, require source absent, then call launcher. On pre-publish exception, cleanup staging only. Never remove an existing or published final folder. Return final path only after launch succeeds.

- [ ] **Step 6: Run GREEN and refactor**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -TestsOnly -RunTests
```

Expected: all named core tests pass, zero warnings. Refactor duplicated test setup only while staying green.

### Task 3: Add windowless application shell

**Files:**
- Create: `src/ZipFlow.Program.cs`
- Modify: `build.ps1`
- Modify: `tests/ZipFlow.Tests.cs`

- [ ] **Step 1: Add argument and sequence tests before entry-point code**

Test that processing rejects zero/multiple arguments, a missing file, and non-`.zip` input without invoking remover/launcher. Run tests and observe expected missing-entry behavior.

- [ ] **Step 2: Implement shell adapters**

`RecycleBinSourceRemover` calls `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` with `UIOption.OnlyErrorDialogs` and `RecycleOption.SendToRecycleBin`. `ExplorerFolderLauncher` uses `ProcessStartInfo { FileName = path, UseShellExecute = true }`. `Program.Main` resolves `Environment.SpecialFolder.DesktopDirectory`, runs the processor, and shows a concise `MessageBox` on failure, including any published output path present in the exception.

- [ ] **Step 3: Compile as windowless executable**

Use `/target:winexe`, references to `System.IO.Compression.dll`, `System.IO.Compression.FileSystem.dll`, `System.Windows.Forms.dll`, and `Microsoft.VisualBasic.dll`, and output `dist/ZipFlow.exe`.

- [ ] **Step 4: Verify build plus core suite**

Run the full build/test command. Expected: tests pass and `dist/ZipFlow.exe` exists with Windows GUI subsystem.

### Task 4: Build safe per-user registration through TDD

**Files:**
- Create: `tests/Registration.Tests.ps1`
- Create: `installer/ZipFlow.Registration.ps1`
- Create: `Install.ps1`
- Create: `Uninstall.ps1`

- [ ] **Step 1: Write registration-plan tests first**

Tests call a pure `Get-ZipFlowRegistrationPlan -ExecutablePath 'C:\\Safe Path\\ZipFlow.exe'` and assert:

```text
ProgID = ZipFlow.Archive
command = "C:\Safe Path\ZipFlow.exe" "%1"
Capabilities FileAssociations .zip = ZipFlow.Archive
RegisteredApplications ZipFlow = Software\ZipFlow\Capabilities
OpenWithProgids contains ZipFlow.Archive
no path/name/value contains UserChoice
no operation sets HKCU\Software\Classes\.zip default value
all operations are under HKCU\Software\Classes or HKCU\Software\ZipFlow or HKCU\Software\RegisteredApplications
```

Run test script and verify failure because registration module is absent.

- [ ] **Step 2: Implement pure plan and mutation helpers**

Return typed `SetValue` records for the ProgID open command, application open command/supported type, Capabilities, RegisteredApplications, and `.zip\\OpenWithProgids`. `Install-ZipFlowRegistration` applies only those records. `Uninstall-ZipFlowRegistration` removes only owned values/keys and calls `SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, ...)` after mutations.

- [ ] **Step 3: Implement install flow**

Build if `dist/ZipFlow.exe` is missing, copy to `$env:LOCALAPPDATA\\ZipFlow\\ZipFlow.exe`, register per user, print the one-time selection instruction, then open `ms-settings:defaultapps` (generic URI supports this Windows 10 host; README also documents Windows 11 app-specific UI availability). Do not claim the app became default.

- [ ] **Step 4: Implement uninstall flow**

Warn user to choose File Explorer/another archive app first because Windows owns default selection. Require explicit confirmation unless `-Force`; unregister only owned state; remove installed ZipFlow files; notify Shell. Never read/write/delete `UserChoice`.

- [ ] **Step 5: Run registration tests**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Registration.Tests.ps1
```

Expected: all registration safety assertions pass without registry mutation.

### Task 5: Document and package

**Files:**
- Create: `README.md`
- Create: `LICENSE`
- Modify: `.gitignore`

- [ ] **Step 1: Document zero-context install/use**

README must explain: run `Install.ps1`; Windows opens Default Apps; choose ZipFlow for `.zip` once; afterward double-click extracts to Desktop, sends original to Recycle Bin only after CRC-verified success, and opens output. State no background process runs.

- [ ] **Step 2: Document boundaries and recovery**

List quotas, unsupported encrypted/ZIP64/multidisk archives, collision suffixing, source preservation on failure, completed-output behavior on delete/open failure, uninstall steps, and how to restore File Explorer as default.

- [ ] **Step 3: Produce release artifact**

Run full build/test and retain only `dist/ZipFlow.exe`; keep transient test binaries in ignored `artifacts/`.

### Task 6: Independent review and authoritative verification

**Files:** all project files.

- [ ] **Step 1: Fresh spec review**

Reviewer maps every original behavior and safety contract to exact code/tests. Any missing behavior blocks acceptance.

- [ ] **Step 2: Fresh quality/security review**

Reviewer attempts to refute path containment, collision, CRC, quotas, cleanup, deletion ordering, registry ownership, and quoting. Confirmed issues return to implementer and are re-reviewed.

- [ ] **Step 3: Full local verification**

Run natively under Windows PowerShell 5.1:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -RunTests
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Registration.Tests.ps1
```

Also inspect PE subsystem, run a real temporary archive through the core end-to-end, scan sources for `UserChoice`, verify no test touches Desktop/registry, and inspect final diff/file inventory.

- [ ] **Step 4: Create local repo without publishing**

Place verified project at `C:\Users\Home\CoreWise\ZipFlow`, run `git init -b main`, and leave changes uncommitted/unpushed unless separately authorized.
