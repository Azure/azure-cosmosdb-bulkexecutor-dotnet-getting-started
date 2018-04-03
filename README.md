<img src="https://raw.githubusercontent.com/dennyglee/azure-cosmosdb-spark/master/docs/images/azure-cosmos-db-icon.png" width="75">  &nbsp; Azure Cosmos DB BulkExecutor library for .NET
==========================================

The Azure Cosmos DB BulkExecutor library for .NET acts as an extension library to the [Cosmos DB .NET SDK](https://docs.microsoft.com/en-us/azure/cosmos-db/sql-api-sdk-dotnet) and provides developers out-of-the-box functionality to perform bulk operations in [Azure Cosmos DB](http://cosmosdb.com).

<details>
<summary><strong><em>Table of Contents</em></strong></summary>

* [Consuming the Microsoft Azure Cosmos DB BulkExecutor .NET library](#nuget)
* [Bulk Import API](#bulk-import-api)
  * [Configurable parameters](#bulk-import-configurations)
  * [Bulk import response object definition](#bulk-import-response)
  * [Getting started with bulk import](#bulk-import-getting-started)
  * [Performance of bulk import sample](bulk-import-performance)
  * [API implementation details](bulk-import-client-side)
* [Bulk Update API](#bulk-import-api)
  * [List of supported field update operations](#field-update-operations)
  * [Configurable parameters](#bulk-update-configurations)
  * [Bulk update response object definition](#bulk-update-response)
  * [Getting started with bulk update](#bulk-update-getting-started)
  * [Performance of bulk update sample](bulk-update-performance)
  * [API implementation details](bulk-update-client-side)
* [Performance tips](#additional-pointers)
* [Contributing & Feedback](#contributing--feedback)
* [Other relevant projects](#relevant-projects)

</details>

## Consuming the Microsoft Azure Cosmos DB BulkExecutor .NET library

This project includes samples, documentation and performance tips for consuming the BulkExecutor library. You can download the official public NuGet package from [here](https://www.nuget.org/packages/Microsoft.Azure.CosmosDB.BulkExecutor/1.0.0).

## Bulk Import API

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

### Configurable parameters

* *enableUpsert* : A flag to enable upsert of the documents if document with given id already exists - default value is false.
* *disableAutomaticIdGeneration* : A flag to disable automatic generation of id if absent in the document - default value is true.
* *maxConcurrencyPerPartitionKeyRange* : The maximum degree of concurrency per partition key range, setting to null will cause library to use default value of 20.
* *maxInMemorySortingBatchSize* : The maximum number of documents pulled from the document enumerator passed to the API call in each stage for in-memory pre-processing sorting phase prior to bulk importing, setting to null will cause library to use default value of min(documents.count, 1000000).
* *cancellationToken* : The cancellation token to gracefully exit bulk import.

### Bulk import response object definition

The result of the bulk import API call contains the following attributes:
* *NumberOfDocumentsImported* (long) : The total number of documents which were successfully imported out of the documents supplied to the bulk import API call.
* *TotalRequestUnitsConsumed* (double) : The total request units (RU) consumed by the bulk import API call.
* *TotalTimeTaken* (TimeSpan) : The total time taken by the bulk import API call to complete execution.
* *BadInputDocuments* (List\<object\>) : The list of bad-format documents which were not successfully imported in the bulk import API call. User needs to fix the documents returned and retry import. Bad-format documents include documents whose *id* value is not a string (null or any other datatype is considered invalid).

### Getting started with bulk import

* Initialize DocumentClient set to Direct TCP connection mode
```csharp
ConnectionPolicy connectionPolicy = new ConnectionPolicy
{
    ConnectionMode = ConnectionMode.Direct,
    ConnectionProtocol = Protocol.Tcp
};
DocumentClient client = new DocumentClient(
    new Uri(endpointUrl),
    authorizationKey,
    connectionPolicy)
```

* Initialize BulkExecutor
```csharp
IBulkExecutor bulkExecutor = new BulkExecutor(client, dataCollection);
await bulkExecutor.InitializeAsync();
```

* Call BulkImportAsync
```csharp
BulkImportResponse bulkImportResponse = await bulkExecutor.BulkImportAsync(
    documents: documentsToImportInBatch,
    enableUpsert: true,
    disableAutomaticIdGeneration: true,
    maxConcurrencyPerPartitionKeyRange: null,
    maxInMemorySortingBatchSize: null,
    cancellationToken: token);
```

You can find the complete sample application program consuming the bulk import API [here](https://github.com/Azure/azure-cosmosdb-bulkexecutor-dotnet-getting-started/blob/master/BulkImportSample/BulkImportSample/Program.cs) - which generates random documents to be then bulk imported into an Azure Cosmos DB collection. You can configure the application settings in *appSettings* [here](https://github.com/Azure/azure-cosmosdb-bulkexecutor-dotnet-getting-started/blob/master/BulkImportSample/BulkImportSample/App.config).

You can download the Microsoft.Azure.CosmosDB.BulkExecutor nuget package from [here](https://www.nuget.org/packages/Microsoft.Azure.CosmosDB.BulkExecutor/1.0.0).

### Performance of bulk import sample

Let us compare the performace of the bulk import sample [application](https://github.com/Azure/azure-cosmosdb-bulkexecutor-dotnet-getting-started/blob/master/BulkImportSample/BulkImportSample/) against a [multi-threaded application](https://github.com/Azure/azure-documentdb-dotnet/tree/master/samples/documentdb-benchmark) which utilizes point writes (CreateDocumentAsync API in DocumentClient)

Both the applications are run on a standard DS16 v3 Azure VM in East US against a Cosmos DB collection in East US with 1 million RU/s allocated throughput.

The bulk import sample is executed with *NumberOfDocumentsToImport* set to **25 million** and *NumberOfBatches* set to **25** (in *App.config*) and default parameters for the bulk import API. The multi-threaded point write application is set up with a *DegreeOfParallelism* set to 2000 (spawns 2000 concurrent tasks) which maxes out the VM's CPU.

We observe the following performance for ingestion of 25 million documents into a 1 million RU/s Cosmos DB collection:

| | Time taken (sec) | Writes/second | RU/s consumed |
| --- | --- | --- | --- |
| Bulk import API | 301 | 83136 | 426765 |
| Multi-threaded point write | 2431 | 10280 | 72481 |

As seen, we observe **8x** improvement in the write throughput using the bulk import API while providing out-of-the-box efficient handling of throttling, timeouts and transient exceptions - allowing easier scale-out by adding additional *BulkExecutor* client instances on individual VMs to achieve even greater write throughputs.

### API implementation details

When a bulk import API is triggered with a batch of documents, on the client-side, they are first shuffled into buckets corresponding to their target Cosmos DB partition key range. Within each partiton key range bucket, they are broken down into mini-batches and each mini-batch of documents acts as a payload that is committed transactionally.

We have built in optimizations for the concurrent execution of these mini-batches both within and across partition key ranges to maximally utilize the allocated collection throughput. We have designed an [AIMD-style congestion control](https://academic.microsoft.com/#/detail/2158700277?FORM=DACADP) mechanism for each Cosmos DB partition key range **to efficiently handle throttling and timeouts**.

These client-side optimizations augment server-side features specific to the BulkExecutor library which together make maximal consumption of available throughput possible.

------------------------------------------

## Bulk Update API

The bulk update (a.k.a patch) API accepts a list of update items - each update item specifies the list of field update operations to be performed on a document identified by an id and parititon key value.

```csharp
    Task<BulkUpdateResponse> BulkUpdateAsync(
        IEnumerable<UpdateItem> updateItems,
        int? maxConcurrencyPerPartitionKeyRange = null,
        int? maxInMemorySortingBatchSize = null,
        CancellationToken cancellationToken = default(CancellationToken));
```

* Definition of UpdateItem
```csharp
    class UpdateItem
    {
        public string Id { get; private set; }

        public string PartitionKey { get; private set; }

        public IEnumerable<UpdateOperation> UpdateOperations { get; private set; }

        public UpdateItem(
            string id,
            string partitionKey,
            IEnumerable<UpdateOperation> updateOperations)
        {
            this.Id = id;
            this.PartitionKey = partitionKey;
            this.UpdateOperations = updateOperations;
        }
    }
```

### List of supported field update operations

* Increment

Supports incrementing any numeric document field by a specific value
```csharp
class IncUpdateOperation<TValue>
{
    IncUpdateOperation(string field, TValue value)
}
```

* Set

Supports setting any document field to a specific value
```csharp
class SetUpdateOperation<TValue>
{
    SetUpdateOperation(string field, TValue value)
}
```

* Unset

Supports removing a specific document field along with all children fields
```csharp
class UnsetUpdateOperation
{
    SetUpdateOperation(string field)
}
```

* Array push

Supports appending an array of values to a document field which contains an array
```csharp
class PushUpdateOperation
{
    PushUpdateOperation(string field, object[] value)
}
```

* Array remove

Supports removing a specific value (if present) from a document field which contains an array
```csharp
class RemoveUpdateOperation<TValue>
{
    RemoveUpdateOperation(string field, TValue value)
}
```

**Note**: For nested fields, use '.' as the nesting separtor. For example, if you wish to set the '/address/city' field to 'Seattle', express as shown:
```csharp
    SetUpdateOperation<string> nestedPropertySetUpdate = new SetUpdateOperation<string>("address.city", "Seattle");
```

### Configurable parameters

* *maxConcurrencyPerPartitionKeyRange* : The maximum degree of concurrency per partition key range, setting to null will cause library to use default value of 20.
* *maxInMemorySortingBatchSize* : The maximum number of update items pulled from the update items enumerator passed to the API call in each stage for in-memory pre-processing sorting phase prior to bulk updating, setting to null will cause library to use default value of min(updateItems.count, 1000000).
* *cancellationToken* : The cancellation token to gracefully exit bulk update.

### Bulk update response object definition

The result of the bulk update API call contains the following attributes:
* *NumberOfDocumentsUpdated* (long) : The total number of documents which were successfully updated out of the ones supplied to the bulk update API call.
* *TotalRequestUnitsConsumed* (double) : The total request units (RU) consumed by the bulk update API call.
* *TotalTimeTaken* (TimeSpan) : The total time taken by the bulk update API call to complete execution.

### Getting started with bulk update

* Initialize DocumentClient set to Direct TCP connection mode
```csharp
ConnectionPolicy connectionPolicy = new ConnectionPolicy
{
    ConnectionMode = ConnectionMode.Direct,
    ConnectionProtocol = Protocol.Tcp
};
DocumentClient client = new DocumentClient(
    new Uri(endpointUrl),
    authorizationKey,
    connectionPolicy)
```

* Initialize BulkExecutor
```csharp
IBulkExecutor bulkExecutor = new BulkExecutor(client, dataCollection);
await bulkExecutor.InitializeAsync();
```

* Define the update items along with corresponding field update operations
```csharp
SetUpdateOperation<string> nameUpdate = new SetUpdateOperation<string>("Name", "UpdatedDoc");
UnsetUpdateOperation descriptionUpdate = new UnsetUpdateOperation("description");

List<UpdateOperation> updateOperations = new List<UpdateOperation>();
updateOperations.Add(nameUpdate);
updateOperations.Add(descriptionUpdate);

List<UpdateItem> updateItems = new List<UpdateItem>();
for (int i = 0; i < 10; i++)
{
    updateItems.Add(new UpdateItem(i.ToString(), i.ToString(), updateOperations));
}
```

* Call BulkUpdateAsync
```csharp
BulkUpdateResponse bulkUpdateResponse = await bulkExecutor.BulkUpdateAsync(
    updateItems: updateItems,
    maxConcurrencyPerPartitionKeyRange: null,
    maxInMemorySortingBatchSize: null,
    cancellationToken: token);
```

You can find the complete sample application program consuming the bulk update API [here](https://github.com/Azure/azure-cosmosdb-bulkexecutor-dotnet-getting-started/blob/master/BulkUpdateSample/BulkUpdateSample/Program.cs). You can configure the application settings in *appSettings* [here](https://github.com/Azure/azure-cosmosdb-bulkexecutor-dotnet-getting-started/blob/master/BulkUpdateSample/BulkUpdateSample/App.config).

In the sample application, we first bulk import documents and then bulk update all the imported documents to set the *Name* field to a new value and unset the *description* field in each document.

You can download the Microsoft.Azure.CosmosDB.BulkExecutor nuget package from [here](https://www.nuget.org/packages/Microsoft.Azure.CosmosDB.BulkExecutor/1.0.0).

### Performance of bulk update sample

When the given sample application is run on a standard DS16 v3 Azure VM in East US against a Cosmos DB collection in East US with **1 million RU/s** allocated throughput - with *NumberOfDocumentsToUpdate* set to **25 million** and *NumberOfBatches* set to **25** (in *App.config*) and default parameters for the bulk update API (as well as bulk import API), we observe the following performance for bulk update:
```csharp
Updated 25000000 docs @ 52778 update/s, 481734 RU/s in 473.6824773 sec
```

### API implementation details

The bulk update API is designed similar to bulk import - look at the implementation details of bulk import API for details.

------------------------------------------

## Performance tips

* For best performance, run your application **from an Azure VM in the same region as your Cosmos DB account write region**.
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

------------------------------------------
## Contributing & feedback

This project has adopted the [Microsoft Open Source Code of
Conduct](https://opensource.microsoft.com/codeofconduct/).  For more information
see the [Code of Conduct
FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact
[opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional
questions or comments.

See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidelines.

To give feedback and/or report an issue, open a [GitHub
Issue](https://help.github.com/articles/creating-an-issue/).

------------------------------------------

## Other relevant projects

* [Cosmos DB BulkExecutor library for Java](https://github.com/Azure/azure-cosmosdb-bulkexecutor-java-getting-started)