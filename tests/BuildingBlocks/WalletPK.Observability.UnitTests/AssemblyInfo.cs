// Serilog's request logging writes through the process-wide Log.Logger, so tests that each
// start their own app must not run at the same time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
