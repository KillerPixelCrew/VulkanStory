# Controller dialog world/shutdown ownership — 2026-10-01

Implementation only; no builds, tests, probes, staging or game launches.
The preceding turn made progress with a clean graph/controller compile batch.

Source review found controller settings dialogs closed/dropped on replacement
but not unregistered, and retained settingsGame/dialog references after world
exit and controller shutdown. Added CloseSettings to detach fields, close the
dialog, unregister from its original client and dispose composers in finally.
Device replacement, world replacement and SdlGamepadInput.Dispose use this path.

WorldLeaving closes only settings/keyboard dialogs owned by the departing client,
cancels binding capture and releases controller actions. Both original client
disposal and queued ordinary LeaveWorld invoke it. Their existing newer-client
guards remain, so delayed old-world cleanup cannot clear a newer session's input.

Normal StopAndDrain already calls StopControllers before setting session.stopping
or disposing the device. ProcessRuntime removes routing only after session
disposal. Thus owned GUI texture releases occur while their graphics route is
available; this change does not move GUI destruction past GPU/device shutdown.
Preparation failure has no opened settings dialog. No graphics lifetime access
rules or input algorithms changed.

Source is unbuilt/unrun. Actual world rejoin, device loss and dialog shutdown
behavior remain unverified; full renderer/SDL/provider objective stays open.
