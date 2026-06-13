Quick Translator  (D:\More\GITranslator)
=========================================

A tiny, fast, frictionless translator that lives in the system tray.
One native .exe (compiled .NET Framework). No AutoHotkey, no PowerShell,
no API keys. Free translation engines.

FILES
-----
  QuickTranslator.exe   The whole app (~75 KB).
  QuickTranslator.cs    Source. Rebuild with build.cmd.
  build.cmd             Compiles with the in-box C# compiler (no SDK needed).
  Translator.ico        App / tray / window icon.
  Open Translator.lnk   Pin to taskbar (opens the Main window).
  settings.json         Your settings (created on first change).
  history.json          Saved history (only if History is enabled).

SHORTCUT  (rebindable in Settings; default Ctrl+; , layout independent)
-----------------------------------------------------------------------
  - Text selected      -> translate it
  - Nothing selected   -> open the type box
  - Window already open -> copy result & close
  Esc hides; Enter translates; Ctrl+Enter copies & closes.

QUICK WINDOW
------------
  - Source & result boxes both grow with the window.
  - Speaker buttons read the source / translation aloud (free Google TTS).
  - OCR (camera button): drag a box on screen, the text is extracted and translated.
  - Romanization of the translation is shown under it (e.g. pinyin / romaji).
  - From / Swap / To selectors; swap is smart with Auto-detect.
  - Alternative translations appear as clickable chips (gendered forms, synonyms).
  - Auto-copy (on by default) copies the result automatically.

MAIN WINDOW  (tray -> Open main window)
---------------------------------------
  Translate | History | Settings.
  History: search, mark favorites (right-click), export / import, clear.
  Settings:
    - Engine: Google / MyMemory / Lingva / LibreTranslate (auto-fallback if one fails).
    - Behaviour: auto-copy, keep history, paste-typed-translation-on-close,
      show alternatives & romanization, run on startup.
    - Default languages for "selected text" vs "typing" modes.
    - Shortcut: rebind the global hotkey.
    - Accessibility: theme (Dark / Light / Follow system) and text size.

REQUIREMENTS
------------
  Windows 10/11 (.NET Framework 4.x is built in). Internet for translation/TTS.
  OCR uses the built-in Windows OCR engine (install extra languages via
  Settings > Time & language > Language > Optional features if needed).
