# Provider disable/drain failure ownership — 2026-10-01

Implementation only; no builds, tests, probes, staging or game launches.
The previous goal turn reconciled active source and scoped evidence into the
current feature ledger.

## Source findings

The FSR3 native disable already waits for proxy presents and clears registered UI,
but it dropped swapchain/effect ownership links even when configure/wait failed.
Managed Reset warned and then retired the effect and deleted input images anyway.
Native effect destruction likewise continued after a failed disable. Streamline
suspension and swapchain replacement ignored their SDK options/tag return codes.
XeSS StopXessPresenter already clears its submit gate and disposes/drains its
presenter before restoring the Vulkan swapchain; that route is unchanged.

## Corrections

- FSR3 disable retains context/swapchain links until configure, wait and UI-clear
  succeed. Effect destruction returns on disable/destroy failure, retaining the
  native context. Managed Dispose clears its handle only after success.
- Managed reset/suspension throws on failed FSR3 disable/drain before retiring
  resources. This preserves the session's input/effect ownership for diagnosis or
  retry, instead of continuing presentation with stale resource references.
- Streamline suspension checks frame-tag invalidation and Off options. Swapchain
  replacement checks Off before retiring/replacing the old chain.
- Native Streamline Off for an unused/already-disabled feature succeeds even when
  the optional FG function is unavailable. Real On/Off SDK calls still propagate
  errors, preserving runtime-optional provider behavior on other hardware.

SDK calls, formats, algorithms and successful-path sequencing are retained.
No provider ABI/export changes. These corrections are unbuilt/unrun; fresh FSR3
and Streamline bridges are required for the next matching payload. Prior stages
and ZIPs contain older bridges. SDK failure branches and full provider/renderer/
SDL acceptance remain open; successful prior captures do not prove these paths.
