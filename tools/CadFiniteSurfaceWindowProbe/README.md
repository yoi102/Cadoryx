# Finite-surface extrusion window observer

This auxiliary WPF host opens one owned synthetic document and observes the
production creation/edit dialog. It does not inject input. Supply a frozen
candidate WPF output; the host uses its application assemblies, language
resources, native runtime, and production worker executable.

```powershell
dotnet build tools/CadFiniteSurfaceWindowProbe/WindowHost.csproj -c Release -p:CandidatePayload=C:/absolute/frozen/workspace/Cadoryx.wpf/bin/Release/net10.0-windows/win-x64
```

Run `WindowHost.exe --d6-newbody-ui-smoke <fresh-output> <exact-fixture>
<en-US|zh-CN|ja-JP> <confirm|cancel|close> <create|edit>` from its build output.
An existing output directory is rejected. For interactive acceptance, notify
the user before input and operate only this task's owned window.

Keep the original 36-case matrix, six required control states, and 30/300-second
budgets. A successful build or modal startup is not a passed case. The host
requires explicit source inventory, missing-limit rejection, near preview,
far invalidation and rejection, then restored near preview before the planned
closing route. Confirm additionally checks commit, undo/redo, exact persistence,
reopen, zero retained assets, binding errors, and actual native module identity.

The October 13 revision includes `Cadoryx.wpf.exe`, its dependency manifest and
runtime configuration, which the isolated production kernel requires. It also
avoids assigning `Application.ResourceAssembly` after WPF initializes it.
The actual first revised case was cancelled with an incomplete control trace;
see the retained [startup and trace record](../../capability-evidence/diagnostics/af1074-public85/window-startup-and-trace-verification.json).
The current Computer Use surface exposed only the owner window; subsequent
menu/keyboard input did not reliably target the modal. Do not change the product
dialog, relax its guards, or count startup as acceptance to work around this.
