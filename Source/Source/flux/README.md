# Kaiden Flux 0.3.4

Windows x64 mouse-acceleration configuration and aim testing. This build removes the Attack Shark/X11 hardware-control screen and its background USB scanning. The normal DPI/polling setup remains. The desktop copy supplied by the user is left unchanged; this build keeps the later dashboard fixes and existing native curve controls.

## Install

Close the old application, extract the complete ZIP into a new folder and launch KaidenFlux.exe. Keep writer.exe and the DLLs beside it. Existing setup, game profiles, curves and sessions stay under %LOCALAPPDATA%\KaidenFlux\. A compatible existing acceleration driver is reused. If a driver install is needed, use Setup and restart Windows afterward. The original signed driver is embedded, pinned and unchanged. Windows x64 and .NET Framework 4.7.2 or newer are required.

## Navigation

- Home: current curve, DPI/polling, active versus pending graph, Apply/Discard and Undo.
- Tuning: game profiles, curve editor, saved curves and sensitivity calculator.
- Practice: aim tests and AI assistant.
- Setup: manual DPI, configured polling, sensitivity, pad size and aiming style.

The styling uses subdued colors, flat panels and descriptive headings. Driver changes remain explicit previews, never automatic AI actions. Hardware DPI/polling programming, lighting, remapping and battery panels have been removed. Setup values normalize acceleration; they do not program the mouse sensor.

## Detailed curve controls

Off, Linear, Classic, Natural, Power, Jump and Synchronous use the existing native engine. Linear is Classic with exponent 2. Each mode exposes its corresponding numeric parameters. Linear/Classic output-cap and Natural curves also have drag handles. Advanced options include gain/velocity, input/output/both caps and combined/component X/Y calculation. Undo/Redo, Reset, Discard, Save and Apply remain available. LUT table editing is read-only. Profile/library loading preserves destination device configuration and protected fields; review the axis calculation setting separately.

## AI coaching

Use the AI assistant to request adjustments such as: "My game is Valorant, DPI 1600, sensitivity 0.14. Keep slow aiming precise and make fast flicks faster. Explain each parameter and suggest a comparison test."

The assistant can propose mode changes, offsets, acceleration, decay, caps, exponents, smoothing, synchronous parameters, native directional/domain/range scales, gain/velocity and combined/component axes. Exact changes are validated locally, compared with the complete current settings and rejected if stale or malformed. Device identities, driver installation, kernel settings and LUT arrays remain protected.

Game-sensitivity recommendations and a test plan are displayed separately. Flux does not change a game's settings. The assistant should confirm the game before suggesting a number and explain tradeoffs. DPI plus sensitivity cannot identify a universally perfect curve: compare repeatable tests, adjust gradually and judge actual in-game results.

## Saved key

When you enter an API key it is saved automatically to gemini-key.dat using Windows DPAPI encryption for your current account. It is loaded on subsequent launches. Use Forget saved key to remove it. It is not stored in plaintext, setup.json or tuning history; it cannot normally be decrypted under another Windows account. Key-save failures leave the key usable for the current session with a visible storage status. Environment-variable overrides remain supported when no saved key is available. Design previews never read or save credentials.

## Local memory

ai-memory.json stores up to 50 request/proposal/applied-change records on this PC, including bounded request text, explanations and change summaries. The most recent five summaries can accompany a request to Gemini, along with the current setup, selected game, current curve and summarized completed aim tests. Raw aim trajectories, USB identifiers and unrelated profiles stay local. Memory records exclude the API key. Saved history is shown when reopening the assistant. Local memory has On/Off and Clear local history controls. Off stops new memory records and suppresses historical context; Clear removes the stored history and displayed conversation. It is ordinary local JSON, so do not include sensitive personal material in tuning requests. Gemini requests still require internet; local history is not an offline language model.

## Reliability and verification

The setup loader handles malformed, unsupported-version and locked files without crashing, reports a recovery notice and leaves the existing file untouched. Setup saves validate first, use unique atomic staging and clean up temporary files. New profile/session/history files use versioned atomic JSON. Driver validation, stale-preview rejection, standalone writer validation and previous-successful-apply backups remain intact.

See TEST-RESULTS.txt and KNOWN-LIMITATIONS.txt for the actual checks and limits. Live Gemini/account access, privileged driver installation, reboot persistence and live driver activation are not claimed tested. Existing hardware tests are retained only as legacy source tests and are not included in the app's compiled hardware functionality.

## Source

Source includes the WPF C# app, existing native components and build/test scripts. Build using scripts/Build-Flux.ps1 with Roslyn csc.exe and the official native-release directory. Run scripts/Test-Flux.ps1 with the same compiler. The original MIT attribution and Newtonsoft notices are included. This is an independent project.
