using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using LoLData.DataCollection;

namespace LoLData
{
    public class ServerManager
    {
        private static int rateLimitBuffer = 1000;

        private static int cooldownInterval = 5000;

        private static int maxProcessQueueWaits = 12;

        private static Object haltQueryLock = new Object();

        private static Object retryAfterLock = new Object();

        private static string logFilePathSuffix = "log.txt";

        private static string playersFilePathSuffix = "players.txt";

        private static string gamesFilePathSuffix = "games.txt";

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

        private bool haltQuery;

        private int retryAfter;

        private FileManager logFile;

        private FileManager playersFile;

        private FileManager gamesFile;

        public ServerManager(string serverName, string apiKey) 
        {
            this.server = serverName;
            this.apiKey = apiKey;
            this.httpClient = new HttpClient();
            this.playersToProcess = new Dictionary<string, int>();
            this.playersUnderProcess = new Dictionary<string, int>();
            this.playersProcessed = new Dictionary<string, int>();
            this.gamesProcessed = new Dictionary<string, int>();
            this.logFile = new FileManager(this.server + ServerManager.logFilePathSuffix);
            this.playersFile = new FileManager(this.server + ServerManager.playersFilePathSuffix);
            this.gamesFile = new FileManager(this.server + ServerManager.gamesFilePathSuffix);
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
                        lock (this.playersFile) 
                        {
                            this.playersFile.writeLine(playerId);
                        }
                        lock (this.logFile)
                        {
                            this.logFile.writeLine(String.Format("{0} {1} ======== Player Qualified for Process: {2} (Seed Scan)", DateTime.Now.ToLongTimeString(),
                                DateTime.Now.ToLongDateString(), playerId));
                        }                       
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
                // TODO: Make the call async as well
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
            // Only an approximate, yielding access to data processing work
            return this.playersToProcess.Keys.Count + 
                this.playersUnderProcess.Keys.Count + 
                this.playersProcessed.Keys.Count;
        }

        public int getTotalGamesCount()
        {
            // Only an approximate, yielding access to data processing work
            return this.gamesProcessed.Keys.Count;
        }

        public void closeAllFiles() 
        {
            this.logFile.writerClose();
            this.playersFile.writerClose();
            this.gamesFile.writerClose();
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
                System.Diagnostics.Debug.WriteLine(String.Format("===== {0} server now has processed {1} games.",
                    this.server, this.getTotalGamesCount()));
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
                        string tier = (string)((JObject)queues[j])["tier"];
                        if (((string)((JObject)queues[j])["queue"]).Equals(ServerManager.gameType)
                                && this.validatePlayer(summonerId, tier))
                        {
                            lock (this.playersToProcess)
                            {
                                this.playersToProcess.Add(summonerId, 1);
                                lock (this.playersFile)
                                {
                                    this.playersFile.writeLine(summonerId);
                                }
                                lock (this.logFile) 
                                {
                                    this.logFile.writeLine(String.Format("{0} {1} ======== Player Qualified for Process: {2} ({3})", DateTime.Now.ToLongTimeString(),
                                        DateTime.Now.ToLongDateString(), summonerId, tier));
                                }                              
                            }
                            break;
                        }
                    }
                }            
            }
        }

        private void registerGame(JObject game)
        {
            // TODO: Game analysis implementation
            lock (this.gamesProcessed)
            {
                string gameId = (string)game["gameId"];
                if (!this.gamesProcessed.ContainsKey(gameId))
                {
                    this.gamesProcessed.Add((string)game["gameId"], 1);
                    lock (this.gamesFile)
                    {
                        this.gamesFile.writeLine(gameId);
                    }
                    lock (this.logFile)
                    {
                        this.logFile.writeLine(String.Format("{0} {1} ======== Game Registered {2}", DateTime.Now.ToLongTimeString(),
                            DateTime.Now.ToLongDateString(), gameId));
                    }
                }               
            }
        }

        private JObject makeQuery(string queryString)
        {
            JObject result = null;
            if (!haltQuery)
            {
                var httpResponse = httpClient.GetAsync(queryString).Result;
                int statusCode = (int)httpResponse.StatusCode;
                System.Diagnostics.Debug.WriteLine(String.Format("==== Query: {0} {1}",
                        statusCode, queryString));
                lock (this.logFile)
                {
                    this.logFile.writeLine(String.Format("{0} {1} == Query: {2} {3}", DateTime.Now.ToLongTimeString(),
                        DateTime.Now.ToLongDateString(), statusCode, queryString));
                }          
                if (statusCode == 429)
                {
                    lock (ServerManager.haltQueryLock) 
                    {
                        this.haltQuery = true;
                    }
                    lock (ServerManager.retryAfterLock) 
                    {
                        this.retryAfter = int.Parse(httpResponse.Headers.GetValues("Retry-After").FirstOrDefault()) * 1000;
                    }
                    System.Diagnostics.Debug.WriteLine(String.Format("== Rate Limit hit. Retry after {0} milliseconds. Query: {1}",
                        this.retryAfter, queryString));
                    System.Threading.Thread.Sleep(ServerManager.rateLimitBuffer + this.retryAfter);
                    lock (ServerManager.haltQueryLock)
                    {
                        this.haltQuery = false;
                    }
                    makeQuery(queryString);
                }
                if (statusCode == 200)
                {
                    string json = httpResponse.Content.ReadAsStringAsync().Result;
                    result = JObject.Parse(json);
                }
            }
            else 
            {
                System.Threading.Thread.Sleep(ServerManager.rateLimitBuffer + this.retryAfter);
                makeQuery(queryString);
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
