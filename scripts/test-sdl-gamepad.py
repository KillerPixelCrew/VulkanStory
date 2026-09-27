#!/usr/bin/env python3
"""Exercise the packaged SDL3 gamepad ABI with a software-only controller."""

import argparse
import ctypes as c
from pathlib import Path


class VirtualJoystickDesc(c.Structure):
    _fields_ = [
        ("version", c.c_uint32),
        ("type", c.c_uint16),
        ("padding", c.c_uint16),
        ("vendor_id", c.c_uint16),
        ("product_id", c.c_uint16),
        ("naxes", c.c_uint16),
        ("nbuttons", c.c_uint16),
        ("nballs", c.c_uint16),
        ("nhats", c.c_uint16),
        ("ntouchpads", c.c_uint16),
        ("nsensors", c.c_uint16),
        ("padding2", c.c_uint16 * 2),
        ("button_mask", c.c_uint32),
        ("axis_mask", c.c_uint32),
        ("name", c.c_char_p),
        ("touchpads", c.c_void_p),
        ("sensors", c.c_void_p),
        ("userdata", c.c_void_p),
        ("update", c.c_void_p),
        ("set_player_index", c.c_void_p),
        ("rumble", c.c_void_p),
        ("rumble_triggers", c.c_void_p),
        ("set_led", c.c_void_p),
        ("send_effect", c.c_void_p),
        ("set_sensors_enabled", c.c_void_p),
        ("cleanup", c.c_void_p),
    ]


class VirtualSensorDesc(c.Structure):
    _fields_ = [("type", c.c_int), ("rate", c.c_float)]


def bind(lib, name, result, *args):
    fn = getattr(lib, name)
    fn.restype = result
    fn.argtypes = list(args)
    return fn


def drain_events(poll):
    event = c.create_string_buffer(128)  # sizeof(SDL_Event)
    types = []
    while poll(event):
        types.append(c.c_uint32.from_buffer(event).value)
    return types


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("native", type=Path, help="SDL3.dll or libSDL3.so from the build")
    parser.add_argument("--mapping-db", type=Path,
                        help="controller database; defaults to gamecontrollerdb.txt beside SDL3")
    args = parser.parse_args()
    lib = c.CDLL(str(args.native.resolve()))
    init = bind(lib, "SDL_InitSubSystem", c.c_bool, c.c_uint32)
    quit_subsystem = bind(lib, "SDL_QuitSubSystem", None, c.c_uint32)
    error = bind(lib, "SDL_GetError", c.c_char_p)
    attach = bind(lib, "SDL_AttachVirtualJoystick", c.c_uint32, c.POINTER(VirtualJoystickDesc))
    detach = bind(lib, "SDL_DetachVirtualJoystick", c.c_bool, c.c_uint32)
    open_joystick = bind(lib, "SDL_OpenJoystick", c.c_void_p, c.c_uint32)
    close_joystick = bind(lib, "SDL_CloseJoystick", None, c.c_void_p)
    open_gamepad = bind(lib, "SDL_OpenGamepad", c.c_void_p, c.c_uint32)
    close_gamepad = bind(lib, "SDL_CloseGamepad", None, c.c_void_p)
    get_gamepads = bind(lib, "SDL_GetGamepads", c.c_void_p, c.POINTER(c.c_int))
    free = bind(lib, "SDL_free", None, c.c_void_p)
    pump = bind(lib, "SDL_PumpEvents", None)
    poll = bind(lib, "SDL_PollEvent", c.c_bool, c.c_void_p)
    update = bind(lib, "SDL_UpdateGamepads", None)
    set_button = bind(lib, "SDL_SetJoystickVirtualButton", c.c_bool, c.c_void_p, c.c_int, c.c_bool)
    set_axis = bind(lib, "SDL_SetJoystickVirtualAxis", c.c_bool, c.c_void_p, c.c_int, c.c_int16)
    get_button = bind(lib, "SDL_GetGamepadButton", c.c_bool, c.c_void_p, c.c_int)
    get_button_label = bind(lib, "SDL_GetGamepadButtonLabel", c.c_int, c.c_void_p, c.c_int)
    get_axis = bind(lib, "SDL_GetGamepadAxis", c.c_int16, c.c_void_p, c.c_int)
    get_vendor = bind(lib, "SDL_GetGamepadVendor", c.c_uint16, c.c_void_p)
    get_product = bind(lib, "SDL_GetGamepadProduct", c.c_uint16, c.c_void_p)
    get_serial = bind(lib, "SDL_GetGamepadSerial", c.c_char_p, c.c_void_p)
    has_sensor = bind(lib, "SDL_GamepadHasSensor", c.c_bool, c.c_void_p, c.c_int)
    enable_sensor = bind(lib, "SDL_SetGamepadSensorEnabled", c.c_bool, c.c_void_p, c.c_int, c.c_bool)
    send_sensor = bind(lib, "SDL_SendJoystickVirtualSensorData", c.c_bool,
                       c.c_void_p, c.c_int, c.c_uint64, c.POINTER(c.c_float), c.c_int)
    get_sensor = bind(lib, "SDL_GetGamepadSensorData", c.c_bool,
                      c.c_void_p, c.c_int, c.POINTER(c.c_float), c.c_int)
    rumble = bind(lib, "SDL_RumbleGamepad", c.c_bool,
                  c.c_void_p, c.c_uint16, c.c_uint16, c.c_uint32)
    rumble_triggers = bind(lib, "SDL_RumbleGamepadTriggers", c.c_bool,
                           c.c_void_p, c.c_uint16, c.c_uint16, c.c_uint32)
    add_mappings = bind(lib, "SDL_AddGamepadMappingsFromFile", c.c_int, c.c_char_p)

    if not init(0x00002000):
        raise RuntimeError(f"SDL_InitSubSystem: {error()!r}")
    instance_id = 0
    joystick = None
    gamepad = None
    try:
        mapping_db = args.mapping_db or args.native.resolve().parent / "gamecontrollerdb.txt"
        if mapping_db.is_file():
            loaded = add_mappings(str(mapping_db.resolve()).encode("utf-8"))
            if loaded < 0:
                raise RuntimeError(f"SDL_AddGamepadMappingsFromFile: {error()!r}")
            print(f"SDL3 gamepad mappings: {loaded} loaded from {mapping_db}")
        elif args.mapping_db is not None:
            raise FileNotFoundError(mapping_db)
        rumble_calls = []
        trigger_calls = []
        callback_type = c.CFUNCTYPE(c.c_bool, c.c_void_p, c.c_uint16, c.c_uint16)

        @callback_type
        def on_rumble(_userdata, low, high):
            rumble_calls.append((low, high))
            return True

        @callback_type
        def on_trigger_rumble(_userdata, left, right):
            trigger_calls.append((left, right))
            return True

        desc = VirtualJoystickDesc()
        desc.version = c.sizeof(desc)  # SDL_INIT_INTERFACE
        desc.type = 1  # SDL_JOYSTICK_TYPE_GAMEPAD
        desc.vendor_id = 0x1234
        desc.product_id = 0x5678
        desc.naxes = 6
        desc.nbuttons = 15
        sensor = VirtualSensorDesc(2, 60.0)  # SDL_SENSOR_GYRO
        desc.nsensors = 1
        desc.sensors = c.cast(c.pointer(sensor), c.c_void_p)
        desc.rumble = c.cast(on_rumble, c.c_void_p)
        desc.rumble_triggers = c.cast(on_trigger_rumble, c.c_void_p)
        desc.button_mask = (1 << 15) - 1
        desc.axis_mask = (1 << 6) - 1
        desc.name = b"Optimum virtual gamepad probe"
        instance_id = attach(c.byref(desc))
        if not instance_id:
            raise RuntimeError(f"SDL_AttachVirtualJoystick: {error()!r}")
        pump()
        if 0x653 not in drain_events(poll):  # SDL_EVENT_GAMEPAD_ADDED
            raise AssertionError("SDL did not emit a gamepad-added event")
        count = c.c_int()
        ids = get_gamepads(c.byref(count))
        try:
            detected = bool(ids) and instance_id in c.cast(ids, c.POINTER(c.c_uint32 * count.value)).contents
        finally:
            if ids:
                free(ids)
        if not detected:
            raise AssertionError(f"virtual gamepad {instance_id} not enumerated")

        joystick = open_joystick(instance_id)
        gamepad = open_gamepad(instance_id)
        if not joystick or not gamepad:
            raise RuntimeError(f"opening virtual gamepad: {error()!r}")
        if get_vendor(gamepad) != 0x1234 or get_product(gamepad) != 0x5678:
            raise AssertionError("SDL gamepad identity did not match the virtual device")
        if get_button_label(gamepad, 0) not in range(9):
            raise AssertionError("SDL returned an invalid face-button label")
        get_serial(gamepad)  # Optional, but the exported function must be available.
        if not set_button(joystick, 0, True) or not set_axis(joystick, 1, -20000):
            raise RuntimeError(f"setting virtual input: {error()!r}")
        pump()
        drain_events(poll)
        update()
        if not get_button(gamepad, 0) or get_axis(gamepad, 1) != -20000:
            raise AssertionError("SDL gamepad button/axis state did not update")
        if not has_sensor(gamepad, 2) or not enable_sensor(gamepad, 2, True):
            raise AssertionError(f"virtual gyro could not be enabled: {error()!r}")
        gyro = (c.c_float * 3)(0.25, -0.5, 0.0)
        if not send_sensor(joystick, 2, 1, gyro, 3):
            raise RuntimeError(f"sending virtual gyro data: {error()!r}")
        pump()
        drain_events(poll)
        observed = (c.c_float * 3)()
        if not get_sensor(gamepad, 2, observed, 3) or abs(observed[0] - 0.25) > 0.001 or abs(observed[1] + 0.5) > 0.001:
            raise AssertionError("SDL gamepad gyro state did not update")
        if not rumble(gamepad, 1000, 2000, 70) or not rumble_triggers(gamepad, 3000, 0, 70):
            raise AssertionError(f"virtual gamepad rumble failed: {error()!r}")
        pump()
        if (1000, 2000) not in rumble_calls or (3000, 0) not in trigger_calls:
            raise AssertionError("virtual gamepad did not receive rumble callbacks")

        detached_id = instance_id
        if not detach(instance_id):
            raise RuntimeError(f"SDL_DetachVirtualJoystick: {error()!r}")
        instance_id = 0
        pump()
        if 0x654 not in drain_events(poll):  # SDL_EVENT_GAMEPAD_REMOVED
            raise AssertionError("SDL did not emit a gamepad-removed event")
        ids = get_gamepads(c.byref(count))
        try:
            if ids and detached_id in c.cast(ids, c.POINTER(c.c_uint32 * count.value)).contents:
                raise AssertionError("virtual gamepad remained enumerated after detach")
        finally:
            if ids:
                free(ids)
        print("SDL3 virtual gamepad: connect, button, axis, gyro, rumble, disconnect passed")
    finally:
        if gamepad:
            close_gamepad(gamepad)
        if joystick:
            close_joystick(joystick)
        if instance_id:
            detach(instance_id)
        quit_subsystem(0x00002000)


if __name__ == "__main__":
    main()
