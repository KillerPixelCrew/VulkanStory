using Xunit;

// Many tests change process-wide OptimumConfig fields and restore them afterward.
// Running those test classes concurrently makes otherwise independent assertions race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
