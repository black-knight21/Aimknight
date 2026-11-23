<p align="center">
  <img src="kb-logo.webp" alt="KaidenFlux logo" width="100" />
</p>

<h1 align="center">KaidenFlux</h1>

<p align="center">
  Windows mouse-acceleration configuration tool with curve editing and aim practice — built in C# on the Raw Accel driver.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-0.3.4-8b5cf6?style=flat-square" />
  <img src="https://img.shields.io/badge/platform-Windows%20x64-blue?style=flat-square" />
  <img src="https://img.shields.io/badge/language-C%23-239120?style=flat-square" />
  <img src="https://img.shields.io/badge/.NET-4.7.2%2B-512BD4?style=flat-square" />
</p>

---

## About

KaidenFlux is a personal Windows desktop project built to practise C# application development, GUI design, and driver integration. It extends the open-source Raw Accel driver with a custom interface for configuring mouse acceleration curves and running aim practice sessions.

This is a personal software project, not a commercial product.

---

## Features

- **Curve editor** — Linear, Classic, Natural, Power, Jump, and Synchronous acceleration modes with live graph preview
- **Aim practice** — Windowed aim tests with high-resolution timing and session comparisons
- **Game profiles** — Save and load per-game acceleration setups with a sensitivity calculator
- **Setup wizard** — Manual DPI, polling rate, sensitivity, pad size, and aiming style configuration
- **Undo / Redo** — Full change history with Reset, Discard, Save, and Apply controls

---

## Requirements

- Windows 10 or Windows 11 (x64)
- .NET Framework 4.7.2 or newer

---

## Getting Started

1. Extract the ZIP into a new folder
2. Launch `KaidenFlux.exe`
3. Keep `writer.exe` and the DLLs in the same folder
4. If a driver install is required, use the Setup screen and restart Windows

Existing settings, game profiles, curves, and sessions are stored under `%LOCALAPPDATA%\KaidenFlux\` and are preserved between versions.

---

## Navigation

| Tab | Description |
|---|---|
| **Home** | Current curve, DPI/polling, Apply/Discard, and Undo |
| **Tuning** | Game profiles, curve editor, saved curves, sensitivity calculator |
| **Practice** | Aim tests and session comparisons |
| **Setup** | Manual DPI, polling, sensitivity, pad size, and aiming style |

---

## Built With

- **C#** — WinForms application and core logic
- **C++ / C** — Native Raw Accel driver wrapper
- **Newtonsoft.Json** — JSON serialisation

Build using `scripts/Build-Flux.ps1` with Roslyn `csc.exe`.

---

## Licence

MIT. See [LICENSE](LICENSE) for details.  
Newtonsoft.Json licence: [LICENSE.Newtonsoft.Json.md](LICENSE.Newtonsoft.Json.md)

---

<p align="center">Personal project by <a href="https://black-knight21.github.io/Black-knight/">Black Knight</a></p>
