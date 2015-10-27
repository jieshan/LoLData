﻿using System;
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
        private static int batchCoolDownInterval = 200000;

        private static int playerBatchSize = 500;

        private static int cooldownInterval = 10000;

        private static int maxProcessQueueWaits = 6;

        private static int gamesProgressReport = 50;

        private static int maxServersThreads = 400;

        private static int maxPlayersPerQuery = 10;

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

        private Dictionary<string, int> playersInQueue;

        private Dictionary<string, int> playersUnderProcess;

        private Dictionary<string, int> playersProcessed;

        private Dictionary<string, int> playersInQuery;

        private Dictionary<string, int> playersDiscarded;

        private Dictionary<string, int> gamesProcessed;

        private List<string> newSummoners;

        private QueryManager queryManager;

        private string server;

        private string apiKey;

        private FileManager logFile;

        private FileManager playersFile;

        private FileManager gamesFile;

        private Object badRequestPlayersLock;

        private int badRequestPlayers;

        public ServerManager(string serverName, string apiKey) 
        {
            this.server = serverName;
            this.apiKey = apiKey;
            this.playersToProcess = new Dictionary<string, int>();
            this.playersInQueue = new Dictionary<string, int>();
            this.playersUnderProcess = new Dictionary<string, int>();
            this.playersProcessed = new Dictionary<string, int>();
            this.playersInQuery = new Dictionary<string, int>();
            this.playersDiscarded = new Dictionary<string, int>();
            this.gamesProcessed = new Dictionary<string, int>();
            this.newSummoners = new List<string>();
            this.badRequestPlayersLock = new Object();
            this.badRequestPlayers = 0;
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
                        if (ValidateNewPlayer(playerId))
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
            if (this.playersProcessed.Count == 0)
            {
                this.GetNextPlayerBatch();
            }
            if (remainingWaits == -1) 
            {
                remainingWaits = ServerManager.maxProcessQueueWaits;
            }
            while (true)
            {
                lock (this.playersInQueue)
                {
                    if (this.playersInQueue.Count == 0)
                    {
                        break;
                    }
                }               
                this.VerifyThreadsCapacity();
                this.ProcessNextPlayer();
                remainingWaits = ServerManager.maxProcessQueueWaits;
            }
            int playerUnderProcess;
            lock (this.playersUnderProcess)
            {
                playerUnderProcess = this.playersUnderProcess.Count;
            }
            if (playerUnderProcess == 0)
            {
                int playerToProcess;
                lock (this.playersToProcess)
                {
                    playerToProcess = this.playersToProcess.Count;
                }
                if (playerToProcess > 0)
                {
                    System.Diagnostics.Debug.WriteLine(String.Format("===== Batch completed. Breaking for {0} milliseconds.",
                   ServerManager.batchCoolDownInterval));
                    System.Threading.Thread.Sleep(ServerManager.batchCoolDownInterval);
                    this.GetNextPlayerBatch();
                }
                else if (playerToProcess == 0)
                {
                    this.ProcessQueryQueueRemainder();
                }
            }

            if (this.playersUnderProcess.Count == 0 && this.playersInQueue.Count == 0 && this.playersToProcess.Count == 0)
            {
                System.Threading.Thread.Sleep(ServerManager.cooldownInterval);
                remainingWaits -= 1;
                System.Diagnostics.Debug.WriteLine(String.Format("===== Remaining waits for more players to process: {0}.",
                   remainingWaits));
            }
            else if (this.playersUnderProcess.Count > 0) 
            {
                System.Threading.Thread.Sleep(ServerManager.cooldownInterval);
            }
            if (remainingWaits == 0)
            {
                System.Diagnostics.Debug.WriteLine("****************");
                System.Diagnostics.Debug.WriteLine(this.GetTotalValidPlayersCount());
                System.Diagnostics.Debug.WriteLine("****************");
                System.Diagnostics.Debug.WriteLine(this.GetTotalGamesCount());
                return true;
            }
            return this.ProcessAllPlayers(remainingWaits);
        }

        public int GetTotalValidPlayersCount()
        {
            // Only an approximate, yielding access to data processing work
            return this.playersToProcess.Count + 
                this.playersInQueue.Count +
                this.playersUnderProcess.Count + 
                this.playersProcessed.Count;
        }

        public int GetTotalGamesCount()
        {
            // Only an approximate, yielding access to data processing work
            return this.gamesProcessed.Count;
        }

        public void CloseAllFiles() 
        {
            this.logFile.WriterClose();
            this.playersFile.WriterClose();
            this.gamesFile.WriterClose();
        }

        private async void ProcessNextPlayer() 
        {
            string playerId = null;
            lock (this.playersInQueue) lock (this.playersUnderProcess)
            {
                if (this.playersInQueue.Count > 0)
                {
                    playerId = this.playersInQueue.Keys.ElementAt(0);
                    this.playersInQueue.Remove(playerId);
                    this.playersUnderProcess.Add(playerId, 1);
                }
                else 
                {
                    lock (ServerManager.currentThreadsLock)
                    {
                        ServerManager.currentWebCalls--;
                    }
                    return;
                }               
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
                        System.Diagnostics.Debug.WriteLine(String.Format("{0} {1} ==== Player {2} processed.", DateTime.Now.ToShortDateString(),
                            DateTime.Now.ToLongTimeString(), playerId));
                        System.Diagnostics.Debug.WriteLine(String.Format("{0} {1} ==== *Valid: {2} - Done: {3}. InProgress: {4}. InQueue: {5}. " +
                            "ToDo: {6}. *Invalid: {7}. *InQuery: {8}. *BadRequests: {9}.", DateTime.Now.ToShortDateString(), DateTime.Now.ToLongTimeString(),
                            this.GetTotalValidPlayersCount(), this.playersProcessed.Count, this.playersUnderProcess.Count, this.playersInQueue.Count, 
                            this.playersToProcess.Count, this.playersDiscarded.Count, this.playersInQuery.Count, this.badRequestPlayers));
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
            playerCount = this.playersProcessed.Count;
            if (playerCount % ServerManager.gamesProgressReport == 0)
            {
                System.Diagnostics.Debug.WriteLine(String.Format("======== {0} server now has processed {1} players.", 
                    this.server, playerCount));
                System.Diagnostics.Debug.WriteLine(String.Format("======== {0} server now has processed {1} games.",
                    this.server, this.GetTotalGamesCount()));
            }
            return;
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
            string summonerIdsParam = null;
            for (int i = 0; i < fellowPlayers.Count; i++ )
            {
                string summonerId = (string) ((JObject) fellowPlayers[i])["summonerId"];
                if (this.ValidateNewPlayer(summonerId))
                {
                    lock (this.playersInQuery) lock (this.newSummoners)
                    {
                        if(!this.playersInQuery.ContainsKey(summonerId))
                        {
                            this.playersInQuery.Add(summonerId, 1);
                            if (this.newSummoners.Count == ServerManager.maxPlayersPerQuery - 1)
                            {
                                this.newSummoners.Add(summonerId);
                                summonerIdsParam = String.Join(",", this.newSummoners);
                                this.newSummoners = new List<string>();
                            }
                            else if (this.newSummoners.Count < ServerManager.maxPlayersPerQuery)
                            {
                                this.newSummoners.Add(summonerId);
                            }
                            else
                            {
                                throw new Exception("Number of players in queue for by-entry query exceeds max limit.");
                            }
                        }                       
                    }
                }
            }
            if (summonerIdsParam != null)
            {
                try
                {
                    this.VerifyThreadsCapacity();
                    Task.Run(() => this.ProcessPlayerFromGame(summonerIdsParam));
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
            lock (ServerManager.currentThreadsLock)
            {
                ServerManager.currentWebCalls--;
            }
        }

        private async void ProcessPlayerFromGame(string summonerIdsParam) 
        {
            string queryString = String.Format(ServerManager.summonerQueryTemplate, this.server.ToLower(),
                    this.server.ToLower(), summonerIdsParam, this.apiKey);
            try
            {
                this.VerifyThreadsCapacity();
                JObject summoners = await this.queryManager.MakeQuery(queryString);
                if (summoners != null)
                {
                    string[] summonerIds = summonerIdsParam.Split(',');
                    for (int i = 0; i < summonerIds.Length; i++)
                    {
                        string summonerId = summonerIds[i];
                        JArray queues = (JArray)summoners[summonerId];
                        if (queues != null)
                        {
                            for (int j = 0; j < queues.Count; j++)
                            {
                                string tier = (string)((JObject)queues[j])["tier"];
                                if (((string)((JObject)queues[j])["queue"]).Equals(ServerManager.gameType)
                                        && this.ValidateNewPlayer(summonerId, true))
                                {
                                    if (ServerManager.qualifiedLeagues.Contains(tier))
                                    {
                                        lock (this.playersToProcess)
                                        {
                                            this.playersToProcess.Add(summonerId, 1);
                                        }
                                        lock (this.playersInQuery)
                                        {
                                            this.playersInQuery.Remove(summonerId);
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
                                    }
                                    else
                                    {
                                        lock (this.playersDiscarded)
                                        {
                                            this.playersDiscarded.Add(summonerId, 1);
                                        }
                                        lock (this.playersInQuery)
                                        {
                                            this.playersInQuery.Remove(summonerId);
                                        }
                                        lock (this.logFile)
                                        {
                                            this.logFile.WriteLine(String.Format("{0} {1} ======== Player Disqualified for Process: {2} ({3})", DateTime.Now.ToLongTimeString(),
                                                DateTime.Now.ToLongDateString(), summonerId, tier));
                                            this.logFile.WriterFlush();
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                        else
                        {
                            lock (this.badRequestPlayersLock)
                            {
                                this.badRequestPlayers ++;
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

        private void ProcessQueryQueueRemainder()
        {
            string summonerIdsParam = null;
            lock (this.newSummoners)
            {
                if (this.newSummoners.Count > 0)
                {
                    summonerIdsParam = String.Join(",", this.newSummoners);
                }
            }
            if (summonerIdsParam != null)
            {
                try
                {
                    this.VerifyThreadsCapacity();
                    Task.Run(() => this.ProcessPlayerFromGame(summonerIdsParam));
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
        }

        private void GetNextPlayerBatch()
        {
            lock (this.playersToProcess) lock (this.playersInQueue)
            {
                int toProcessPlayerCount = this.playersToProcess.Count;
                for (int i = 0; i < Math.Min(toProcessPlayerCount, ServerManager.playerBatchSize); i++)
                {
                    string nextSummonerId = this.playersToProcess.Keys.ElementAt(0);
                    this.playersToProcess.Remove(nextSummonerId);
                    this.playersInQueue.Add(nextSummonerId, 1);
                }
            }
        }

        private bool ValidateNewPlayer(string playerId, bool toAdd = false) 
        {
            if (!toAdd)
            {
                lock (this.playersToProcess) lock (this.playersInQueue) lock (this.playersUnderProcess)
                    lock (this.playersProcessed) lock (this.playersInQuery) lock (this.playersDiscarded)
                {
                    return !this.playersToProcess.ContainsKey(playerId) && !this.playersInQueue.ContainsKey(playerId)
                        && !this.playersUnderProcess.ContainsKey(playerId) && !this.playersProcessed.ContainsKey(playerId)
                        && !this.playersInQuery.ContainsKey(playerId) && !this.playersDiscarded.ContainsKey(playerId)
                        ? true : false;
                }
            }
            else 
            {
                lock (this.playersToProcess) lock (this.playersInQueue) lock (this.playersUnderProcess)
                    lock (this.playersProcessed) lock (this.playersDiscarded)
                {
                    return !this.playersToProcess.ContainsKey(playerId) && !this.playersInQueue.ContainsKey(playerId)
                        && !this.playersUnderProcess.ContainsKey(playerId) && !this.playersProcessed.ContainsKey(playerId)
                        && !this.playersDiscarded.ContainsKey(playerId)
                        ? true : false;
                }
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
