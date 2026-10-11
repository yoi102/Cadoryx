using Xunit;

namespace Cadoryx.Tests;

// These contracts time an owned worker from the initiating command. Other test
// collections can occupy the global parent native queue before it starts, so run
// deadline and cancellation fixtures without unrelated native queue contention.
// The elapsed-time assertions retain their original bounds.
[CollectionDefinition("IsolatedNativeDeadlines", DisableParallelization = true)]
public sealed class IsolatedNativeDeadlineCollection;
