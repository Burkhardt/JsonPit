using Xunit;

// CR003 §2/§8: the ordinary suite remains non-parallel. Tests touch process-global
// state (Os.Config, MasterFlagFile.TicketDuration, Pit.RecoveryDebounce, the
// process-wide canonical-path registry) and real configured cloud roots; product
// concurrency is proven by explicit concurrency tests, never by runner scheduling.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
