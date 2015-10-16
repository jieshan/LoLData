using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LoLData.DataCollection
{
    public class DataCollection 
    {
        public static string[] serverList = {"NA"};

        public static string apiKey;

        public static string apiKeyPath = "PrivateData\\ApiKey.txt";

        public static void Main() 
        {
            apiKey = apiKey == null ? new FileManager(DataCollection.apiKeyPath).readOneLine() : apiKey;

            System.Diagnostics.Debug.WriteLine("============================");
            System.Diagnostics.Debug.WriteLine("Data Collection Starts");

            for (int i = 0; i < DataCollection.serverList.Length; i++ )
            {
                // TODO: Query servers asynchronously
                ServerManager queryManager = new ServerManager(serverList[0], DataCollection.apiKey);
                queryManager.initiateNewSeedScan();
                queryManager.processAllPlayers();
            }

            System.Diagnostics.Debug.WriteLine("============================");
        }
    }
}
