# ZipFlow Self-Setup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a downloaded `ZipFlow.exe` handle its own per-user setup when opened without a ZIP, while preserving one-argument archive processing and clearly guiding the one Windows-owned default-app choice.

**Architecture:** Add a focused C# setup module with a testable backend boundary. `Program` dispatches zero arguments to an interactive setup flow and one argument to the existing archive processor. Setup copies the running executable to `%LOCALAPPDATA%\ZipFlow\ZipFlow.exe`, writes only ZipFlow-owned HKCU registration values, notifies Explorer, explains the remaining Windows choice, and opens the ZipFlow Default Apps page.

**Tech Stack:** C# 5, .NET Framework WinForms, `Microsoft.Win32.Registry`, Windows Shell APIs, PowerShell 5.1 build and installer tests.

---

### Task 1: Specify zero-argument launch behavior

**Files:**
- Modify: `tests/ZipFlow.Tests.cs`
- Modify: `src/ZipFlow.Program.cs`

- [ ] **Step 1: Write the failing dispatch test**

Add a recording implementation of this wished-for boundary:

```csharp
internal interface IZipFlowSetupFlow
{
    void Run();
}
```

Add `zero_argument_launch_runs_setup_without_archive_side_effects`. It calls the five-argument `Program.Run` overload with `new string[0]`, then asserts one setup call, zero source-remover calls, zero folder-launcher calls, and a null archive result.

- [ ] **Step 2: Verify RED**

Run:

```powershell
.\build.ps1 -RunTests -TestsOnly
```

Expected: compilation fails because `IZipFlowSetupFlow` and the five-argument `Program.Run` overload do not exist.

- [ ] **Step 3: Implement minimal dispatch**

Add the interface and overload:

```csharp
internal static string Run(
    string[] args,
    string destinationRoot,
    ISourceRemover remover,
    IFolderLauncher launcher,
    IZipFlowSetupFlow setupFlow)
{
    if (args == null || args.Length == 0)
    {
        setupFlow.Run();
        return null;
    }

    return Run(args, destinationRoot, remover, launcher);
}
```

Keep the existing four-argument archive path unchanged. Update `Main` to call the new overload.

- [ ] **Step 4: Verify GREEN**

Run the same test command. Expected: 30/30 tests pass.

### Task 2: Specify self-contained per-user registration

**Files:**
- Create: `src/ZipFlow.Setup.cs`
- Modify: `tests/ZipFlow.Tests.cs`
- Modify: `build.ps1`

- [ ] **Step 1: Write failing setup tests**

Define a recording `IZipFlowSetupBackend` in the tests and add cases proving:

```text
raw EXE -> create %LOCALAPPDATA%\ZipFlow, copy ZipFlow.exe, write 9 HKCU values, notify once
installed EXE -> skip copy, refresh the same 9 values, notify once
copy failure -> write no registration values and send no notification
registration plan -> contains no UserChoice path and never replaces HKCU\Software\Classes\.zip default
```

The expected open command is:

```text
"C:\Users\Test\AppData\Local\ZipFlow\ZipFlow.exe" "%1"
```

- [ ] **Step 2: Verify RED**

Run `.\build.ps1 -RunTests -TestsOnly`. Expected: compilation fails because the setup module is absent.

- [ ] **Step 3: Implement the setup module**

Create these focused types:

```csharp
internal interface IZipFlowSetupBackend
{
    void EnsureDirectory(string path);
    void CopyFile(string source, string destination);
    void SetCurrentUserValue(string subKeyPath, string name, object value, RegistryValueKind kind);
    void NotifyAssociationsChanged();
    void OpenDefaultApps();
}

internal sealed class ZipFlowRegistrationValue
{
    internal readonly string SubKeyPath;
    internal readonly string Name;
    internal readonly object Value;
    internal readonly RegistryValueKind Kind;
}

internal sealed class ZipFlowSetup
{
    internal string Install(string currentExecutable, string localAppData);
    internal static IList<ZipFlowRegistrationValue> GetRegistrationPlan(string installedExecutable);
}
```

`Install` validates both paths, calculates `%LOCALAPPDATA%\ZipFlow\ZipFlow.exe`, copies only when source and target differ case-insensitively, applies the nine values already used by `installer/ZipFlow.Registration.ps1`, then sends `SHCNE_ASSOCCHANGED` once.

- [ ] **Step 4: Add the Windows backend**

Implement `WindowsZipFlowSetupBackend` with `Directory.CreateDirectory`, `File.Copy(..., true)`, `Registry.CurrentUser.CreateSubKey`, `SHChangeNotify`, and this preferred Settings URI:

```text
ms-settings:defaultapps?registeredAppUser=ZipFlow
```

If that launch throws, retry `ms-settings:defaultapps`.

- [ ] **Step 5: Include setup code in both build targets**

Add `src\ZipFlow.Setup.cs` to `$testSources` and `$appArguments` in `build.ps1`.

- [ ] **Step 6: Verify GREEN**

Run `.\build.ps1 -RunTests -TestsOnly`. Expected: all setup and archive tests pass.

### Task 3: Make first launch explain the only remaining choice

**Files:**
- Modify: `src/ZipFlow.Program.cs`
- Modify: `tests/ZipFlow.Tests.cs`

- [ ] **Step 1: Write the failing guidance test**

Add a test for `Program.SetupInstructions` requiring these concrete ideas:

```text
ZipFlow installed itself for this Windows account.
Choose ZipFlow for .zip once in Default Apps.
After that, double-click ZIP files normally.
```

- [ ] **Step 2: Verify RED**

Run `.\build.ps1 -RunTests -TestsOnly`. Expected: compilation fails because the setup instructions and production flow do not exist.

- [ ] **Step 3: Implement the interactive flow**

Add `InteractiveZipFlowSetupFlow`. Its `Run` method installs the running EXE, shows one information dialog before Settings, then opens Default Apps:

```text
One Windows choice remains

ZipFlow installed itself for this Windows account.

Click OK to open Default Apps. Choose ZipFlow for .zip once.
Windows requires you to approve this choice.

After that, double-click ZIP files normally.
```

Errors continue through `Main`'s existing concise error dialog.

- [ ] **Step 4: Verify GREEN**

Run `.\build.ps1 -RunTests -TestsOnly`. Expected: all tests pass.

### Task 4: Rewrite the download path around the EXE

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Replace PowerShell-first setup**

Make the primary path:

```text
1. Download ZipFlow.exe.
2. Double-click it once.
3. Read the one-screen explanation and click OK.
4. In Windows Default Apps, choose ZipFlow for .zip.
```

- [ ] **Step 2: Explain exactly what setup changes**

State that setup copies one executable to `%LOCALAPPDATA%\ZipFlow`, registers ZipFlow only for the current account, uses no administrator access or background process, and cannot silently make itself the default because Windows owns that final choice.

- [ ] **Step 3: Preserve portable and uninstall paths**

Keep drag-a-ZIP-on-the-EXE portable use. Keep `Uninstall.ps1` as the precise removal path from the release bundle.

- [ ] **Step 4: Run the writing revision pass**

Lead with the download action, remove PowerShell from the normal setup path, scan for em dashes and machine-written filler, and keep limitations concrete.

### Task 5: Verify and ship v1.1.0

**Files:**
- Modify: `dist/ZipFlow.exe`
- Add: `docs/superpowers/plans/2026-07-14-zipflow-self-setup.md`

- [ ] **Step 1: Run full verification**

```powershell
.\build.ps1 -RunTests
.\tests\Registration.Tests.ps1
git diff --check
```

Expected: all C# tests pass, all 11 PowerShell tests pass, GUI PE subsystem remains `2`, and whitespace check is clean.

- [ ] **Step 2: Audit and stage explicit paths**

Stage only:

```text
README.md
build.ps1
dist/ZipFlow.exe
docs/superpowers/plans/2026-07-14-zipflow-self-setup.md
src/ZipFlow.Program.cs
src/ZipFlow.Setup.cs
tests/ZipFlow.Tests.cs
```

- [ ] **Step 3: Commit, push, and create PR**

Commit with an imperative subject, push `codex/self-setup`, and open a ready PR with the actual test evidence.

- [ ] **Step 4: Merge and publish**

After mergeability checks, squash-merge to `main`. Package the exact merged `ZipFlow.exe` plus installer, uninstaller, registration helper, README, and license as `ZipFlow-v1.1.0-windows.zip`. Publish tag `v1.1.0` with both the bundle and standalone EXE.

- [ ] **Step 5: Verify published bytes**

Download both GitHub release assets, compare SHA-256 hashes with the local merged artifacts, inspect the bundle entries, and confirm the public repository, merged PR, clean `main`, and release metadata.
