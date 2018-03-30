<img src="https://raw.githubusercontent.com/dennyglee/azure-cosmosdb-spark/master/docs/images/azure-cosmos-db-icon.png" width="75">  &nbsp; Azure Cosmos DB BulkExecutor library for .NET
==========================================

The Azure Cosmos DB BulkExecutor library for .NET acts as an extension library to the [Cosmos DB .NET SDK](https://docs.microsoft.com/en-us/azure/cosmos-db/sql-api-sdk-dotnet) and provides developers out-of-the-box functionality to perform bulk operations in [Azure Cosmos DB](http://cosmosdb.com).

<details>
<summary><strong><em>Table of Contents</em></strong></summary>

* [Bulk Import API](#bulk-import-api)
  * [API details](#bulk-import-api)
    * [Configurable parameters](#bulk-import-configurations)
    * [Bulk import response object definition](#bulk-import-response)
  * [Getting started](#bulk-import-getting-started)
  * [Performance of bulk import sample](bulk-import-performance)
  * [Client-side API implementation details](bulk-import-client-side)
* [Additional pointers](#additional-pointers)
* [Contributing & Feedback](#contributing--feedback)
* [Other relevant projects](#relevant-projects)

### Bulk Import API

#### API details

We provide two overloads of the bulk import API - one which accepts a list of JSON-serialized documents and the other a list of deserialized POCO documents.

* With list of JSON-serialized documents
```csharp
        Task<BulkImportResponse> BulkImportAsync(
            IEnumerable<string> documents,
            bool enableUpsert = false,
            bool disableAutomaticIdGeneration = true,
            int? maxConcurrencyPerPartitionKeyRange = null,
            int? maxInMemorySortingBatchSize = null,
            CancellationToken cancellationToken = default(CancellationToken));
```

* With list of deserialized POCO documents
```csharp
        Task<BulkImportResponse> BulkImportAsync(
            IEnumerable<object> documents,
            bool enableUpsert = false,
            bool disableAutomaticIdGeneration = true,
            int? maxConcurrencyPerPartitionKeyRange = null,
            int? maxInMemorySortingBatchSize = null,
            CancellationToken cancellationToken = default(CancellationToken));
```

##### Configurable parameters

* *enableUpsert* : A flag to enable upsert of the documents, default value is false.
* *disableAutomaticIdGeneration* : A flag to disable automatic generation of ids if absent in the docuement.
* *maxConcurrencyPerPartitionKeyRange* : The maximum degree of concurrency per partition key range, setting to null will cause library to use default value of 20.
* *maxInMemorySortingBatchSize* : The maximum number of documents pulled from the document enumerator passed to the API call in each stage for in-memory pre-processing sorting phase prior to bulk importing, setting to null will cause library to use default value of min(documents.count, 1000000).
* *cancellationToken* : The cancellation token to gracefully exit bulk import.

##### Bulk import response object definition

The result of the bulk import API call contains the following attributes:
* *NumberOfDocumentsImported* (long) : The total number of documents which were successfully imported out of the documents supplied to the bulk import API call.
* *TotalRequestUnitsConsumed* (double) : The total request units (RU) consumed by the bulk import API call.
* *TotalTimeTaken* (TimeSpan) : The total time taken by the bulk import API call to complete.
* *BadInputDocuments* (List\<object\>) : The list of bad-format documents which were not successfully imported in the bulk import API call. User needs to fix the documents returned and retry import. Bad-format documents include documents whose *id* value is not a string (null or any other datatype is considered invalid).

#### Getting started

You can find a sample application program consuming the bulk import API [here](https://github.com/Azure/azure-cosmosdb-bulkexecutor-dotnet-getting-started/blob/master/BulkImportSample/BulkImportSample/Program.cs) - which generates random documents to be then bulk imported into a Cosmos DB collecition. You can configure the application settings in *appSettings* [here](https://github.com/Azure/azure-cosmosdb-bulkexecutor-dotnet-getting-started/blob/master/BulkImportSample/BulkImportSample/App.config).

You can download the Microsoft.Azure.CosmosDB.BulkExecutor nuget package from [here](https://github.com/Azure/azure-cosmosdb-bulkexecutor-dotnet-getting-started/tree/master/BulkImportSample/BulkImportSample/NugetPackages).

#### Performance of bulk import sample

When the given sample application is run on a standard DS16 v3 Azure VM in East US against a Cosmos DB collection in East US with 1 million RU/s allocated througput - with *NumberOfDocumentsToImport* set to **25 million** and *NumberOfBatches* set to **25** (in *App.config*) and default parameters for the bulk import API, we observe the following performance:
```csharp
Inserted 25000000 docs @ 83136 writes/s, 426765 RU/s in 300.7134074 sec
```
#### Client-side API implementation details

When a bulk import API is triggered with a batch of documents, on the client-side, they are first shuffled into buckets corresponding to their target Cosmos DB partition key range. Within each partiton key range bucket, they are broken down into mini-batches - each mini-batch of documents acts as a payload that is committed transactionally

We have built in optimizations for the concurrent execution of these mini-batches both within and across partition key ranges to maximally utilize the allocated collection throughput. We have designed an [AIMD-style congestion control](https://academic.microsoft.com/#/detail/2158700277?FORM=DACADP) mechanism for each Cosmos DB partition key range **to efficiently handle throttling and timeouts**.

### Additional pointers

* For best performance, run your application from an Azure VM in the same region as your Cosmos DB account write region.
* It is advised to instantiate a single *BulkExecutor* object for the entirety of the application corresponding to a specific Cosmos DB collection.
* Since a single bulk operation API execution consumes a large chunk of the client machine's CPU and network IO by spawning multiple tasks internally, avoid spawning multiple concurrent tasks within your application process each executing bulk operation API calls. If a single bulk operation API call running on a single VM is unable to consume your entire collection's throughput (if your collections throughput > 1 million RU/s), preferably spin up separate VMs to concurrently execute bulk operation API calls.
* Ensure *InitializeAsync()* is invoked after instantiating a *BulkExecutor* object to fetch the target Cosmos DB collection partition map.
* In your application's *App.Config*, ensure **gcServer** is enabled for better performance
```csharp
  <runtime>
    <gcServer enabled="true" />
  </runtime>
```
* The library emits traces which can be collected either into a log file or on the console. To enable both, add the following to your application's *App.Config*.
```csharp
  <system.diagnostics>
    <trace autoflush="false" indentsize="4">
      <listeners>
        <add name="logListener" type="System.Diagnostics.TextWriterTraceListener" initializeData="application.log" />
        <add name="consoleListener" type="System.Diagnostics.ConsoleTraceListener" />
      </listeners>
    </trace>
  </system.diagnostics>
```

### Contributing & feedback

This project has adopted the [Microsoft Open Source Code of
Conduct](https://opensource.microsoft.com/codeofconduct/).  For more information
see the [Code of Conduct
FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact
[opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional
questions or comments.

See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidelines.

To give feedback and/or report an issue, open a [GitHub
Issue](https://help.github.com/articles/creating-an-issue/).

### Other relevant projects

* [Cosmos DB BulkExecutor library for Java](https://github.com/Azure/azure-cosmosdb-bulkexecutor-java-getting-started)