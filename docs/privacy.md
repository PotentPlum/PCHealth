# Privacy check

T-36 checks the network behavior the app actually shows against what it tells the user (spec §3.5, §9).

## What the app discloses

- First run: "Nothing leaves this PC. Checking for updates contacts GitHub only when you ask." (D-018)
- Updates page (D-010): checking reads the GitHub release list and installing downloads from GitHub. Nothing about the user's files is sent, GitHub sees the IP address, and the app never checks or downloads on its own.
- Settings page: lists everything the app stores, all of it in `%LocalAppData%\PCHealth`. Folder measurements are kept in memory only.

## How it is checked

Both checks run in CI on every push.

1. **Every app flow, in process.** `PrivacyTests` in `tests/PCHealth.App.Tests` listens to every event from the .NET networking stack: name resolution, socket connects, HTTP and TLS. While it listens, it runs:
   - startup with the real composition, including the Velopack update service;
   - the first-run discovery and every page;
   - "Measure again";
   - a quarantine move and its Undo through the gateway;
   - a settings change, and clearing local data.

   It expects no events at all. A control test makes a loopback connection and expects to see it, which proves the listener works. All files are fixtures in the temp folder.
2. **The real app, out of process.** `scripts/ci/observe-network.ps1` starts the built `PCHealth.App.exe` and leaves it untouched for 30 seconds. Every 200 ms it records the TCP connections and UDP endpoints the process owns. A control process holding a loopback connection must be seen first.
3. **Code.** No product project uses `HttpClient`, sockets, DNS or web requests. The only library that can reach the network is Velopack, and only through `CheckForUpdatesAsync` and `DownloadUpdatesAsync`. Those are called only from the two buttons on the Updates page.

## Result

**2026-10-22, CI run 39754673628 (commit 03ee6d8), GitHub Actions `windows-latest`: matches the disclosure.**

- `PrivacyTests`: no network events during any flow. The loopback control was seen.
- Launch of the real app: the control connection was seen; the main window "PC Health Monitor" was shown; no TCP connection or UDP endpoint was owned by the app in its first 30 seconds.

## Limits

- The in-process listener sees only .NET networking. Windows components the app calls are not covered by it: the Shell for the Recycle Bin, Restart Manager, and the registry for installed apps. The out-of-process check covers them only at startup, when none of them run.
- The out-of-process check polls, so a connection that opens and closes within 200 ms could be missed. The in-process listener has no such gap for .NET code.
- The update check itself was not exercised against GitHub here; its traffic is the disclosed exception. The first real check happens when v0.1.0 is installed (T-38). Watching the app in Resource Monitor then would confirm it contacts only GitHub.
