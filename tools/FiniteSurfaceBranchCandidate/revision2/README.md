Revision 2 retains the original 6974 discovered test assemblies, all 6830
original sources and the 144 new cases byte for byte. Only the candidate kernel
changes. The two configurations retain the frozen Release companion DLLs.

The first revision failed 75 of 243 focused cases in both configurations; all
99 existing finite-surface tests passed. The original kernel baseline failed
111 of the new 144 cases. These receipts remain authoritative failures.

This revision recovers recognized analytic coefficients at an interior UV
point, avoiding evaluations outside a selected finite half-face. Finite-surface
area/volume checks use the existing adaptive native integrator at 1e-11; all
geometry budgets and the complete boundary/domain checks remain unchanged.
Failure messages include the measured values for diagnosis. This is an
isolated candidate, not a product completion claim.
