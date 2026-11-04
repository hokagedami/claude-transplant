# Contributing

- run `npm test` before a pull request with Node 22 or newer
- one source file per language, no code comments, names carry the meaning
- deletions beat additions, and a new file needs a reason the README can state
- keep the dead-ends table current when you learn why something fails
- claims about macOS or Claude behavior come with the Claude Code and Desktop versions you verified

Use a synthetic HOME for tests. App-control commands and cloud operations are mocked. Set `npm_config_offline=true npm_config_update_notifier=false` to prevent npm's update check.

Give each local suite a dedicated `TMPDIR` outside the checkout. Remove it after saving diagnostics so synthetic fixtures do not accumulate.

The October 11, 2026 disposable API test ran on a Mac with Claude Desktop 2.31226.1 and embedded Claude Code 2.1.295 installed. It verified archive, unarchive and final cleanup in another organization while Desktop stayed in its selected organization. The Code binary was not run for this test. Any repeat requires owner approval for one empty test session, with no prompts, workers or app actions. Automated coverage stays mocked.

When process inspection is denied, focused fixtures can preload an empty `/bin/ps` result and a not-running `/usr/bin/pgrep` result. Tests that enumerate real processes need `ps` access.

Run `swiftc -typecheck -parse-as-library menubar.swift`. The `@main` app needs library parsing.

On Windows, tests set `USERPROFILE` beside `HOME`, because Node reads the home folder from `USERPROFILE` there, and the Windows process query returns an empty table unless a test supplies one. The tray compiles with `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe -target:winexe -r:System.Web.Extensions.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll tray.cs`, which accepts C# 5 only. `tray.exe --describe <accounts.json>` prints its menu lines without starting the tray.
