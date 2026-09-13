#include "OcctSharp.Native.LocalFeatures.h"

_Static_assert(sizeof(OcctSharp_LocalFeatureInfo) == 56, "Local info ABI changed");
_Static_assert(sizeof(OcctSharp_LocalFeatureHistory) == 24, "History ABI changed");
typedef OcctSharp_Status (OCCTSHARP_CALL *BooleanHistoryCall)(
    const OcctSharp_ShapeHandle* const*, int32_t, int32_t, int32_t, OcctSharp_FeatureResultHandle**);
BooleanHistoryCall boolean_history_signature = &occtsharp_boolean_topology_history;
