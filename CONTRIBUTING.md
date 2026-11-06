# Contributing to PerfScope

Thanks for taking the time to contribute! Bug reports, feature ideas, documentation fixes and code changes are
all welcome.

## Requirements

- Windows 10 or Windows 11 (x64). PerfScope is a WinForms application and cannot be built or run elsewhere.
- .NET 10 SDK — the repository pins `10.0.402` in `global.json` (`rollForward: latestFeature`).
- Git. NSIS 3 is only needed if you want to build the installer locally.

## Get the code

```powershell
git clone https://github.com/Dynesshely/PerfScope.git
cd PerfScope
dotnet build -c Debug          # must be 0 warnings, 0 errors
dotnet run --project src/PerfScope.App
```

Settings and history live in `%LOCALAPPDATA%\PerfScope`. For experiments use a separate data directory so you
never touch your real data:

```powershell
dotnet run --project src/PerfScope.App -- --data-dir .\artifacts\dev-run
```

`--mock` runs the UI against synthetic data (this is how the screenshots are produced) and never reads real
process data.

## Tests

The source of truth is the repository's test runner:

```powershell
.\scripts\run-tests.ps1                                 # Debug build
.\scripts\run-tests.ps1 -Configuration Release -Filter Grouping
```

**Why not `dotnet test`?** On some machines (including the maintainer's) the VSTest testhost cannot start and
fails with `Win32Exception (5)` while opening a parent-process handle. `tests/PerfScope.TestRunner` is a small
reflection-based runner that executes the same `[Fact]`/`[Theory]` cases from `tests/PerfScope.Tests`.
`scripts/run-tests.ps1` is therefore the authoritative entry point, and CI runs exactly that. If `dotnet test`
works on your machine you may use it, but make sure the script still passes.

## Code conventions

- `PerfScope.Core` must not reference WinForms; the UI talks to Core through the interfaces in `Abstractions`.
- Core and App keep **zero third-party NuGet dependencies** (BCL + WinForms + P/Invoke only).
- Sampling must go through `NtQuerySystemInformation(SystemProcessInformation)`; no WMI, PerformanceCounter or
  per-process `System.Diagnostics.Process` polling.
- User-facing strings are written in Chinese in source, where they double as resource keys; English lives in
  `src/PerfScope.App/Localization/Strings.en.resx`. Add a translation for every new string — missing entries fall
  back to Chinese. Code comments and log messages stay in Chinese.
- Frozen contract files are listed in `docs/CONTRACTS.md` — coordinate before changing them.
- New behaviour needs tests in `tests/PerfScope.Tests`; keep the whole suite green.
- `bin/`, `obj/`, `dist/` and `artifacts/` are ignored — do not commit build output.

## Formatting

There is no `.editorconfig` yet and `dotnet format --verify-no-changes` currently reports pre-existing whitespace
differences, so CI runs it as an advisory job only. Keep formatting diffs focused: do not reformat unrelated
code. Once the existing differences are cleaned up, the check can become blocking.

## Commit messages

Use one short Chinese subject line with a Conventional Commits style prefix, as in the existing history:

```text
feat: 顶栏新增「接入 Agent」按钮
fix: 修复设置页深色主题细节
docs: 补充 FAQ 与数据隐私说明
ci: 新增 CI 质量门与 CodeQL
```

Keep one topic per commit and reference issues (`#123`) in the body.

## Pull requests

1. Branch from `main` and open the PR against `main`.
2. Describe the problem, the approach and how you tested it. Screenshots help a lot for UI changes.
3. CI (`.github/workflows/ci.yml`) must be green: Release build + the test runner. CodeQL runs as well.
4. Keep PRs small; unrelated refactors belong in separate PRs.
5. By contributing you agree that your work is licensed under the MIT License.

## CI and releases

| Workflow | Trigger | What it does |
| --- | --- | --- |
| `ci.yml` | push / PR to `main` | `dotnet build -c Release`, `scripts/run-tests.ps1`, advisory `dotnet format` |
| `codeql.yml` | push / PR to `main`, weekly schedule | CodeQL analysis for C# and GitHub Actions |
| `release.yml` | tags `v*.*.*` | tests, portable zip + NSIS installer, `SHA256SUMS.txt`, GitHub Release |

`windows-latest` does not ship `makensis`, so CI deliberately does no packaging; only the release workflow
installs NSIS and builds the installer. To reproduce a release locally:

```powershell
.\scripts\package.ps1 -Version 0.1.3   # dist\release: portable zip + NSIS installer + SHA256SUMS
```

Release checklist for maintainers:

1. Update `CHANGELOG.md` (move *Unreleased* entries into the new version section).
2. Bump `<Version>` in `src/PerfScope.App/PerfScope.App.csproj`.
3. Commit, then tag `vX.Y.Z` and push the tag. The tag drives the assembly version and the release.

## Security

Please do not open public issues for vulnerabilities — see [SECURITY.md](SECURITY.md).
