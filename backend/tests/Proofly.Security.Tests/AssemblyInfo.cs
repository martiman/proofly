// Every test class owns a migrated schema and one or more hosts, each with a connection pool; running them one after the other
// keeps the shared proofly_test server below max_connections when other test projects run at the same time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
