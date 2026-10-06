# Third-party notices

`r1ng-launcher` is an independent project. **No third-party source files or binaries are vendored or
copied into this repository.**

## Protocols and references

### Everything SDK / Everything IPC (MIT)

The file-search backend speaks Everything's documented IPC protocol: a `WM_COPYDATA` message carrying
a packed `EVERYTHING_IPC_QUERY2` payload (Unicode, `EVERYTHING_IPC_COPYDATA_QUERY2W`) sent to the
`EVERYTHING_TASKBAR_NOTIFICATION` window, answered with an `EVERYTHING_IPC_LIST2` reply.

- The structure layouts, message ids and flag values implemented in
  `src/R1ng.Launcher/Files/EverythingProtocol.cs` follow the publicly documented header of the
  Everything SDK, `Everything_IPC.h`, which is distributed under the MIT licence
  (Copyright (C) 2016 David Carpenter), https://www.voidtools.com/support/everything/sdk/.
- No SDK source or `Everything64.dll` binary is included or shipped. The implementation is an
  independent managed re-implementation of the documented wire format (hand-rolled byte packing
  rather than a struct mapping), and the launcher keeps working without any Everything binary
  installed.

### QuickLook interoperability (no code copied)

- Optional file previews use the current-user local named-pipe endpoint and command strings exposed
  by [QuickLook](https://github.com/QL-Win/QuickLook), as described in its
  [`PipeServerManager.cs`](https://github.com/QL-Win/QuickLook/blob/master/QuickLook/PipeServerManager.cs).
- QuickLook is GPL-3.0-or-later. No QuickLook source, binaries, preview plugins, or libraries are copied
  or bundled here; the launcher's managed IPC adapter is independently implemented and only sends
  a bounded preview request to the user's separately installed, running instance.

### UI/behaviour inspiration (no code copied)

- **EverythingToolbar** (MIT) — result-row anatomy, filter/scope chips and availability messaging were
  used as a behavioural/visual reference while designing the local UI.
- **PowerToys Run** (MIT) — hotkey-driven floating launcher interaction model and empty-query history
  were used as a behavioural reference.

Neither project's source files were copied; the launcher's own architecture, search pipeline, UI and
tests were written for this repository.

### Deliberately avoided

- CPOL-licensed shell context-menu implementations (for example `ShellContextMenu` ports) were
  intentionally not used; the launcher ships its own first-party actions (open / open location / copy
  path / pin / hide / preview).
- The `tool_r1ng` sources are unlicensed; they were read as a behaviour reference only and no code was
  copied from them (the pinyin-initial resolver here is an independent GB2312-order implementation).
- The `r1ng-toolbar` repository is an independent fork that is not linked or referenced by this
  project.
