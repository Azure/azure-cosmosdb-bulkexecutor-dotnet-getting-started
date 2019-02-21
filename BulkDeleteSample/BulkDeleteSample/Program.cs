using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.CosmosDB.BulkExecutor;
using Microsoft.Azure.CosmosDB.BulkExecutor.BulkImport;
using Microsoft.Azure.CosmosDB.BulkExecutor.BulkDelete;

namespace BulkDeleteSample
{
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
        private DocumentCollection dataCollection;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="Program"/> class.
        /// </summary>
        /// <param name="client">The DocumentDB client instance.</param>
        private Program(DocumentClient client)
        {
            this.client = client;
        }

        public static void Main(string[] args)
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
                    program.RunBulkImportAndBulkDeleteAsync().Wait();
                }
            }
            catch (AggregateException e)
            {
                Trace.TraceError("Caught AggregateException in Main, Inner Exception:\n" + e);
                Console.ReadKey();
            }
        }

        private async Task RunBulkImport()
        {
            // Cleanup on start if set in config.

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
                    this.dataCollection = await Utils.CreatePartitionedCollectionAsync(client, DatabaseName, CollectionName, CollectionThroughput);
                }
                else
                {
                    this.dataCollection = Utils.GetCollectionIfExists(client, DatabaseName, CollectionName);
                    if (this.dataCollection == null)
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
            string partitionKeyProperty = this.dataCollection.PartitionKey.Paths[0].Replace("/", "");

            long numberOfDocumentsToGenerate = long.Parse(ConfigurationManager.AppSettings["NumberOfDocumentsToDelete"]);
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
        }

        private List<Tuple<string, string>> GeneratePartitionKeyDocumentIdTuplesToBulkDelete()
        {
            long NumberOfDocumentsToDelete = long.Parse(ConfigurationManager.AppSettings["NumberOfDocumentsToDelete"]);
            
            List<Tuple<string, string>> pkIdTuplesToDelete = new List<Tuple<string, string>>();
            for(int i=0; i < NumberOfDocumentsToDelete; i++)
            {
                pkIdTuplesToDelete.Add(new Tuple<string, string>(i.ToString(), i.ToString()));
            }

            return pkIdTuplesToDelete;
        }

        /// <summary>
        /// Driver function for bulk import.
        /// </summary>
        /// <returns></returns>
        private async Task RunBulkImportAndBulkDeleteAsync()
        {
            // Import documents into the collection
            await this.RunBulkImport();

            // Fetch <PartitionKey><DocumentId> tuples to delete in bulk
            List<Tuple<string, string>> pkIdTuplesToDelete = GeneratePartitionKeyDocumentIdTuplesToBulkDelete();

            long totalNumberOfDocumentsDeleted = 0;
            double totalRequestUnitsConsumed = 0;
            double totalTimeTakenSec = 0;
            BulkDeleteResponse bulkDeleteResponse = null;

            BulkExecutor bulkExecutor = new BulkExecutor(this.client, this.dataCollection);
            await bulkExecutor.InitializeAsync();
            try
            {
                bulkDeleteResponse = await bulkExecutor.BulkDeleteAsync(pkIdTuplesToDelete);
                totalNumberOfDocumentsDeleted = bulkDeleteResponse.NumberOfDocumentsDeleted;
                totalRequestUnitsConsumed = bulkDeleteResponse.TotalRequestUnitsConsumed;
                totalTimeTakenSec = bulkDeleteResponse.TotalTimeTaken.TotalSeconds;
            }
            catch (DocumentClientException de)
            {
                Trace.TraceError("Document client exception: {0}", de);
            }
            catch (Exception e)
            {
                Trace.TraceError("Exception: {0}", e);
            }

            Trace.WriteLine("\n\n--------------------------------------------------------------------- ");
            Trace.WriteLine("Executing bulk delete:");
            Trace.WriteLine("--------------------------------------------------------------------- ");
            Trace.WriteLine("\n\nOverall summary of bulk delete:");
            Trace.WriteLine("--------------------------------------------------------------------- ");
            Trace.WriteLine(String.Format("Deleted {0} docs @ {1} writes/s, {2} RU/s in {3} sec",
                totalNumberOfDocumentsDeleted,
                Math.Round(totalNumberOfDocumentsDeleted / totalTimeTakenSec),
                Math.Round(totalRequestUnitsConsumed / totalTimeTakenSec),
                totalTimeTakenSec));
            Trace.WriteLine(String.Format("Average RU consumption per document delete: {0}",
                (totalRequestUnitsConsumed / totalNumberOfDocumentsDeleted)));
            Trace.WriteLine("--------------------------------------------------------------------- \n");

            //-----------------------------------------------------------------------------------------------
        }
    }
}
