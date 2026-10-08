using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Input;

// Adapter extraction from ClientPlatformWindows input patches at baseline
// 386e0d05386d0b228b439d09aeca851428f7bbf3. Source arbitration is retained.
/// <summary>Routes physical, touch, and reference-counted controller input into original game handlers without duplicate releases.</summary>
/// <remarks>Per-source held state prevents one source from releasing a key/button that another source still owns.</remarks>
internal sealed class GameInputBridge
{
    private readonly ClientPlatformWindows platform;
    private readonly GamePlatformBindings bindings;
    private readonly Action notePhysicalInput;
    private readonly System.Func<(float X, float Y)> cursorPosition;
    private readonly System.Func<float> wheelSensitivity;
    private HashSet<int>? physicalKeys;
    private Dictionary<int, int>? controllerKeys;
    private HashSet<EnumMouseButton>? physicalMouseButtons;
    private Dictionary<EnumMouseButton, int>? controllerMouseButtons;
    private HashSet<EnumMouseButton>? touchMouseButtons;

    /// <summary>Retains original handlers, shared mouse/wheel state, and callbacks for input ownership and cursor lookup.</summary>
    internal GameInputBridge(ClientPlatformWindows platform, GamePlatformBindings bindings,
        Action notePhysicalInput, System.Func<(float X, float Y)> cursorPosition, System.Func<float> wheelSensitivity)
    {
        this.platform = platform;
        this.bindings = bindings;
        this.notePhysicalInput = notePhysicalInput;
        this.cursorPosition = cursorPosition;
        this.wheelSensitivity = wheelSensitivity;
    }

    /// <summary>Whether a physical source currently holds any of the four supplied movement key codes.</summary>
    internal bool PhysicalMovementHeld(int forward, int backward, int left, int right) =>
        physicalKeys != null && (physicalKeys.Contains(forward) || physicalKeys.Contains(backward) ||
            physicalKeys.Contains(left) || physicalKeys.Contains(right));

    // Called after the controller mapper drops its actions on focus loss or shutdown.
    // Collapse any remaining source references and deliver only the final release.
    /// <summary>Collapses remaining controller references and emits final releases while preserving other sources' ownership.</summary>
    internal void ReleaseControllers()
    {
        if (controllerKeys != null)
        {
            foreach (int key in controllerKeys.Keys.ToArray())
            {
                controllerKeys[key] = 1;
                InjectControllerKey(new KeyEvent { KeyCode = key }, false);
            }
        }
        if (controllerMouseButtons != null)
        {
            foreach (EnumMouseButton button in controllerMouseButtons.Keys.ToArray())
            {
                controllerMouseButtons[button] = 1;
                InjectControllerMouseButton(button, false);
            }
        }
    }

	/// <summary>Updates physical key ownership and dispatches a transition unless controller input already holds the same key.</summary>
	public void InjectPhysicalKey(KeyEvent source, bool down)
	{
		int keyCode = source.KeyCode;
		if (keyCode <= 0) return;
		if (down)
		{
			notePhysicalInput();
			(physicalKeys ??= new HashSet<int>()).Add(keyCode);
			if (controllerKeys?.ContainsKey(keyCode) == true) return;
			if (platform.EllapsedMs - bindings.LastKeyUpMs <= 200) source.KeyCode2 = bindings.LastKeyUpKey;
		}
		else
		{
			bindings.LastKeyUpMs = platform.EllapsedMs;
			bindings.LastKeyUpKey = keyCode;
			if (physicalKeys?.Remove(keyCode) != true) return;
			if (controllerKeys?.ContainsKey(keyCode) == true) return;
		}
		foreach (KeyEventHandler handler in platform.keyEventHandlers)
		{
			var e = new KeyEvent
			{
				KeyCode = source.KeyCode,
				KeyCode2 = source.KeyCode2,
				AltPressed = source.AltPressed,
				CtrlPressed = source.CtrlPressed,
				ShiftPressed = source.ShiftPressed,
				CommandPressed = source.CommandPressed
			};
			if (down) handler.OnKeyDown(e); else handler.OnKeyUp(e);
		}
	}

	/// <summary>Delivers committed text as character events to every original key handler.</summary>
	public void InjectPhysicalText(string text)
	{
		foreach (char character in text)
		{
			foreach (KeyEventHandler handler in platform.keyEventHandlers)
				handler.OnKeyPress(new KeyEvent { KeyCode = character, KeyChar = character });
		}
	}

	/// <summary>Marks physical input activity, updates shared pixel coordinates, and forwards relative mouse motion.</summary>
	public void InjectPhysicalMouseMotion(float x, float y, float deltaX, float deltaY)
	{
		notePhysicalInput();
		bindings.SetMousePosition(x, y);
		foreach (MouseEventHandler handler in platform.mouseEventHandlers)
			handler.OnMouseMove(new MouseEvent((int)bindings.MouseX, (int)bindings.MouseY, (int)deltaX, (int)deltaY));
	}

	/// <summary>Updates shared pixel coordinates and forwards synthetic cursor motion without claiming physical input ownership.</summary>
	public void InjectControllerMouseMotion(float x, float y, float deltaX, float deltaY)
	{
		bindings.SetMousePosition(x, y);
		foreach (MouseEventHandler handler in platform.mouseEventHandlers)
			handler.OnMouseMove(new MouseEvent((int)bindings.MouseX, (int)bindings.MouseY, (int)deltaX, (int)deltaY));
	}

	/// <summary>Tracks a physical mouse transition at pixel coordinates, dispatching only when touch/controller ownership permits it.</summary>
	public void InjectPhysicalMouseButton(EnumMouseButton button, bool down, float x, float y)
	{
		if (button == EnumMouseButton.None) return;
		bindings.SetMousePosition(x, y);
		if (down)
		{
			notePhysicalInput();
			(physicalMouseButtons ??= new HashSet<EnumMouseButton>()).Add(button);
			if (controllerMouseButtons?.ContainsKey(button) == true ||
				touchMouseButtons?.Contains(button) == true) return;
		}
		else
		{
			if (physicalMouseButtons?.Remove(button) != true) return;
			if (controllerMouseButtons?.ContainsKey(button) == true ||
				touchMouseButtons?.Contains(button) == true) return;
		}
		foreach (MouseEventHandler handler in platform.mouseEventHandlers)
		{
			var e = new MouseEvent((int)bindings.MouseX, (int)bindings.MouseY, button, 0);
			if (down) handler.OnMouseDown(e); else handler.OnMouseUp(e);
		}
	}

	/// <summary>Marks physical input and applies the current sensitivity to a wheel amount at pixel coordinates.</summary>
	public void InjectPhysicalMouseWheel(float verticalAmount, float x, float y)
	{
		notePhysicalInput();
		bindings.SetMousePosition(x, y);
		float amount = verticalAmount * wheelSensitivity();

		InjectMouseWheel(amount);
	}

	/// <summary>Applies one synthetic wheel amount using the shared cumulative wheel position.</summary>
	public void InjectControllerMouseWheel(int direction) => InjectMouseWheel(direction);

	private void InjectMouseWheel(float amount)
	{
		bindings.Wheel += amount;
		foreach (MouseEventHandler handler in platform.mouseEventHandlers)
			handler.OnMouseWheel(new Vintagestory.API.Client.MouseWheelEventArgs
			{
				delta = (int)amount,
				deltaPrecise = amount,
				value = (int)bindings.Wheel,
				valuePrecise = bindings.Wheel
			});
	}

	/// <summary>Tracks touch-generated mouse ownership and forwards a transition only when another source does not hold it.</summary>
	public void InjectTouchMouseButton(EnumMouseButton button, bool down, float x, float y)
	{
		if (button == EnumMouseButton.None) return;
		bindings.SetMousePosition(x, y);
		if (down)
		{
			if (!(touchMouseButtons ??= new HashSet<EnumMouseButton>()).Add(button)) return;
		}
		else if (touchMouseButtons?.Remove(button) != true) return;
		if (physicalMouseButtons?.Contains(button) == true ||
			controllerMouseButtons?.ContainsKey(button) == true) return;
		foreach (MouseEventHandler handler in platform.mouseEventHandlers)
		{
			var e = new MouseEvent((int)bindings.MouseX, (int)bindings.MouseY, button, 0);
			if (down) handler.OnMouseDown(e); else handler.OnMouseUp(e);
		}
	}

	/// <summary>Acquires/releases one controller reference for a game key, emitting only the first press or final unshared release.</summary>
	public void InjectControllerKey(KeyEvent source, bool down)
	{
		int keyCode = source.KeyCode;
		if (down)
		{
			var counts = controllerKeys ??= new Dictionary<int, int>();
			counts.TryGetValue(keyCode, out int count);
			counts[keyCode] = count + 1;
			if (count > 0 || physicalKeys?.Contains(keyCode) == true) return;
		}
		else
		{
			if (controllerKeys == null || !controllerKeys.TryGetValue(keyCode, out int count)) return;
			if (count > 1)
			{
				controllerKeys[keyCode] = count - 1;
				return;
			}
			controllerKeys.Remove(keyCode);
			if (physicalKeys?.Contains(keyCode) == true) return;
		}
		foreach (KeyEventHandler handler in platform.keyEventHandlers)
		{
			var e = new KeyEvent
			{
				KeyCode = keyCode,
				AltPressed = source.AltPressed,
				CtrlPressed = source.CtrlPressed,
				ShiftPressed = source.ShiftPressed,
				CommandPressed = source.CommandPressed
			};
			if (down) handler.OnKeyDown(e); else handler.OnKeyUp(e);
		}
	}

	/// <summary>Acquires/releases a controller button reference and dispatches at the current cursor when source arbitration allows it.</summary>
	public void InjectControllerMouseButton(EnumMouseButton button, bool down)
	{
		if (down)
		{
			var counts = controllerMouseButtons ??= new Dictionary<EnumMouseButton, int>();
			counts.TryGetValue(button, out int count);
			counts[button] = count + 1;
			if (count > 0 || physicalMouseButtons?.Contains(button) == true ||
				touchMouseButtons?.Contains(button) == true) return;
		}
		else
		{
			if (controllerMouseButtons == null || !controllerMouseButtons.TryGetValue(button, out int count)) return;
			if (count > 1)
			{
				controllerMouseButtons[button] = count - 1;
				return;
			}
			controllerMouseButtons.Remove(button);
			if (physicalMouseButtons?.Contains(button) == true ||
				touchMouseButtons?.Contains(button) == true) return;
		}
		foreach (MouseEventHandler handler in platform.mouseEventHandlers)
		{
			var pos = cursorPosition();
			var e = new MouseEvent((int)pos.X, (int)pos.Y, button, 0);
			if (down) handler.OnMouseDown(e); else handler.OnMouseUp(e);
		}
	}

	/// <summary>Releases physical/touch held state on focus loss, preserves controller references, and notifies original focus state.</summary>
	public void InjectPhysicalFocusChanged(bool focused)
	{
		if (!focused)
		{
			if (physicalKeys != null)
			{
				foreach (int keyCode in physicalKeys)
				{
					if (controllerKeys != null && controllerKeys.ContainsKey(keyCode)) continue;
					foreach (KeyEventHandler handler in platform.keyEventHandlers)
						handler.OnKeyUp(new KeyEvent { KeyCode = keyCode });
				}
				physicalKeys.Clear();
			}
			if (physicalMouseButtons != null)
			{
				foreach (EnumMouseButton button in physicalMouseButtons)
				{
					if ((controllerMouseButtons != null && controllerMouseButtons.ContainsKey(button)) ||
						(touchMouseButtons?.Contains(button) == true)) continue;
					foreach (MouseEventHandler handler in platform.mouseEventHandlers)
						handler.OnMouseUp(new MouseEvent((int)bindings.MouseX, (int)bindings.MouseY, button, 0));
				}
				physicalMouseButtons.Clear();
			}
			if (touchMouseButtons != null)
			{
				foreach (EnumMouseButton button in touchMouseButtons)
				{
					if ((controllerMouseButtons?.ContainsKey(button) == true) ||
						(physicalMouseButtons?.Contains(button) == true)) continue;
					foreach (MouseEventHandler handler in platform.mouseEventHandlers)
						handler.OnMouseUp(new MouseEvent((int)bindings.MouseX, (int)bindings.MouseY, button, 0));
				}
				touchMouseButtons.Clear();
			}
		}
		bindings.NotifyFocus(focused);
	}
}
