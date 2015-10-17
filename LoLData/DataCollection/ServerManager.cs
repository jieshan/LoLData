using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Net.Http;

namespace LoLData
{
    public class ServerManager
    {
        private static int cooldownInterval = 120000;

        private static int maxProcessQueueWaits = 1; 

        private static string gameType = "RANKED_SOLO_5x5";

        private static string[] leagueSeed = { "CHALLENGER" };

        private static string[] qualifiedLeagues = { "CHALLENGER", "MASTER", "DIAMOND", "PLATINUM" };

        private static string seedQueryTemplate = "https://{0}.api.pvp.net/api/lol/{1}/v2.5/league/{2}?" +
            "type={3}&api_key={4}";

        private static string gamesQueryTemplate = "https://{0}.api.pvp.net/api/lol/{1}/v1.3/game/" +
            "by-summoner/{2}/recent?api_key={3}";

        private static string summonerQueryTemplate = "https://{0}.api.pvp.net/api/lol/{1}/v2.5/league/" + 
            "by-summoner/{2}/entry?api_key={3}";

        private Dictionary<string, int> playersToProcess;

        private Dictionary<string, int> playersUnderProcess;

        private Dictionary<string, int> playersProcessed;

        private Dictionary<string, int> gamesProcessed;

        private HttpClient httpClient;

        private string server;

        private string apiKey;

        public ServerManager(string serverName, string apiKey) 
        {
            this.server = serverName;
            this.apiKey = apiKey;
            this.httpClient = new HttpClient();
            this.playersToProcess = new Dictionary<string, int>();
            this.playersUnderProcess = new Dictionary<string, int>();
            this.playersProcessed = new Dictionary<string, int>();
            this.gamesProcessed = new Dictionary<string, int>();
        }

        public void initiateNewSeedScan()
        {
            for (int i = 0; i < ServerManager.leagueSeed.Length; i++ )
            {
                string queryString = String.Format(ServerManager.seedQueryTemplate, this.server.ToLower(), 
                    this.server.ToLower(), ServerManager.leagueSeed[i].ToLower(), ServerManager.gameType, this.apiKey);
                JObject league = this.makeQuery(queryString);
                if(league == null)
                {
                    return;
                }
                JArray players = (JArray) league["entries"];
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

        public bool processAllPlayers(int remainingWaits = -1) 
        {
            if (remainingWaits == -1) 
            {
                remainingWaits = ServerManager.maxProcessQueueWaits;
            }
            int remainingPlayers = this.playersToProcess.Keys.Count;
            while (remainingPlayers > 0)
            {
                this.processNextPlayer();
                remainingWaits = ServerManager.maxProcessQueueWaits;
            }
            System.Threading.Thread.Sleep(ServerManager.cooldownInterval);
            remainingWaits -= 1;
            System.Diagnostics.Debug.WriteLine(String.Format("===== Remaining waits for more players to process: {0}.",
               remainingWaits));
            if (remainingWaits == 0)
            {
                System.Diagnostics.Debug.WriteLine("****************");
                System.Diagnostics.Debug.WriteLine(this.getTotalPlayersCount());
                System.Diagnostics.Debug.WriteLine("****************");
                System.Diagnostics.Debug.WriteLine(this.getTotalGamesCount());
                return true;
            }
            return this.processAllPlayers(remainingWaits);
        }

        public int getTotalPlayersCount()
        {
            return this.playersToProcess.Keys.Count + 
                this.playersUnderProcess.Keys.Count + 
                this.playersProcessed.Keys.Count;
        }

        public int getTotalGamesCount()
        {
            return this.gamesProcessed.Keys.Count;
        }

        private void processNextPlayer() 
        {
            string playerId;
            lock (this.playersToProcess) lock (this.playersUnderProcess)
            {
                playerId = this.playersToProcess.Keys.ElementAt(0);
                this.playersToProcess.Remove(playerId);
                this.playersUnderProcess.Add(playerId, 1); 
            }            
            string queryString = String.Format(ServerManager.gamesQueryTemplate, this.server.ToLower(), 
                this.server.ToLower(), playerId, this.apiKey);
            JObject gamesResponse = this.makeQuery(queryString);

            if(gamesResponse != null)
            {
                JArray games = (JArray)gamesResponse["games"];

                for (int i = 0; i < games.Count; i++)
                {
                    JObject game = (JObject)games.ElementAt(i);
                    Task.Run(() => processGame(game));
                }
            }         

            // TODO: Write playerId to cache
            int playerCount;
            lock (this.playersUnderProcess) lock (this.playersProcessed)
            {
                this.playersUnderProcess.Remove(playerId);
                this.playersProcessed.Add(playerId, 1);
            }
            System.Diagnostics.Debug.WriteLine(String.Format("========= Player {0} processed. Remaining {1} players.",
                playerId, this.playersToProcess.Keys.Count));
            playerCount = this.playersProcessed.Keys.Count;
            if (playerCount % 200 == 0)
            {
                System.Diagnostics.Debug.WriteLine(String.Format("===== {0} server now has processed {1} players.", 
                    this.server, playerCount));
            }
        }

        private void processGame(JObject game)
        {
            if (game["subType"].Equals(ServerManager.gameType))
            {
                registerGame(game);
            }
            JArray fellowPlayers = (JArray) game["fellowPlayers"];
            if(fellowPlayers == null)
            {
                return;
            }
            for (int i = 0; i < fellowPlayers.Count; i++ )
            {
                string summonerId = (string) ((JObject) fellowPlayers[i])["summonerId"];
                string queryString = String.Format(ServerManager.summonerQueryTemplate, this.server.ToLower(), 
                    this.server.ToLower(), summonerId, this.apiKey);
                JObject summoner = this.makeQuery(queryString);
                if(summoner != null)
                {
                    JArray queues = (JArray)summoner[summonerId];
                    for (int j = 0; j < queues.Count; j++)
                    {
                        if (((string)((JObject)queues[j])["queue"]).Equals(ServerManager.gameType)
                                && this.validatePlayer(summonerId, (string)((JObject)queues[j])["tier"]))
                        {
                            lock (this.playersToProcess)
                            {
                                this.playersToProcess.Add(summonerId, 1);
                            }
                            break;
                        }
                    }
                }            
            }
        }

        private void registerGame(JObject game)
        {
            // TODO: Implementation
        }

        private JObject makeQuery(string queryString)
        {
            JObject result = null;
            var httpResponse = httpClient.GetAsync(queryString).Result;
            int statusCode = (int) httpResponse.StatusCode;
            while (statusCode == 429)
            {
                System.Threading.Thread.Sleep(ServerManager.cooldownInterval);
                httpResponse = httpClient.GetAsync(queryString).Result;
                statusCode = (int)httpResponse.StatusCode;
            }
            if (statusCode == 200) 
            {
                string json = httpResponse.Content.ReadAsStringAsync().Result;
                result = JObject.Parse(json);
            }
            return result;
        }

        private bool validatePlayer(string playerId, string tier) 
        {
            lock (this.playersToProcess) lock (this.playersUnderProcess) lock (this.playersProcessed)
            {
                return !this.playersToProcess.ContainsKey(playerId) && !this.playersUnderProcess.ContainsKey(playerId)
                    && !this.playersProcessed.ContainsKey(playerId) && qualifiedLeagues.Contains(tier.ToUpper()) ? true : false;
            }
        }
    }
}
