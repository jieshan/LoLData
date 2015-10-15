using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json.Linq;

namespace LoLData
{
    class QueryManager
    {
        private static int timeout = 5000;

        private static string[] leagueSeed = { "CHALLENGER" };

        private static string[] qualifiedLeagues = { "CHALLENGER", "MASTER", "DIAMOND", "PLATINUM" };

        private static string queryTemplate = "https://{0}.api.pvp.net/api/lol/{1}/v2.5/league/{2}?" +
            "type=RANKED_SOLO_5x5&api_key={3}";

        private Dictionary<string, int> playersToProcess;

        private Dictionary<string, int> playersProcessed;

        private Dictionary<string, int> gamesProcessed;

        private WebClient webClient;

        private string server;

        private string apiKey;

        public QueryManager(string serverName, string apiKey) 
        {
            this.server = serverName;
            this.apiKey = apiKey;
            this.webClient = new WebClient();
            this.playersToProcess = new Dictionary<string, int>();
            this.playersProcessed = new Dictionary<string, int>();
            this.gamesProcessed = new Dictionary<string, int>();
        }

        public void initiateSeedScan()
        {
            for (int i = 0; i < QueryManager.leagueSeed.Length; i++ )
            {
                string queryString = String.Format(queryTemplate, server.ToLower(), server.ToLower(), 
                    QueryManager.leagueSeed[i].ToLower(), this.apiKey);
                JObject league = JObject.Parse(webClient.DownloadString(queryString));
                JArray players = (JArray) league["entries"];
                while (players == null)
                {
                    System.Threading.Thread.Sleep(QueryManager.timeout);
                    league = JObject.Parse(webClient.DownloadString(queryString));
                    players = (JArray) league["entries"];
                }
                string tier = (string) league["tier"];
                for (int j = 0; j < players.Count; j++) 
                {
                    JObject player = (JObject) players[j];
                    string playerId = (string) player["playerOrTeamId"];
                    if (validatePlayer(playerId, tier)) 
                    {
                        playersToProcess.Add(playerId, 1);
                    }
                }
            }          
        }

        public int getTotalPlayersCount() 
        {
            return this.playersToProcess.Keys.Count + this.playersProcessed.Keys.Count;
        }

        public int getTotalGamesCount()
        {
            return this.gamesProcessed.Keys.Count;
        }

        private bool validatePlayer(string playerId, string tier) 
        {
            return !playersProcessed.ContainsKey(playerId) && !playersToProcess.ContainsKey(playerId)
                && qualifiedLeagues.Contains(tier.ToUpper()) ? true : false;
        }
    }
}
