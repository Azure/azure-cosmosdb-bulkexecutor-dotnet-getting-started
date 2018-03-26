//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace BulkImportSample
{
    using System;
    using System.Configuration;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using Newtonsoft.Json;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Client;
    using Microsoft.Azure.CosmosDB.BulkExecutor;
    using Microsoft.Azure.CosmosDB.BulkExecutor.BulkImport;

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
                    program.RunBulkImportAsync().Wait();
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
        private async Task RunBulkImportAsync()
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

            string sampleDocument = File.ReadAllText("../../" + ConfigurationManager.AppSettings["SampleDocumentTemplateFile"]);
            Dictionary<string, object> sampleDictionary = JsonConvert.DeserializeObject<Dictionary<string, object>>(sampleDocument);

            long numberOfDocumentsToGenerate = long.Parse(ConfigurationManager.AppSettings["NumberOfDocumentsToImport"]);
            int numberOfBatches = int.Parse(ConfigurationManager.AppSettings["NumberOfBatches"]);
            long numberOfDocumentsPerBatch = (long)Math.Floor(((double)numberOfDocumentsToGenerate) / numberOfBatches);

            IBulkExecutor bulkExecutor = new BulkExecutor(client, dataCollection);
            await bulkExecutor.InitializeAsync();

            BulkImportResponse bulkImportResponse = null;
            long totalNumberOfDocumentsInserted = 0;
            double totalRequestUnitsConsumed = 0;
            double totalTimeTakenSec = 0;

            var tokenSource = new CancellationTokenSource();
            var token = tokenSource.Token;

            for (int i = 0; i < numberOfBatches; i++)
            {
                // Generate documents to import.

                List<object> documentsToImportInBatch = new List<object>();
                long prefix = i * numberOfDocumentsPerBatch;

                Trace.TraceInformation(String.Format("Generating {0} documents to import for batch {1}", numberOfDocumentsPerBatch, i));
                for (int j = 0; j < numberOfDocumentsPerBatch; j++)
                {
                    string partitionKeyValue = (prefix + j).ToString();
                    sampleDictionary["id"] = partitionKeyValue + Guid.NewGuid().ToString();
                    sampleDictionary[partitionKeyProperty] = partitionKeyValue;

                    documentsToImportInBatch.Add(sampleDictionary.ToDictionary(entry => entry.Key, entry => entry.Value));
                }

                // Invoke bulk import (deserialized docs) API.

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
                                enableUpsert: false,
                                disableAutomaticIdGeneration: true,
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
                    } while (bulkImportResponse.NumberOfDocumentsImported < documentsToImportInBatch.Count);

                    Trace.WriteLine(String.Format("\nSummary for batch {0}:", i));
                    Trace.WriteLine("--------------------------------------------------------------------- ");
                    Trace.WriteLine(String.Format("Inserted {0} docs @ {1} writes/s, {2} RU/s in {3} sec)",
                        bulkImportResponse.NumberOfDocumentsImported,
                        Math.Round(bulkImportResponse.NumberOfDocumentsImported / bulkImportResponse.TotalTimeTaken.TotalSeconds),
                        Math.Round(bulkImportResponse.TotalRequestUnitsConsumed / bulkImportResponse.TotalTimeTaken.TotalSeconds),
                        bulkImportResponse.TotalTimeTaken.TotalSeconds));
                    Trace.WriteLine(String.Format("Average RU consumption per document: {0}",
                        (bulkImportResponse.TotalRequestUnitsConsumed / bulkImportResponse.NumberOfDocumentsImported)));
                    Trace.WriteLine("---------------------------------------------------------------------\n ");

                    totalNumberOfDocumentsInserted += bulkImportResponse.NumberOfDocumentsImported;
                    totalRequestUnitsConsumed += bulkImportResponse.TotalRequestUnitsConsumed;
                    totalTimeTakenSec += bulkImportResponse.TotalTimeTaken.TotalSeconds;
                },
                token));

                tasks.Add(Task.Run(() =>
                {
                    char ch = Console.ReadKey(true).KeyChar;
                    if (ch == 'c' || ch == 'C')
                    {
                        tokenSource.Cancel();
                        Trace.WriteLine("\nTask cancellation requested.");
                    }
                }));

                await Task.WhenAll(tasks);
            }

            Trace.WriteLine("Overall summary:");
            Trace.WriteLine("--------------------------------------------------------------------- ");
            Trace.WriteLine(String.Format("Inserted {0} docs @ {1} writes/s, {2} RU/s in {3} sec)",
                totalNumberOfDocumentsInserted,
                Math.Round(totalNumberOfDocumentsInserted / totalTimeTakenSec),
                Math.Round(totalRequestUnitsConsumed / totalTimeTakenSec),
                totalTimeTakenSec));
            Trace.WriteLine(String.Format("Average RU consumption per document: {0}",
                (totalRequestUnitsConsumed / totalNumberOfDocumentsInserted)));
            Trace.WriteLine("--------------------------------------------------------------------- ");

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
