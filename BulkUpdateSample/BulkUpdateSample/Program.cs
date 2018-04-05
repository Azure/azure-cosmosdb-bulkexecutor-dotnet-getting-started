//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace BulkUpdateSample
{
    using System;
    using System.Configuration;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Client;
    using Microsoft.Azure.CosmosDB.BulkExecutor;
    using Microsoft.Azure.CosmosDB.BulkExecutor.BulkImport;
    using Microsoft.Azure.CosmosDB.BulkExecutor.BulkUpdate;

    class Program
    {
        private static readonly string EndpointUrl = ConfigurationManager.AppSettings["EndPointUrl"];
        private static readonly string AuthorizationKey = ConfigurationManager.AppSettings["AuthorizationKey"];
        private static readonly string DatabaseName = ConfigurationManager.AppSettings["DatabaseName"];
        private static readonly string CollectionName = ConfigurationManager.AppSettings["CollectionName"];
        private static readonly int CollectionThroughput = int.Parse(ConfigurationManager.AppSettings["CollectionThroughput"]);

        private static readonly ConnectionPolicy ConnectionPolicy = new ConnectionPolicy
        {
            ConnectionMode = ConnectionMode.Direct,
            ConnectionProtocol = Protocol.Tcp
        };

        private DocumentClient client;

        /// <summary>
        /// Initializes a new instance of the <see cref="Program"/> class.
        /// </summary>
        /// <param name="client">The DocumentDB client instance.</param>
        private Program(DocumentClient client)
        {
            this.client = client;
        }

        static void Main(string[] args)
        {
            Trace.WriteLine("Summary:");
            Trace.WriteLine("--------------------------------------------------------------------- ");
            Trace.WriteLine(String.Format("Endpoint: {0}", EndpointUrl));
            Trace.WriteLine(String.Format("Collection : {0}.{1}", DatabaseName, CollectionName));
            Trace.WriteLine("--------------------------------------------------------------------- ");
            Trace.WriteLine("");

            try
            {
                using (var client = new DocumentClient(
                    new Uri(EndpointUrl),
                    AuthorizationKey,
                    ConnectionPolicy))
                {
                    var program = new Program(client);
                    program.RunBulkImportAndUpdateAsync().Wait();
                }
            }
            catch (AggregateException e)
            {
                Trace.TraceError("Caught AggregateException in Main, Inner Exception:\n" + e);
                Console.ReadKey();
            }
        }

        /// <summary>
        /// Driver function for bulk import.
        /// </summary>
        /// <returns></returns>
        private async Task RunBulkImportAndUpdateAsync()
        {
            // Cleanup on start if set in config.

            DocumentCollection dataCollection = null;
            try
            {
                if (bool.Parse(ConfigurationManager.AppSettings["ShouldCleanupOnStart"]))
                {
                    Database database = Utils.GetDatabaseIfExists(client, DatabaseName);
                    if (database != null)
                    {
                        await client.DeleteDatabaseAsync(database.SelfLink);
                    }

                    Trace.TraceInformation("Creating database {0}", DatabaseName);
                    database = await client.CreateDatabaseAsync(new Database { Id = DatabaseName });

                    Trace.TraceInformation(String.Format("Creating collection {0} with {1} RU/s", CollectionName, CollectionThroughput));
                    dataCollection = await Utils.CreatePartitionedCollectionAsync(client, DatabaseName, CollectionName, CollectionThroughput);
                }
                else
                {
                    dataCollection = Utils.GetCollectionIfExists(client, DatabaseName, CollectionName);
                    if (dataCollection == null)
                    {
                        throw new Exception("The data collection does not exist");
                    }
                }
            }
            catch (Exception de)
            {
                Trace.TraceError("Unable to initialize, exception message: {0}", de.Message);
                throw;
            }

            // Prepare for bulk import.

            // Creating documents with simple partition key here.
            string partitionKeyProperty = dataCollection.PartitionKey.Paths[0].Replace("/", "");

            long numberOfDocumentsToGenerate = long.Parse(ConfigurationManager.AppSettings["NumberOfDocumentsToUpdate"]);
            int numberOfBatches = int.Parse(ConfigurationManager.AppSettings["NumberOfBatches"]);
            long numberOfDocumentsPerBatch = (long)Math.Floor(((double)numberOfDocumentsToGenerate) / numberOfBatches);

            // Set retry options high for initialization (default values).
            client.ConnectionPolicy.RetryOptions.MaxRetryWaitTimeInSeconds = 30;
            client.ConnectionPolicy.RetryOptions.MaxRetryAttemptsOnThrottledRequests = 9;

            IBulkExecutor bulkExecutor = new BulkExecutor(client, dataCollection);
            await bulkExecutor.InitializeAsync();

            // Set retries to 0 to pass control to bulk executor.
            client.ConnectionPolicy.RetryOptions.MaxRetryWaitTimeInSeconds = 0;
            client.ConnectionPolicy.RetryOptions.MaxRetryAttemptsOnThrottledRequests = 0;

            BulkImportResponse bulkImportResponse = null;
            long totalNumberOfDocumentsInserted = 0;
            double totalRequestUnitsConsumed = 0;
            double totalTimeTakenSec = 0;

            var tokenSource = new CancellationTokenSource();
            var token = tokenSource.Token;

            for (int i = 0; i < numberOfBatches; i++)
            {
                // Generate JSON-serialized documents to import.

                List<string> documentsToImportInBatch = new List<string>();
                long prefix = i * numberOfDocumentsPerBatch;

                Trace.TraceInformation(String.Format("Generating {0} documents to import for batch {1}", numberOfDocumentsPerBatch, i));
                for (int j = 0; j < numberOfDocumentsPerBatch; j++)
                {
                    string partitionKeyValue = (prefix + j).ToString();
                    string id = partitionKeyValue;

                    documentsToImportInBatch.Add(Utils.GenerateRandomDocumentString(id, partitionKeyProperty, partitionKeyValue));
                }

                // Invoke bulk import API.

                var tasks = new List<Task>();

                tasks.Add(Task.Run(async () =>
                {
                    Trace.TraceInformation(String.Format("Executing bulk import for batch {0}", i));
                    do
                    {
                        try
                        {
                            bulkImportResponse = await bulkExecutor.BulkImportAsync(
                                documents: documentsToImportInBatch,
                                enableUpsert: true,
                                disableAutomaticIdGeneration: true,
                                maxConcurrencyPerPartitionKeyRange: null,
                                maxInMemorySortingBatchSize: null,
                                cancellationToken: token);
                        }
                        catch (DocumentClientException de)
                        {
                            Trace.TraceError("Document client exception: {0}", de);
                            break;
                        }
                        catch (Exception e)
                        {
                            Trace.TraceError("Exception: {0}", e);
                            break;
                        }
                    } while (bulkImportResponse.NumberOfDocumentsImported < documentsToImportInBatch.Count);

                    Trace.WriteLine(String.Format("\nSummary for batch {0}:", i));
                    Trace.WriteLine("--------------------------------------------------------------------- ");
                    Trace.WriteLine(String.Format("Inserted {0} docs @ {1} writes/s, {2} RU/s in {3} sec",
                        bulkImportResponse.NumberOfDocumentsImported,
                        Math.Round(bulkImportResponse.NumberOfDocumentsImported / bulkImportResponse.TotalTimeTaken.TotalSeconds),
                        Math.Round(bulkImportResponse.TotalRequestUnitsConsumed / bulkImportResponse.TotalTimeTaken.TotalSeconds),
                        bulkImportResponse.TotalTimeTaken.TotalSeconds));
                    Trace.WriteLine(String.Format("Average RU consumption per document insert: {0}",
                        (bulkImportResponse.TotalRequestUnitsConsumed / bulkImportResponse.NumberOfDocumentsImported)));
                    Trace.WriteLine("---------------------------------------------------------------------\n ");

                    totalNumberOfDocumentsInserted += bulkImportResponse.NumberOfDocumentsImported;
                    totalRequestUnitsConsumed += bulkImportResponse.TotalRequestUnitsConsumed;
                    totalTimeTakenSec += bulkImportResponse.TotalTimeTaken.TotalSeconds;
                },
                token));

                /*
                tasks.Add(Task.Run(() =>
                {
                    char ch = Console.ReadKey(true).KeyChar;
                    if (ch == 'c' || ch == 'C')
                    {
                        tokenSource.Cancel();
                        Trace.WriteLine("\nTask cancellation requested.");
                    }
                }));
                */

                await Task.WhenAll(tasks);
            }

            Trace.WriteLine("Overall summary of bulk import:");
            Trace.WriteLine("--------------------------------------------------------------------- ");
            Trace.WriteLine(String.Format("Inserted {0} docs @ {1} writes/s, {2} RU/s in {3} sec",
                totalNumberOfDocumentsInserted,
                Math.Round(totalNumberOfDocumentsInserted / totalTimeTakenSec),
                Math.Round(totalRequestUnitsConsumed / totalTimeTakenSec),
                totalTimeTakenSec));
            Trace.WriteLine(String.Format("Average RU consumption per document insert: {0}",
                (totalRequestUnitsConsumed / totalNumberOfDocumentsInserted)));
            Trace.WriteLine("--------------------------------------------------------------------- \n");

            //-----------------------------------------------------------------------------------------------

            // Prepare for bulk update.

            BulkUpdateResponse bulkUpdateResponse = null;
            long totalNumberOfDocumentsUpdated = 0;
            totalRequestUnitsConsumed = 0;
            totalTimeTakenSec = 0;

            tokenSource = new CancellationTokenSource();
            token = tokenSource.Token;

            // Generate update operations.
            List<UpdateOperation> updateOperations = new List<UpdateOperation>();
            // Set the name field.
            updateOperations.Add(new SetUpdateOperation<string>("Name", "UpdatedDoc"));
            // Unset the description field.
            updateOperations.Add(new UnsetUpdateOperation("description"));

            for (int i = 0; i < numberOfBatches; i++)
            {
                // Generate update items.

                List<UpdateItem> updateItemsInBatch = new List<UpdateItem>();
                long prefix = i * numberOfDocumentsPerBatch;

                Trace.TraceInformation(String.Format("Generating {0} update items for batch {1}", numberOfDocumentsPerBatch, i));
                for (int j = 0; j < numberOfDocumentsPerBatch; j++)
                {
                    string partitionKeyValue = (prefix + j).ToString();
                    string id = partitionKeyValue;

                    updateItemsInBatch.Add(new UpdateItem(id, partitionKeyValue, updateOperations));
                }

                // Invoke bulk update API.

                var tasks = new List<Task>();

                tasks.Add(Task.Run(async () =>
                {
                    Trace.TraceInformation(String.Format("Executing bulk update for batch {0}", i));
                    do
                    {
                        try
                        {
                            bulkUpdateResponse = await bulkExecutor.BulkUpdateAsync(
                                updateItems: updateItemsInBatch,
                                maxConcurrencyPerPartitionKeyRange: null,
                                cancellationToken: token);
                        }
                        catch (DocumentClientException de)
                        {
                            Trace.TraceError("Document client exception: {0}", de);
                            break;
                        }
                        catch (Exception e)
                        {
                            Trace.TraceError("Exception: {0}", e);
                            break;
                        }
                    } while (bulkUpdateResponse.NumberOfDocumentsUpdated < updateItemsInBatch.Count);

                    Trace.WriteLine(String.Format("\nSummary for batch {0}:", i));
                    Trace.WriteLine("--------------------------------------------------------------------- ");
                    Trace.WriteLine(String.Format("Updated {0} docs @ {1} updates/s, {2} RU/s in {3} sec",
                        bulkUpdateResponse.NumberOfDocumentsUpdated,
                        Math.Round(bulkUpdateResponse.NumberOfDocumentsUpdated / bulkUpdateResponse.TotalTimeTaken.TotalSeconds),
                        Math.Round(bulkUpdateResponse.TotalRequestUnitsConsumed / bulkUpdateResponse.TotalTimeTaken.TotalSeconds),
                        bulkUpdateResponse.TotalTimeTaken.TotalSeconds));
                    Trace.WriteLine(String.Format("Average RU consumption per document update: {0}",
                        (bulkUpdateResponse.TotalRequestUnitsConsumed / bulkUpdateResponse.NumberOfDocumentsUpdated)));
                    Trace.WriteLine("---------------------------------------------------------------------\n ");

                    totalNumberOfDocumentsUpdated += bulkUpdateResponse.NumberOfDocumentsUpdated;
                    totalRequestUnitsConsumed += bulkUpdateResponse.TotalRequestUnitsConsumed;
                    totalTimeTakenSec += bulkUpdateResponse.TotalTimeTaken.TotalSeconds;
                },
                token));

                /*
                tasks.Add(Task.Run(() =>
                {
                    char ch = Console.ReadKey(true).KeyChar;
                    if (ch == 'c' || ch == 'C')
                    {
                        tokenSource.Cancel();
                        Trace.WriteLine("\nTask cancellation requested.");
                    }
                }));
                */

                await Task.WhenAll(tasks);
            }

            Trace.WriteLine("Overall summary of bulk update:");
            Trace.WriteLine("--------------------------------------------------------------------- ");
            Trace.WriteLine(String.Format("Updated {0} docs @ {1} update/s, {2} RU/s in {3} sec",
                totalNumberOfDocumentsUpdated,
                Math.Round(totalNumberOfDocumentsUpdated / totalTimeTakenSec),
                Math.Round(totalRequestUnitsConsumed / totalTimeTakenSec),
                totalTimeTakenSec));
            Trace.WriteLine(String.Format("Average RU consumption per document update: {0}",
                (totalRequestUnitsConsumed / totalNumberOfDocumentsUpdated)));
            Trace.WriteLine("--------------------------------------------------------------------- \n");

            //-----------------------------------------------------------------------------------------------

            // Cleanup on finish if set in config.

            if (bool.Parse(ConfigurationManager.AppSettings["ShouldCleanupOnFinish"]))
            {
                Trace.TraceInformation("Deleting Database {0}", DatabaseName);
                await client.DeleteDatabaseAsync(UriFactory.CreateDatabaseUri(DatabaseName));
            }

            Trace.WriteLine("\nPress any key to exit.");
            Console.ReadKey();
        }
    }
}
