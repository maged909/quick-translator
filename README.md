# Quick Translator

A tiny, fast, frictionless translator that lives in the Windows system tray. Select text anywhere, hit a global hotkey, and get an instant translation you can copy and paste back — without breaking your flow.

Built as a **single native C# WinForms executable** (~75 KB) that compiles against the in-box .NET Framework — **no SDK, no runtime install, no API keys, and no AutoHotkey/PowerShell dependencies.** It idles at ~40 MB RAM.

## Why

I talk to people in other languages and study languages daily. Existing tools were too heavy or too slow — open a browser, paste, wait, copy back. The goal here was zero friction: **press a shortcut → translate → copy → close**, all from the keyboard, in under a second.

## Features

- **Global hotkey** (default `Ctrl+;`, fully rebindable, keyboard-layout independent via low-level scan-code hook)
  - Text selected → translate it instantly
  - Nothing selected → open a type-to-translate box
  - Window already open → copy the result and close
- **Multiple free translation engines** with automatic fallback: Google, MyMemory, Lingva, LibreTranslate
- **Offline OCR** — drag a box anywhere on screen and translate text from images, using the built-in Windows OCR engine (WinRT)
- **Text-to-speech** via offline Windows SAPI voices — read the source or translation aloud
- **Romanization** of translations (pinyin, romaji, etc.)
- **Alternative translations** surfaced as clickable chips (gendered forms, synonyms)
- **History** with search, favorites, and JSON export/import
- **Smart paste-on-close** — types the translation back into the app you were using
- **Per-mode default languages** (one pair for selected text, another for typing)
- **Accessibility**: dark / light / follow-system themes and adjustable text size
- **Runs on startup**, single-instance (named-pipe IPC), custom dark-titlebar UI

## Requirements

- Windows 10 / 11 (.NET Framework 4.x is built in)
- Internet connection for translation and TTS
- OCR uses the built-in Windows OCR engine; add extra languages via
  *Settings → Time & language → Language → Optional features*

## Build

No SDK required — compiles with the in-box .NET Framework C# compiler:

```cmd
build.cmd
```

This produces `QuickTranslator.exe`. The build references the per-namespace WinRT
metadata for offline OCR and the GAC `System.Speech` assembly for TTS (see `build.cmd`).

## Usage

| Action | Result |
|--------|--------|
| Select text + hotkey | Translate selection |
| Hotkey with nothing selected | Open the type box |
| Hotkey while window is open | Copy result & close |
| `Esc` | Hide |
| `Enter` | Translate |
| `Ctrl+Enter` | Copy & close |

The tray icon opens the main window (Translate / History / Settings tabs), where you
can switch engines, rebind the hotkey, toggle behaviours, and pick default languages.

## Technical notes

- Single-file `QuickTranslator.cs`, compiled to a `winexe` with `/codepage:65001`
  so the UTF-8 UI glyphs compile correctly.
- Offline OCR is done by referencing the per-namespace WinRT `.winmd` files directly
  and blocking on `IAsyncOperation` via `Completed` + a `ManualResetEventSlim`, since
  the union `Windows.winmd` / `System.Runtime.WindowsRuntime` facade isn't available.
- TTS uses `System.Speech` (SAPI) for instant offline playback with selectable voices.
- Custom owner-drawn dark UI: rounded buttons, inputs, listboxes, and a marquee loader,
  with DWM dark-titlebar and rounded-window attributes.

## License

MIT
