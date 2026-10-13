The boundary candidate subtracts the exact union of permitted original face
domains sequentially on independent deep copies. It retains the original
complete finite cap, Near/Far bounds, start coverage and material budgets.
Non-finite area measurements reject rather than silently passing comparisons.

The complete start-material builder preserves every original native boundary:
polygon, circle, spline, mixed, expression/freeform and recursive islands.
It calls the existing SectionWire and original planar nesting validation, then
constructs each face with its explicit hole wires before the unit extrusion.
No 3D hole cutters or conversion to a narrower typed curve representation occur.

The new start helper is not wired into evaluation yet. It explicitly reverses
each native hole wire with the existing generated Shape.Reversed() value API.
Its owning native-wire face callback awaits the qualified PlanarWireFaces.Create
API; the SDK protects the original wires before any pcurve construction. It
must not be reported as implemented runtime behavior. A minimal frozen86
consumer compiled with exactly CS1501 for the two-wire-set overload.
The first two frozen revisions and all 144 new/99 original assertions remain
unchanged. No native candidate execution is permitted until the SDK hands the
lane back explicitly and actual consumers are idle.
