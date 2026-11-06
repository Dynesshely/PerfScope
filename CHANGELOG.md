# Changelog

All notable changes to PerfScope are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Drivers & firmware** system module (`驱动与固件`): driver services and OEM hardware helpers
  (Armoury Crate, AMD/ATI services, Realtek, DriverStore helpers, …) are grouped into one readable row
  instead of showing up as unrecognised processes.
- Application grouping now recognises **release channels**, so e.g. Microsoft Edge stable and Edge Beta
- **Localization (i18n)**: UI strings are now resource-based (`src/PerfScope.App/Localization`),
  with English translations in `Strings.en.resx`; the language can be switched in Settings
  (Follow system / 中文 / English) and is stored in `settings.json`.
  Untranslated strings fall back to Chinese instead of showing empty text.
  are reported as two separate applications instead of being merged into one.

### Changed

- Header cards are slightly taller and the trend axis labels are aligned correctly, so the hover axes stay
  readable.
- Path-fragment matching for system modules is now case-insensitive, so differently-cased install paths no
  longer miss the intended module.

## [0.1.2] - 2026-11-02

### Added

- Custom-drawn combo boxes and dark scrollbars, so dropdowns and scrollbars follow the dark theme instead of
  keeping the system look.
- Hovering a child process row inside an expanded application now shows the full executable path.

### Changed

- Version bumped to 0.1.2.

## [0.1.1] - 2026-11-02

### Fixed

- Settings page now adapts fully to the dark theme (group boxes, labels and controls).
- Custom grouping rules layout no longer clips or misaligns rows.
- Dialogs now show the PerfScope application icon.

### Changed

- Version bumped to 0.1.1.

## [0.1.0] - 2026-11-02

First public release.

### Added

- **Application-level monitoring**: processes are merged into applications using version information, install
  roots, directory trees and parent-process inheritance; the reason behind every grouping is shown in the UI.
- **System feature modules** — Windows Subsystem for Linux, virtual machines & containers, graphics & GPU
  drivers, security, search & indexing, Windows Update and the kernel are reported as their own rows; other
  system components collapse into a `Windows 系统` group, while `explorer.exe` stays a normal application.
- **Distribution maps** for memory and CPU: treemaps with proportional areas, icons in each block, a self-drawn
  hover card, and an idle slice in the CPU view.
- **Header cards** with live system CPU and physical memory, plus hover trend curves with axes, crosshair and
  the exact value at any moment.
- **History and insights**: minute-level aggregation on disk with a configurable retention, noticeable episodes
  (CPU bursts, memory growth, system pressure) and a readable daily report.
- **Agent handoff**: one click copies a ready-to-paste prompt with real paths, metric definitions and
  preliminary findings.
- **Tray-first UI**: single instance, start with Windows, alert balloons, dark theme, and a settings page where
  sampling, smoothing, grouping, retention and analysis thresholds are adjustable and applied live.
- **Headless CLI** (`tools/PerfScope.Cli`) for frames, process lists, JSON output, reports and benchmarks.
- **Mock data mode** and a screenshot script, used to produce the images in `docs/images`.
- **Packaging and releases**: self-contained single-file portable build, per-user NSIS installer, and a
  tag-driven GitHub Actions workflow that publishes both artefacts together with `SHA256SUMS.txt`.
- English README, MIT license and project conventions in `AGENTS.md`.

### Notes

- No third-party runtime dependencies, no administrator rights required, no network access and no telemetry.
- Sampling uses a single `NtQuerySystemInformation` batch query per frame with differential composition, and
  automatically throttles while the window is hidden.

[Unreleased]: https://github.com/Dynesshely/PerfScope/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/Dynesshely/PerfScope/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/Dynesshely/PerfScope/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/Dynesshely/PerfScope/releases/tag/v0.1.0
