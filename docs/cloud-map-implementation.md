# Cloud-map resource lifecycle

Date: 2026-09-30. Source implementation only; no builds, tests, probes,
packages or game runs. Tests remain deferred.

The cloud-map adapter now owns its framebuffer references and routes texture
creation, normalized-short uploads, two color attachments, point-light uniforms,
viewport/framebuffer queries and binding, state changes and resource cleanup.
It uses the retained normalized-short backend upload rather than copying signed
short bits into a different texture interpretation. Original tile generation,
weather updates, arrays and shader setup still execute.

The retained native cloud-map draw uses the original registered program, owned
mesh layout/topology, viewport, two-output formats and captured sampler inputs.
Missing native prerequisites use the stated renderer. The official assembly is
supplied by the profile owner; no fork assembly is referenced.

Original/incoming guards cover cleanup (5 GL calls), uploads (4), initialization
(8), rendering (14 plus one mesh draw) and typed replacement signatures. The
private texture helper is replaced as one operation. Before commit, wrappers
preserve ordinary GL behavior.

These code paths and guards have not been compiled or exercised. Entities/hands,
dense motion, shader-mode publication, complete scene composition and remaining
host factories still prevent startup activation. Cloud pixels/resource lifetime
and exceptional state restoration remain unaccepted in the rewritten host.
