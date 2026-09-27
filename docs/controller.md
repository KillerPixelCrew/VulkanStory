# Controller controls and settings

The Vulkan client uses SDL3 for its window, input and gamepads by default.
Connect a controller and start the game once to create
`ModConfig/optimum-controllers.json` in your [Vintage Story data path](../README.md#settings).
Edit the file with the game closed, then restart. A new controller gets a copy of
`Default` under `Devices`; later changes to `Default` do not overwrite that device's
entry. Controllers with a reported serial get separate profiles. Otherwise,
controllers of the same model share one profile.
When two pads are connected, pressing a button or moving a stick decisively on
the other pad makes it active; each pad keeps its own profile. Idle stick noise
does not switch devices, and background controller activity is ignored.

The package loads a pinned community `gamecontrollerdb.txt` before scanning
controllers. Put an updated `gamecontrollerdb.txt` in the data path's `ModConfig`
folder to override individual device mappings; it loads after the bundled file.
Restart the client after changing that file.

| Control | Default action |
|---|---|
| Left stick | Walk (directional input) |
| Right stick | Look in the world; move the menu cursor |
| D-pad | Navigate a focused inventory grid; otherwise snap to the next widget or slot, with a cursor step fallback |
| South / A | Jump in the world; left-click the selected slot or menu widget |
| East / B, Start | Escape / back |
| Back / View | Open or close the in-game controller settings panel |
| West / X | Drop item |
| North / Y | Inventory |
| Left and right stick clicks | Sneak and sprint in the world; Shift and Ctrl modifiers in menus |
| Triggers | Left and right mouse actions, including inventory clicks |
| Shoulders | Previous and next hotbar slot |

The generated file exposes `MoveDeadzone`, `MovePressThreshold`, and
`MoveReleaseThreshold` for the left stick. The separate press/release thresholds
avoid direction flicker in digital fallback; bundled single-player analog walking
starts just beyond the configured deadzone. `LookDeadzone`, `LookSensitivity`,
`LookCurveExponent`, and `InvertLookY` control camera movement;
`CursorSensitivity` controls the menu cursor. `TriggerThreshold` controls both
triggers. `MoveXAxis`, `MoveYAxis`, `LookXAxis`, `LookYAxis`, and the two
`*TriggerAxis` fields select any of SDL's six standard
[gamepad axes](https://wiki.libsdl.org/SDL3/SDL_GamepadAxis). The Axes page in the
controller panel cycles each role through those axes and selects its active direction.
Stick axes use both positive and negative halves; physical trigger axes only report
0 to 1, so mapping one to movement provides one direction. The `*Button` fields
use SDL [gamepad button](https://wiki.libsdl.org/SDL3/SDL_GamepadButton) numbers.
Invalid axis or button ranges fall back to defaults when loaded.

Set `GyroEnabled` to `true` for a controller with a gyroscope. By default,
`GyroRequireSecondaryTrigger` keeps the sensor off until the secondary trigger
is held while looking around the world; set it to `false` for continuous gyro
aiming. `GyroSensitivity` is mouse pixels per radian of controller rotation,
`GyroDeadzone` filters small rates in radians per second, and `InvertGyroX` /
`InvertGyroY` change direction. SDL reports pitch and yaw rates on its X and Y
sensor axes; these settings are separate from right-stick look. The sensor is
turned off in menus, when focus is lost, and when the controller disconnects.

`RumbleEnabled` and `TriggerRumbleEnabled` are off by default. Set either to
`true` for a short pulse when a controller click or inventory slot activation
begins. `RumbleStrength`, `TriggerRumbleStrength`, and `RumbleDurationMs` set its
intensity and duration. Unsupported rumble modes simply remain inactive; trigger
rumble is available on fewer controllers than whole-pad rumble.

The left stick still sets the game's directional key flags for actions and fallback.
With a compatible server on foot, a three-byte player-entity packet also carries
bounded tilt magnitude and signed X/Y axes; client and server use those axes for
continuous movement direction. The client enables this only after the server
acknowledges a versioned capability probe. Physical keyboard movement stays at full
speed, and older multiplayer servers retain digital movement. The menu cursor
uses the pinned game's active GUI bounds to snap to widgets and slots; a new or
modded GUI without those bounds gets a fixed cursor step. A physical handheld
test is still needed for the built-in controller, Steam Input,
touch, gyro orientation, and display scaling.

The in-game panel lets you change movement/look deadzones, look and menu-cursor
speed, gyro sensitivity, and gyro/rumble switches. It also controls whether gyro
requires the right trigger and whether trigger rumble is enabled. D-pad left/right
adjusts a focused slider. Its Actions and More pages remap the ten controller button actions:
choose an action, release the buttons used to open capture, then press the new
button. Hold physical Start and Back together to cancel capture. Face-button
labels follow SDL's reported controller layout. More also has a left/right
trigger swap. Axes assigns movement, look, and mouse-button trigger axes and their
positive/negative directions; the remaining profile fields still use the JSON file.
If the new button already belongs to another
listed action, the two bindings swap.

More also offers controller-specific toggle sneak and the game's existing
toggle-sprint setting. Toggle sneak clears when the menu opens, focus is lost,
or the controller disconnects; in menus, the same stick click remains a held
Shift modifier for inventory actions.

Keyboard and controller holds can overlap: releasing one source leaves an action
held until the other source also releases it. This includes mouse buttons.
In inventories, D-pad selection moves the cursor onto the highlighted slot;
South or the left trigger uses the normal left-click path, and the right trigger
uses the normal right-click path. Hold the left stick click for Shift-click
actions such as moving a stack.
