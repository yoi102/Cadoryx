# Explicit analytic branch candidate

This isolated candidate builds the original frozen 1081 kernel sources with one
durable finite-surface overlay and a private analytic branch-domain helper.
It keeps original face identity checks, complete source/profile/finite cap
coverage, exact validation, geometry bounds, failure atomicity and explicit
reselection. Construction and coverage use independent deep copies; TopoDS
Clone alone retains shared TShape. Volume metadata uses the existing adaptive
measurement API; no geometry fitting or tolerance increase is used.

The product's original selected face stays authoritative. An exact box in the
recognized sphere/cylinder branch frame restricts a private copy to Near/Far;
the original finite domain must cover every complete cap. A missing requested
half, tangency, invalid direction or incomplete coverage remains a rejection.

The 144 additional authored cases comprise 108 complete full-face positive
cases (sphere/cylinder, three placements, plain/hole/island, Fuse/Cut/NewBody,
Near/Far), and 36 missing-half rejection cases. Positive cases include original
author identities, independent volume, strict validity/self-interference,
material components, undo/redo and save/reopen with a fresh asset store.
The original 6830 test sources are linked unchanged. The existing raw-native
Far90 controls, including their 18 failures, stay separate and unchanged.

Supply CandidateSource (frozen workspace), CandidatePayload (its WPF Release
directory), and CandidateTests (its original Release test directory) to build
BranchTests.csproj. Both projects target the original net10.0. They reference
frozen DLLs and do not restore another SDK version or rebuild the application.
All output must go into fresh artifact directories. Baseline test copies use
the original shipped kernel; the candidate uses its rebuilt kernel. Serial
native validation must complete before product integration. Current compiler
and runtime evidence must be reported separately; this tool is not a claim of
D7, arbitrary surfaces, topology history or A-F completion.
