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

        public static string apiKey = "xxx";

        public static void Main() 
        {
            System.Diagnostics.Debug.WriteLine("============================");
            System.Diagnostics.Debug.WriteLine("Data Collection Starts");

            for (int i = 0; i < DataCollection.serverList.Length; i++ )
            {
                // TODO: Query servers asynchronously
                QueryManager queryManager = new QueryManager(serverList[0], DataCollection.apiKey);
                queryManager.initiateSeedScan();
                System.Diagnostics.Debug.WriteLine("****************");
                System.Diagnostics.Debug.WriteLine(queryManager.getTotalPlayersCount());
            }

            System.Diagnostics.Debug.WriteLine("============================");
        }    
    }
}
