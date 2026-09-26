# Third-party notices

This derivative is distributed under GNU AGPL v3. The full license is in LICENSE.

- **CustomResolution**, copyright its original authors including **0x0ade**.
  Source: https://git.0x0a.de/0x0ade/DP-CustomResolution
  Pinned revision: `f811343843601992efc0ebe0aefabc5ddeab0a90`.
  `src/RenderScale/States/GameSizeState.cs` derives from its gameplay rendering implementation.
- **FloppyUtils**, copyright its original authors including **0x0ade**.
  Source: https://git.0x0a.de/0x0ade/DP-FloppyUtils
  Pinned revision: `add4ed4515425489d0677997ed7465da1cf21a81`.
  The source snapshot and its license are included in `vendor/FloppyUtils`.
- The build references Dalamud, FFXIVClientStructs and graphics interop libraries provided by the user's Dalamud installation. These game/loader libraries are not included in the plugin package.
- SharpDX packages are transitive FloppyUtils dependencies (MIT license).

斯温 modifications (2026-09-26): Chinese gameplay-only interface, API 15 CN target,
version/hash gating, finite bounded custom ratios, measured-size confirmation,
preview rollback, disabled-state no-op guards, original-setting restoration,
resize cleanup, frame-delta ABI preservation, tests and packaging.

The plugin does not include CustomResolution's display/window/cursor/HUD resizing modules.
The author field identifies this derivative's maintainer; it does not claim original authorship of upstream rendering code.
