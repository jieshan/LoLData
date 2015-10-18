using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using LoLData.DataCollection;

namespace LoLData
{
    public class ServerManager
    {
        private static int cooldownInterval = 5000;

        private static int maxProcessQueueWaits = 12;

        private static int gamesProgressReport = 50;

        private static string logFilePathTemplate = "..\\..\\CachedData\\{0}log.txt";

        private static string playersFilePathTemplate = "..\\..\\CachedData\\{0}players.txt";

        private static string gamesFilePathTemplate = "..\\..\\CachedData\\{0}games.txt";

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

        private QueryManager queryManager;

        private string server;

        private string apiKey;

        private FileManager logFile;

        private FileManager playersFile;

        private FileManager gamesFile;

        public ServerManager(string serverName, string apiKey) 
        {
            this.server = serverName;
            this.apiKey = apiKey;
            this.playersToProcess = new Dictionary<string, int>();
            this.playersUnderProcess = new Dictionary<string, int>();
            this.playersProcessed = new Dictionary<string, int>();
            this.gamesProcessed = new Dictionary<string, int>();
            this.logFile = new FileManager(String.Format(ServerManager.logFilePathTemplate, this.server));
            lock (this.logFile)
            {
                this.logFile.writeLine(String.Format("{0} {1} ======== Log file created.", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString()));
            }
            this.playersFile = new FileManager(String.Format(ServerManager.playersFilePathTemplate, this.server));
            this.gamesFile = new FileManager(String.Format(ServerManager.gamesFilePathTemplate, this.server));
            this.queryManager = new QueryManager(this.logFile);
        }

        public void initiateNewSeedScan()
        {
            for (int i = 0; i < ServerManager.leagueSeed.Length; i++ )
            {
                string queryString = String.Format(ServerManager.seedQueryTemplate, this.server.ToLower(), 
                    this.server.ToLower(), ServerManager.leagueSeed[i].ToLower(), ServerManager.gameType, this.apiKey);
                JObject league = this.queryManager.makeQuery(queryString);
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
                this.logFile.writerFlush();
                this.playersFile.writerFlush();
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
                // TODO: Make the call async as well.
                // Not necessary for now as the bottleneck is rate limiting.
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
            JObject gamesResponse = this.queryManager.makeQuery(queryString);

            if(gamesResponse != null)
            {
                JArray games = (JArray)gamesResponse["games"];

                for (int i = 0; i < games.Count; i++)
                {
                    JObject game = (JObject)games.ElementAt(i);
                    try 
                    {
                        Task.Run(() => processGame(game));
                    }
                    catch (AggregateException ae)
                    {
                        lock (this.logFile)
                        {
                            var flattenedAe = ae.Flatten();
                            this.logFile.writeLine("****************");
                            this.logFile.writeLine("Error encountered in processGame: ");
                            this.logFile.writeLine(flattenedAe.ToString());
                            this.logFile.writeLine("****************");
                            this.logFile.writerFlush();
                        }
                    }
                }
            }         

            // TODO: Write playerId to cache
            int playerCount;
            lock (this.playersUnderProcess) lock (this.playersProcessed)
            {
                this.playersUnderProcess.Remove(playerId);
                this.playersProcessed.Add(playerId, 1);
            }
            System.Diagnostics.Debug.WriteLine(String.Format("{0} ========= Player {1} processed. Overall - Done: {2}  InProgress: {3}  ToDo: {4}.",
                DateTime.Now.ToLongTimeString(), playerId, this.playersProcessed.Keys.Count, this.playersUnderProcess.Keys.Count, 
                this.playersToProcess.Keys.Count));
            playerCount = this.playersProcessed.Keys.Count;
            if (playerCount % ServerManager.gamesProgressReport == 0)
            {
                System.Diagnostics.Debug.WriteLine(String.Format("===== {0} server now has processed {1} players.", 
                    this.server, playerCount));
                System.Diagnostics.Debug.WriteLine(String.Format("===== {0} server now has processed {1} games.",
                    this.server, this.getTotalGamesCount()));
            }
        }

        private void processGame(JObject game)
        {
            if (((string) game["subType"]).Equals(ServerManager.gameType))
            {
                registerGame(game);
            }
            JArray fellowPlayers = (JArray) game["fellowPlayers"];
            if(fellowPlayers == null)
            {
                return;
            }
            string[] summonerIds = new string[fellowPlayers.Count];
            for (int i = 0; i < fellowPlayers.Count; i++ )
            {
                string summonerId = (string) ((JObject) fellowPlayers[i])["summonerId"];
                summonerIds[i] = summonerId; 
            }
            try
            {
                Task.Run(() => this.processPlayerFromGame(summonerIds));
            }
            catch (AggregateException ae)
            {
                lock (this.logFile)
                {
                    var flattenedAe = ae.Flatten();
                    this.logFile.writeLine("****************");
                    this.logFile.writeLine("Error encountered in processPlayerFromGame: ");
                    this.logFile.writeLine(flattenedAe.ToString());
                    this.logFile.writeLine("****************");
                    this.logFile.writerFlush();
                }
            }
        }

        private void processPlayerFromGame(string[] summonerIds) 
        {
            string summonerIdsParam = String.Join(",", summonerIds);
            string queryString = String.Format(ServerManager.summonerQueryTemplate, this.server.ToLower(),
                    this.server.ToLower(), summonerIdsParam, this.apiKey);
            JObject summoners = this.queryManager.makeQuery(queryString);
            if (summoners != null)
            {
                for (int i = 0; i < summonerIds.Length; i++)
                {
                    string summonerId = summonerIds[i];
                    JArray queues = (JArray)summoners[summonerId];
                    for (int j = 0; queues != null && j < queues.Count; j++)
                    {
                        string tier = (string)((JObject)queues[j])["tier"];
                        if (((string)((JObject)queues[j])["queue"]).Equals(ServerManager.gameType)
                                && this.validatePlayer(summonerId, tier))
                        {
                            lock (this.playersToProcess)
                            {
                                this.playersToProcess.Add(summonerId, 1);
                            }
                            lock (this.playersFile)
                            {
                                this.playersFile.writeLine(summonerId);
                                this.playersFile.writerFlush();
                            }
                            lock (this.logFile)
                            {
                                this.logFile.writeLine(String.Format("{0} {1} ======== Player Qualified for Process: {2} ({3})", DateTime.Now.ToLongTimeString(),
                                    DateTime.Now.ToLongDateString(), summonerId, tier));
                                this.logFile.writerFlush();
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
            string gameId = null;
            lock (this.gamesProcessed)
            {
                gameId = (string)game["gameId"];
                if (!this.gamesProcessed.ContainsKey(gameId))
                {
                    this.gamesProcessed.Add((string)game["gameId"], 1);
                }               
            }
            lock (this.gamesFile)
            {
                this.gamesFile.writeLine(gameId);
                this.gamesFile.writerFlush();
            }
            lock (this.logFile)
            {
                this.logFile.writeLine(String.Format("{0} {1} ======== Game Registered {2}", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString(), gameId));
                this.logFile.writerFlush();
            }
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
