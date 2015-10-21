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

        private static int maxServersThreads = 400;

        private static string logFilePathTemplate = "..\\..\\CachedData\\{0}log.txt";

        private static string playersFilePathTemplate = "..\\..\\CachedData\\{0}players.txt";

        private static string gamesFilePathTemplate = "..\\..\\CachedData\\{0}games.txt";

        private static string gameType = "RANKED_SOLO_5x5";

        private static string[] leagueSeed = { "CHALLENGER" };

        private static string[] qualifiedLeagues = { "CHALLENGER", "MASTER", "DIAMOND", "PLATINUM" };

        private static string serverRootDomainTemplate = "https://{0}.api.pvp.net/";

        private static string seedQueryTemplate = "https://{0}.api.pvp.net/api/lol/{1}/v2.5/league/{2}?" +
            "type={3}&api_key={4}";

        private static string gamesQueryTemplate = "https://{0}.api.pvp.net/api/lol/{1}/v1.3/game/" +
            "by-summoner/{2}/recent?api_key={3}";

        private static string summonerQueryTemplate = "https://{0}.api.pvp.net/api/lol/{1}/v2.5/league/" + 
            "by-summoner/{2}/entry?api_key={3}";

        public static Object currentThreadsLock = new Object();

        public static int currentWebCalls = 0;

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
                this.logFile.WriteLine(String.Format("{0} {1} ======== Log file created.", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString()));
            }
            this.playersFile = new FileManager(String.Format(ServerManager.playersFilePathTemplate, this.server));
            this.gamesFile = new FileManager(String.Format(ServerManager.gamesFilePathTemplate, this.server));
            this.queryManager = new QueryManager(this.logFile, String.Format(ServerManager.serverRootDomainTemplate, this.server));
        }

        public async void InitiateNewSeedScan()
        {
            for (int i = 0; i < ServerManager.leagueSeed.Length; i++ )
            {
                string queryString = String.Format(ServerManager.seedQueryTemplate, this.server.ToLower(), 
                    this.server.ToLower(), ServerManager.leagueSeed[i].ToLower(), ServerManager.gameType, this.apiKey);
                try
                {
                    this.VerifyThreadsCapacity();
                    JObject league = await this.queryManager.MakeQuery(queryString);
                    if (league == null)
                    {
                        return;
                    }
                    JArray players = (JArray)league["entries"];
                    string tier = (string)league["tier"];
                    for (int j = 0; j < players.Count; j++)
                    {
                        JObject player = (JObject)players[j];
                        string playerId = (string)player["playerOrTeamId"];
                        if (ValidatePlayer(playerId, tier))
                        {
                            playersToProcess.Add(playerId, 1);
                            lock (this.playersFile)
                            {
                                this.playersFile.WriteLine(playerId);
                            }
                            lock (this.logFile)
                            {
                                this.logFile.WriteLine(String.Format("{0} {1} ======== Player Qualified for Process: {2} (Seed Scan)", 
                                    DateTime.Now.ToLongTimeString(), DateTime.Now.ToLongDateString(), playerId));
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    lock (this.logFile)
                    {
                        this.logFile.WriterLogError(e.ToString());
                        this.logFile.WriterFlush();
                    }
                }
                finally 
                {
                    this.logFile.WriterFlush();
                    this.playersFile.WriterFlush();
                }
            }          
        }

        public bool ProcessAllPlayers(int remainingWaits = -1) 
        {
            if (remainingWaits == -1) 
            {
                remainingWaits = ServerManager.maxProcessQueueWaits;
            }
            int remainingPlayers = this.playersToProcess.Keys.Count;
            while (remainingPlayers > 0)
            {
                this.VerifyThreadsCapacity();
                this.ProcessNextPlayer();
                remainingWaits = ServerManager.maxProcessQueueWaits;
            }
            System.Threading.Thread.Sleep(ServerManager.cooldownInterval);
            remainingWaits -= 1;
            System.Diagnostics.Debug.WriteLine(String.Format("===== Remaining waits for more players to process: {0}.",
               remainingWaits));
            if (remainingWaits == 0)
            {
                System.Diagnostics.Debug.WriteLine("****************");
                System.Diagnostics.Debug.WriteLine(this.GetTotalPlayersCount());
                System.Diagnostics.Debug.WriteLine("****************");
                System.Diagnostics.Debug.WriteLine(this.GetTotalGamesCount());
                return true;
            }
            return this.ProcessAllPlayers(remainingWaits);
        }

        public int GetTotalPlayersCount()
        {
            // Only an approximate, yielding access to data processing work
            return this.playersToProcess.Keys.Count + 
                this.playersUnderProcess.Keys.Count + 
                this.playersProcessed.Keys.Count;
        }

        public int GetTotalGamesCount()
        {
            // Only an approximate, yielding access to data processing work
            return this.gamesProcessed.Keys.Count;
        }

        public void CloseAllFiles() 
        {
            this.logFile.WriterClose();
            this.playersFile.WriterClose();
            this.gamesFile.WriterClose();
        }

        private async void ProcessNextPlayer() 
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
            try
            {
                this.VerifyThreadsCapacity();
                JObject gamesResponse = await this.queryManager.MakeQuery(queryString);
                if (gamesResponse != null)
                {
                    JArray games = (JArray)gamesResponse["games"];

                    for (int i = 0; i < games.Count; i++)
                    {
                        JObject game = (JObject)games.ElementAt(i);
                        try
                        {
                            this.VerifyThreadsCapacity();
                            Task.Run(() => ProcessGame(game));
                        }
                        catch (AggregateException ae)
                        {
                            lock (this.logFile)
                            {
                                var flattenedAe = ae.Flatten();
                                this.logFile.WriterLogError(ae.ToString());
                                this.logFile.WriterFlush();
                            }
                        }
                    }
                    lock (this.playersProcessed)
                    {
                        this.playersProcessed.Add(playerId, 1);
                        System.Diagnostics.Debug.WriteLine(String.Format("{0} ========= Player {1} processed. Overall - Done: {2}  InProgress: {3}  ToDo: {4}.",
                            DateTime.Now.ToLongTimeString(), playerId, this.playersProcessed.Keys.Count, this.playersUnderProcess.Keys.Count,
                            this.playersToProcess.Keys.Count));
                    } 
                }
                lock (this.playersUnderProcess) 
                {
                    this.playersUnderProcess.Remove(playerId);
                }              
            }
            catch (Exception e)
            {
                lock (this.logFile)
                {
                    this.logFile.WriterLogError(e.ToString());
                    this.logFile.WriterFlush();
                }
            }
            lock (ServerManager.currentThreadsLock)
            {
                ServerManager.currentWebCalls--;
            }
            int playerCount;
            playerCount = this.playersProcessed.Keys.Count;
            if (playerCount % ServerManager.gamesProgressReport == 0)
            {
                System.Diagnostics.Debug.WriteLine(String.Format("===== {0} server now has processed {1} players.", 
                    this.server, playerCount));
                System.Diagnostics.Debug.WriteLine(String.Format("===== {0} server now has processed {1} games.",
                    this.server, this.GetTotalGamesCount()));
            }
        }

        private void ProcessGame(JObject game)
        {
            if (((string) game["subType"]).Equals(ServerManager.gameType))
            {
                RegisterGame(game);
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
                this.VerifyThreadsCapacity();
                Task.Run(() => this.ProcessPlayerFromGame(summonerIds));
            }
            catch (AggregateException ae)
            {
                lock (this.logFile)
                {
                    var flattenedAe = ae.Flatten();
                    this.logFile.WriterLogError(ae.ToString());
                    this.logFile.WriterFlush();
                }
            }
            lock (ServerManager.currentThreadsLock)
            {
                ServerManager.currentWebCalls--;
            }
        }

        private async void ProcessPlayerFromGame(string[] summonerIds) 
        {
            string summonerIdsParam = String.Join(",", summonerIds);
            string queryString = String.Format(ServerManager.summonerQueryTemplate, this.server.ToLower(),
                    this.server.ToLower(), summonerIdsParam, this.apiKey);
            try
            {
                this.VerifyThreadsCapacity();
                JObject summoners = await this.queryManager.MakeQuery(queryString);
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
                                    && this.ValidatePlayer(summonerId, tier))
                            {
                                lock (this.playersToProcess)
                                {
                                    this.playersToProcess.Add(summonerId, 1);
                                }
                                lock (this.playersFile)
                                {
                                    this.playersFile.WriteLine(summonerId);
                                    this.playersFile.WriterFlush();
                                }
                                lock (this.logFile)
                                {
                                    this.logFile.WriteLine(String.Format("{0} {1} ======== Player Qualified for Process: {2} ({3})", DateTime.Now.ToLongTimeString(),
                                        DateTime.Now.ToLongDateString(), summonerId, tier));
                                    this.logFile.WriterFlush();
                                }
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                lock (this.logFile)
                {
                    this.logFile.WriterLogError(e.ToString());
                    this.logFile.WriterFlush();
                }
            }
            lock (ServerManager.currentThreadsLock)
            {
                ServerManager.currentWebCalls--;
            }
        }

        private void RegisterGame(JObject game)
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
                this.gamesFile.WriteLine(gameId);
                this.gamesFile.WriterFlush();
            }
            lock (this.logFile)
            {
                this.logFile.WriteLine(String.Format("{0} {1} ======== Game Registered {2}", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString(), gameId));
                this.logFile.WriterFlush();
            }
        }

        private bool ValidatePlayer(string playerId, string tier) 
        {
            lock (this.playersToProcess) lock (this.playersUnderProcess) lock (this.playersProcessed)
            {
                return !this.playersToProcess.ContainsKey(playerId) && !this.playersUnderProcess.ContainsKey(playerId)
                    && !this.playersProcessed.ContainsKey(playerId) && qualifiedLeagues.Contains(tier.ToUpper()) ? true : false;
            }
        }

        private bool VerifyThreadsCapacity() 
        {
            bool canProceed = false;
            while (true)
            {
                lock (ServerManager.currentThreadsLock)
                {
                    if (ServerManager.currentWebCalls < ServerManager.maxServersThreads)
                    {
                        ServerManager.currentWebCalls ++;
                        canProceed = true;
                    }
                }
                if (!canProceed)
                {
                    lock (this.logFile)
                    {
                        this.logFile.WriteLine(String.Format("{0} {1} ======== System reached web call limit. Pausing...", DateTime.Now.ToLongTimeString(),
                            DateTime.Now.ToLongDateString()));
                        this.logFile.WriterFlush();
                    }
                    System.Threading.Thread.Sleep(ServerManager.cooldownInterval);
                }
                else
                {
                    return true;
                }
            }
        }
    }
}
