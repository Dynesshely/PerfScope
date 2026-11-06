# Security Policy

PerfScope is a local Windows desktop tool: it reads process metadata, keeps every byte of data on your machine
and never talks to the network. Ordinary bugs should be reported in the public issue tracker, but anything that
could affect the security of users' machines must go through the process below.

## Supported versions

| Version | Supported |
| --- | --- |
| 0.1.x (latest published release) | Yes |
| Older 0.1.x releases | No — please upgrade first |
| `main` branch | Best effort; a fix lands in the next release |

PerfScope is pre-1.0 and has no long-term-support branches. Only the latest published release receives
security updates.

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Use GitHub's private vulnerability reporting:

- <https://github.com/Dynesshely/PerfScope/security/advisories/new>
- or the repository's **Security** tab → **Report a vulnerability**

Please include:

- the affected version (Settings → About, or the installer/zip file name);
- your Windows version and build (`winver`);
- the package you use (installer, portable zip, or built from source);
- reproduction steps and the impact you expect;
- relevant excerpts of `%LOCALAPPDATA%\PerfScope\logs\perfscope.log`, with user names and paths redacted.

## What to expect

- Acknowledgement within 7 days (best effort — this is a spare-time project).
- An assessment plus a fix plan, or a clear explanation of why the report is not a vulnerability.
- Coordinated disclosure: a GitHub Security Advisory is published once a fix is available. Please allow
  90 days before public disclosure.

## Scope

In scope:

- memory-safety or crash bugs reachable from process/system data (native sampling through
  `NtQuerySystemInformation`, P/Invoke signatures, unsafe code);
- path/version resolution bugs that could lead to unsafe behaviour;
- installer/uninstaller problems (per-user install, no elevation, no services, no kernel driver);
- parsing bugs in settings, history, insight or report files.

Out of scope:

- applications being grouped incorrectly, or numbers differing from Task Manager — please report these as
  normal bugs (see the FAQ in the README);
- attacks that require administrative rights or physical access, or that already own the machine;
- vulnerabilities in Windows, drivers or third-party applications.

## Design notes that help triage

- **No network access.** There is no HTTP client, socket, telemetry, analytics or auto-update code. If you find
  anything that transmits data, treat it as a security bug.
- **No elevation.** PerfScope runs as the current user, opens no services and installs no driver.
- **Local data only.** Data lives in `%LOCALAPPDATA%\PerfScope` (or the directory given with `--data-dir` /
  `PERFSCOPE_DATA_DIR`) as plain JSON, Markdown and binary history files; see README → *Data & Privacy*.
- **No third-party runtime dependencies.** Core and App use the BCL, WinForms and P/Invoke only; the test
  project is the only place with NuGet packages (xunit).
