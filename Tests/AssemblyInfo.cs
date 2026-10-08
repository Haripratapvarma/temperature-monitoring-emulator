// Several tests assert on process-wide quantities (allocations, timing under load) and open real sockets,
// so the suite runs sequentially. It takes ~10 s.
#if !XUNIT_SHIM
[assembly: CollectionBehavior(DisableTestParallelization = true)]
#endif
