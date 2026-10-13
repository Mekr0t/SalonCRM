namespace SalonCrm.Api.Tests;

// All integration test classes join this collection, so they share ONE ApiFactory
// (and therefore one PostgreSQL container) and run sequentially.
[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;
