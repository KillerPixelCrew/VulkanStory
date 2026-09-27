# Controller mappings

`gamecontrollerdb.txt` is pinned from
[SDL_GameControllerDB](https://github.com/mdqinc/SDL_GameControllerDB) commit
`4a435090d98dce8f8db370067a8ebc8b7835cd0d`. Its license is kept beside it.
SHA-256: `385cc4cfcf214225c8337623a6874bb40a51028b58071100cca545b1e4c4dfd1`.

SDL loads the bundled database before scanning gamepads. A user's
`ModConfig/gamecontrollerdb.txt` loads afterward and can replace individual
device mappings without changing the package.
