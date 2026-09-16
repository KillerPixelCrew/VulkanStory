---
name: run-optimum
description: Build, deploy, launch, stop and screenshot the Optimum Vintage Story client on Vulkan or OpenGL, and confirm from the log which renderer actually started. Use for any "run it", "check in game", "compare backends" request.
---

# Run Optimum and verify what is on screen

1. Deploy: `make deploy` (Cecil patch, copies DLLs, shaders and the Vulkan backend into
   `.vanilla/win-x64/vintagestory`). If only the backend changed: `dotnet build Optimum.Render.Vulkan -c Release && cp bin/Release/net10.0/Optimum.Render.Vulkan.dll .vanilla/win-x64/vintagestory/`.
2. Stop any running client first: `scripts/dev/kill-client.sh` (its own call; no launch text in the same command).
3. Launch: `RENDERER=vulkan scripts/dev/run-client.sh "serene cave world"` (or `RENDERER=opengl`).
   Diagnostics go in the environment: `OPTIMUM_VULKAN_VALIDATION=1 OPTIMUM_RENDER_TRACE=/tmp/t.log`.
4. Wait for the world: poll the log for `[Client Chat] Welcome` (the player is in the world; "Savegame
   loaded" and "AssetsFinalize" come 20 s earlier while the loading screen is still up), then sleep 8 s
   before any input. Never blind-sleep.
5. **Confirm the renderer:** `scripts/dev/client-renderer.sh`. If it says `OpenGL renderer: <reason>`,
   the Vulkan probe failed; read the reason (stale `Optimum.Render.Vulkan.dll` beside the client is the
   classic one) and fix that before judging pixels.
6. Screenshot: `scripts/dev/screenshot.sh /tmp/vulkan.png`, then Read the PNG and describe what you see.
   For a backend comparison take both shots from the same save and camera.
6b. Daylight for comparable screenshots: focus the window (`xdotool windowactivate --sync $(xdotool search --name "Vintage Story" | tail -1)`), then per command `xdotool key t`, **sleep 1.5 s** (the chat box must be open before typing or the letters become hotkeys: "e" opens the inventory and the first letters are cut off), `xdotool type --delay 100 "<cmd>"`, sleep 0.8, `xdotool key Return`, sleep 2. Verify from the log: `grep "\[Server Chat\]"` shows what really arrived. (chat opens with T, sends with Enter). Commands: `/time set 12:00`, `/weather set clearsky`, `/weather setprecip -1`, `/weather setw still` (wind sway otherwise reads as jitter). Wait 3 s before the screenshot. Chat lines "A heavy temporal storm is imminent" mean the screen will warp soon; judge before it starts.
6c. When the run is for the USER to judge, leave it open and say so; close it only when they answer. When it is your own check, close it immediately.
7. Stop: `scripts/dev/kill-client.sh` immediately after the check; the user does not want it left running. Restore `ModConfig/optimum.json` `Renderer` to what the user had.

Gotchas: `ssaa` 0.5 in clientsettings halves the render resolution on both backends; the random
`--rndWorld -p creativebuilding` world is superflat and has no animals; passing `world.vcdbs` to `-o`
creates a new world named `world.vcdbs.vcdbs`.

Runs FOR THE USER ("run it for me"): launch, confirm the renderer from the log, say so, and hands off.
No chat commands, no xdotool, MangoHud stays at the user's global setting (their Vulkan indicator).
Scene setup and `MANGOHUD=0` are only for my own measurements with nobody at the keyboard.
