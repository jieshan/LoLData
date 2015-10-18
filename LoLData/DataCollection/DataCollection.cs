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
            apiKey = apiKey == null ? new FileManager(DataCollection.apiKeyPath).ReadOneLine() : apiKey;

            System.Diagnostics.Debug.WriteLine("============================");
            System.Diagnostics.Debug.WriteLine("Data Collection Starts");

            for (int i = 0; i < DataCollection.serverList.Length; i++ )
            {
                // TODO: Query servers asynchronously
                ServerManager queryManager = new ServerManager(serverList[0], DataCollection.apiKey);
                try
                {
                    queryManager.InitiateNewSeedScan();
                    queryManager.ProcessAllPlayers();
                }
                finally
                {
                    queryManager.CloseAllFiles();
                    System.Diagnostics.Debug.WriteLine("****************");
                    System.Diagnostics.Debug.WriteLine(String.Format("Error in collecting {0} server.", serverList[0]));
                }              
            }

            System.Diagnostics.Debug.WriteLine("============================");
        }
    }
}
