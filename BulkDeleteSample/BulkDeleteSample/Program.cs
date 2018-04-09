//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace BulkDeleteSample
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
    using Microsoft.Azure.CosmosDB.BulkExecutor.BulkDelete;

    class Program
    {
        private static readonly string EndpointUrl = ConfigurationManager.AppSettings["EndPointUrl"];
        private static readonly string AuthorizationKey = ConfigurationManager.AppSettings["AuthorizationKey"];
        private static readonly string DatabaseName = ConfigurationManager.AppSettings["DatabaseName"];
        private static readonly string CollectionName = ConfigurationManager.AppSettings["CollectionName"];

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
                    program.RunBulkDeleteAsync().Wait();
                }
            }
            catch (AggregateException e)
            {
                Trace.TraceError("Caught AggregateException in Main, Inner Exception:\n" + e);
                Console.ReadKey();
            }
        }

        /// <summary>
        /// Driver function for bulk delete.
        /// </summary>
        /// <returns></returns>
        private async Task RunBulkDeleteAsync()
        {
            String sqlQuery = ConfigurationManager.AppSettings["SQLQuery"];
            if (sqlQuery == null)
            {
                throw new ArgumentNullException(nameof(sqlQuery));
            }

            DocumentCollection dataCollection = null;
            try
            {
                dataCollection = Utils.GetCollectionIfExists(client, DatabaseName, CollectionName);
                if (dataCollection == null)
                {
                    throw new Exception("The data collection does not exist");
                }
            }
            catch (Exception de)
            {
                Trace.TraceError("Unable to initialize, exception message: {0}", de.Message);
                throw;
            }

            // Prepare for bulk delete.

            BulkExecutor bulkExecutor = new BulkExecutor(client, dataCollection);
            await bulkExecutor.InitializeAsync();

            long totalNumberOfDocumentsDeleted = 0;
            double totalRequestUnitsConsumed = 0.0;
            double totalTimeTaken = 0;

            long numberOfDocumentsDeletedInCurrentBatch = 0;

            do
            {
                numberOfDocumentsDeletedInCurrentBatch = 0;

                BulkDeleteResponse bulkDeleteResponse = null;
                try
                {
                    bulkDeleteResponse = await bulkExecutor.BulkDeleteAsync(sqlQuery);
                }
                catch (DocumentClientException de)
                {
                    Trace.TraceError("Document client exception: {0}", de);
                    throw de;
                }
                catch (Exception e)
                {
                    Trace.TraceError("Exception: {0}", e);
                    throw e;
                }

                Trace.WriteLine("Curent batch delete summary:");
                Trace.WriteLine("--------------------------------------------------------------------- ");
                Trace.WriteLine(String.Format("Deleted {0} docs @ {1} deletes/s, {2} RU/s in {3} sec",
                    bulkDeleteResponse.NumberOfDocumentsDeleted,
                    Math.Round(bulkDeleteResponse.NumberOfDocumentsDeleted / bulkDeleteResponse.TotalTimeTaken.TotalSeconds),
                    Math.Round(bulkDeleteResponse.TotalRequestUnitsConsumed / bulkDeleteResponse.TotalTimeTaken.TotalSeconds),
                    bulkDeleteResponse.TotalTimeTaken.TotalSeconds));
                Trace.WriteLine(String.Format("Average RU consumption per document delete: {0}",
                    (bulkDeleteResponse.TotalRequestUnitsConsumed / bulkDeleteResponse.NumberOfDocumentsDeleted)));
                Trace.WriteLine("--------------------------------------------------------------------- ");

                numberOfDocumentsDeletedInCurrentBatch = bulkDeleteResponse.NumberOfDocumentsDeleted;
                totalNumberOfDocumentsDeleted += numberOfDocumentsDeletedInCurrentBatch;
                totalRequestUnitsConsumed += bulkDeleteResponse.TotalRequestUnitsConsumed;
                totalTimeTaken += bulkDeleteResponse.TotalTimeTaken.TotalSeconds;

            } while (numberOfDocumentsDeletedInCurrentBatch > 0);

            Trace.WriteLine("\nOverall summary:");
            Trace.WriteLine("--------------------------------------------------------------------- ");
            Trace.WriteLine(String.Format("Deleted {0} docs @ {1} deletes/s, {2} RU/s in {3} sec",
                totalNumberOfDocumentsDeleted,
                Math.Round(totalNumberOfDocumentsDeleted / totalTimeTaken),
                Math.Round(totalRequestUnitsConsumed / totalTimeTaken),
                totalTimeTaken));
            Trace.WriteLine(String.Format("Average RU consumption per document delete: {0}",
                (totalRequestUnitsConsumed / totalNumberOfDocumentsDeleted)));
            Trace.WriteLine("--------------------------------------------------------------------- ");


            Trace.WriteLine("\nPress any key to exit.");
            Console.ReadKey();
        }
    }
}
