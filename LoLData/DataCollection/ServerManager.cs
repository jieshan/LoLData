using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using LoLData.DataCollection;
using System.IO;

namespace LoLData
{
    public class ServerManager
    {
        public enum Subject
        {
            Player,
            Game
        };

        private static int batchCoolDownInterval = 200000;

        private static int batchSize = 500;

        private static int cooldownInterval = 10000;

        private static int maxProcessQueueWaits = 6;

        private static int gamesProgressReport = 50;

        private static int maxServersThreads = 400;

        private static int maxPlayersPerQuery = 10;

        private static string logFilePathTemplate = "..\\..\\CachedData\\{0}log.txt";

        private static string playersFilePathTemplate = "..\\..\\CachedData\\{0}players.txt";

        private static string gamesFilePathTemplate = "..\\..\\CachedData\\{0}games.txt";

        private static string inFilePathTemplate = "..\\..\\CachedData\\{0}.txt";

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

        private Dictionary<string, Dictionary<string, int>> playersJournal;

        private Dictionary<string, int> gamesToProcess;

        private Dictionary<string, int> gamesInQueue;

        private Dictionary<string, int> gamesUnderProcess;

        private Dictionary<string, int> gamesProcessed;

        private Dictionary<string, Dictionary<string, int>> gamesJournal;

        private List<string> newSummoners;

        private QueryManager queryManager;

        private string server;

        private string apiKey;

        private StreamWriter logFile;

        private StreamWriter playersFile;

        private StreamWriter gamesFile;

        private Object badRequestPlayersLock;

        private int badRequestPlayers;

        private string inFilePrefix;

        private string currentDirectory;

        public ServerManager(string serverName, string apiKey, string inFilePrefix = null, string outFilePrefix = null, string gameType = null) 
        {
            this.server = serverName;
            this.apiKey = apiKey;

            this.playersToProcess = new Dictionary<string, int>();
            this.playersInQueue = new Dictionary<string, int>();
            this.playersUnderProcess = new Dictionary<string, int>();
            this.playersProcessed = new Dictionary<string, int>();
            this.playersInQuery = new Dictionary<string, int>();
            this.playersDiscarded = new Dictionary<string, int>();
            this.playersJournal = new Dictionary<string, Dictionary<string, int>> 
            {
                {"ToProcess", this.playersToProcess},
                {"InQueue", this.playersInQueue},
                {"UnderProcess", this.playersUnderProcess},
                {"Processed", this.playersProcessed},
                {"InQuery", this.playersInQuery},
                {"Discarded", this.playersDiscarded}
            };

            this.gamesToProcess = new Dictionary<string, int>();
            this.gamesInQueue = new Dictionary<string, int>();
            this.gamesUnderProcess = new Dictionary<string, int>();
            this.gamesProcessed = new Dictionary<string, int>();
            this.gamesJournal = new Dictionary<string, Dictionary<string, int>> 
            {
                {"ToProcess", this.gamesToProcess},
                {"InQueue", this.gamesInQueue},
                {"UnderProcess", this.gamesUnderProcess},
                {"Processed", this.gamesProcessed}
            };

            this.newSummoners = new List<string>();
            this.badRequestPlayersLock = new Object();
            this.badRequestPlayers = 0;
            if (inFilePrefix != null)
            {
                this.inFilePrefix = inFilePrefix;
            }
            if (gameType != null)
            {
                ServerManager.gameType = gameType;
            }
            this.currentDirectory = Directory.GetCurrentDirectory();
            this.logFile = new StreamWriter(@Path.Combine(currentDirectory,
                String.Format(ServerManager.logFilePathTemplate, outFilePrefix == null ? this.server : outFilePrefix)));
            lock (this.logFile)
            {
                this.logFile.WriteLine(String.Format("{0} {1} ======== Log file created.", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString()));
            }
            this.playersFile = new StreamWriter(@Path.Combine(currentDirectory,
                String.Format(ServerManager.playersFilePathTemplate, outFilePrefix == null ? this.server : outFilePrefix)));
            this.gamesFile = new StreamWriter(@Path.Combine(currentDirectory,
                String.Format(ServerManager.gamesFilePathTemplate, outFilePrefix == null ? this.server : outFilePrefix)));
            this.queryManager = new QueryManager(this.logFile, String.Format(ServerManager.serverRootDomainTemplate, 
                outFilePrefix == null ? this.server : outFilePrefix));
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
                        FileHelper.WriterLogError(this.logFile, e.ToString());
                        this.logFile.Flush();
                    }
                }
                finally 
                {
                    this.logFile.Flush();
                    this.playersFile.Flush();
                }
            }          
        }

        public void loadDataFromFile(Subject subject = Subject.Player) 
        {
            StreamReader reader = new StreamReader(@Path.Combine(this.currentDirectory,
                String.Format(ServerManager.inFilePathTemplate, this.inFilePrefix)));
            string line;
            int count = 0;
            while ((line = reader.ReadLine()) != null)
            {
                string[] fields = line.Split(',');
                switch (subject)
                {
                    case Subject.Game:
                        lock (this.gamesToProcess) 
                        {
                            this.gamesToProcess.Add(fields[0], 1);
                        }
                        break;
                    case Subject.Player:
                    default:
                        lock (this.playersToProcess)
                        {
                            this.playersToProcess.Add(fields[0], 1);
                        }
                        break;
                }
                count ++;
                if (count % 2000 == 0)
                {
                    System.Diagnostics.Debug.WriteLine(String.Format("===== {0} entries of {1} have been loaded.",
                        count, subject == Subject.Game ? "games" : subject == Subject.Player ? "players" : "records"));
                }
            }
            reader.Close();
        }

        public bool ProcessAll(Subject subject = Subject.Player, bool recursiveSearch = true, int remainingWaits = -1) 
        {
            Dictionary<string, Dictionary<string, int>> journal = null;
            switch (subject)
            {
                case Subject.Game:
                    journal = this.gamesJournal;
                    break;
                case Subject.Player:
                default:
                    journal = this.playersJournal;
                    break;
            }
            if (journal["Processed"].Count == 0)
            {
                this.GetNextBatch(journal, subject);
            }
            if (remainingWaits == -1) 
            {
                remainingWaits = ServerManager.maxProcessQueueWaits;
            }
            while (true)
            {
                lock (journal["InQueue"])
                {
                    if (journal["InQueue"].Count == 0)
                    {
                        break;
                    }
                }               
                this.VerifyThreadsCapacity();
                if (subject == Subject.Player)
                {
                    this.ProcessNextPlayer(recursiveSearch);
                }
                else if (subject == Subject.Game)
                {
                    // TODO
                }
                else
                {
                    ServerManager.releaseOneThread();
                }
                
                remainingWaits = ServerManager.maxProcessQueueWaits;
            }
            int underProcess;
            lock (journal["UnderProcess"])
            {
                underProcess = journal["UnderProcess"].Count;
            }
            if (underProcess == 0)
            {
                int toProcess;
                lock (journal["ToProcess"])
                {
                    toProcess = journal["ToProcess"].Count;
                }
                if (toProcess > 0)
                {
                    System.Diagnostics.Debug.WriteLine(String.Format("===== Batch completed. Breaking for {0} milliseconds.",
                   ServerManager.batchCoolDownInterval));
                    System.Threading.Thread.Sleep(ServerManager.batchCoolDownInterval);
                    this.GetNextBatch(journal, subject);
                }
                else if (toProcess == 0)
                {
                    if (subject == Subject.Player)
                    {
                        this.ProcessQueryQueueRemainder();
                    }
                    else if (subject == Subject.Game)
                    {
                        // TODO
                    }
                    else
                    {
                    }
                }
            }

            if (journal["UnderProcess"].Count == 0 && journal["InQueue"].Count == 0 && journal["ToProcess"].Count == 0
                && subject == Subject.Game ? this.playersToProcess.Count == 0 && this.playersInQueue.Count == 0 
                && this.playersUnderProcess.Count == 0 : true)
            {
                System.Threading.Thread.Sleep(ServerManager.cooldownInterval);
                remainingWaits -= 1;
                System.Diagnostics.Debug.WriteLine(String.Format("===== Remaining waits for more players to process: {0}.",
                   remainingWaits));
            }
            else if (journal["UnderProcess"].Count > 0) 
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
            return this.ProcessAll(subject, recursiveSearch, remainingWaits);
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
            return this.gamesToProcess.Count +
                this.gamesInQueue.Count +
                this.gamesUnderProcess.Count +
                this.gamesProcessed.Count; ;
        }

        public void CloseAllFiles() 
        {
            this.logFile.Close();
            this.playersFile.Close();
            this.gamesFile.Close();
        }

        private async void ProcessNextPlayer(bool recursiveSearch) 
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
                    ServerManager.releaseOneThread();
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
                            Task.Run(() => ProcessGame(game, recursiveSearch));
                        }
                        catch (AggregateException ae)
                        {
                            lock (this.logFile)
                            {
                                var flattenedAe = ae.Flatten();
                                FileHelper.WriterLogError(this.logFile, ae.ToString());
                                this.logFile.Flush();
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
                    FileHelper.WriterLogError(this.logFile, e.ToString());
                    this.logFile.Flush();
                }
            }
            ServerManager.releaseOneThread();
            int playerCount;
            playerCount = this.playersProcessed.Count;
            if (playerCount % ServerManager.gamesProgressReport == 0)
            {
                System.Diagnostics.Debug.WriteLine(String.Format("======== {0} server now has processed {1} players.", 
                    this.server, playerCount));
                System.Diagnostics.Debug.WriteLine(String.Format("======== {0} server now has {1} games on record.",
                    this.server, this.GetTotalGamesCount()));
            }
            return;
        }

        private void ProcessGame(JObject game, bool recursiveSearch)
        {
            if (((string) game["subType"]).Equals(ServerManager.gameType))
            {
                RegisterGame(game);
            }
            if (!recursiveSearch)
            {
                ServerManager.releaseOneThread();
                return;
            }
            JArray fellowPlayers = (JArray) game["fellowPlayers"];
            if(fellowPlayers == null)
            {
                ServerManager.releaseOneThread();
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
                        FileHelper.WriterLogError(this.logFile, ae.ToString());
                        this.logFile.Flush();
                    }
                }
            }
            ServerManager.releaseOneThread();
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
                                            this.playersFile.Flush();
                                        }
                                        lock (this.logFile)
                                        {
                                            this.logFile.WriteLine(String.Format("{0} {1} ======== Player Qualified for Process: {2} ({3})", 
                                                DateTime.Now.ToLongTimeString(), DateTime.Now.ToLongDateString(), summonerId, tier));
                                            this.logFile.Flush();
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
                                            this.logFile.WriteLine(String.Format("{0} {1} ======== Player Disqualified for Process: {2} ({3})", 
                                                DateTime.Now.ToLongTimeString(), DateTime.Now.ToLongDateString(), summonerId, tier));
                                            this.logFile.Flush();
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
                    FileHelper.WriterLogError(this.logFile, e.ToString());
                    this.logFile.Flush();
                }
            }
            ServerManager.releaseOneThread();
        }

        private void RegisterGame(JObject game)
        {
            // TODO: Game analysis implementation
            string gameId = null;
            gameId = (string) game["gameId"];
            if (ValidateNewGame(gameId))
            {
                switch (ServerManager.gameType)
                {
                    case "ONEFORALL_5x5":
                        this.gamesProcessed.Add((string)game["gameId"], 1);
                        JObject stats = (JObject) game["stats"];
                        int playerChampId = (int)game["championId"];
                        int timePlayed = (int) stats["timePlayed"];
                        bool playerWin = (bool)stats["win"];
                        int otherChampId = -1;
                        JArray fellowPlayers = (JArray) game["fellowPlayers"];
                        for (int i = 0; i < fellowPlayers.Count; i++)
                        {
                            otherChampId = (int)((JObject)fellowPlayers[i])["championId"];
                            if (otherChampId != playerChampId)
                            {
                                break;
                            }
                        }
                        lock (this.gamesFile)
                        {
                            if(otherChampId != -1)
                            {
                                this.gamesFile.WriteLine(string.Join(",", gameId, playerChampId, otherChampId, playerWin, timePlayed));
                                this.gamesFile.Flush();
                            }
                        }
                        break;
                    case "RANKED_SOLO_5x5":
                    default:
                        this.gamesToProcess.Add((string)game["gameId"], 1);
                        lock (this.gamesFile)
                        {
                            this.gamesFile.WriteLine(gameId);
                            this.gamesFile.Flush();
                        }
                        break;
                }         
            }               
            lock (this.logFile)
            {
                this.logFile.WriteLine(String.Format("{0} {1} ======== Game Registered {2}", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString(), gameId));
                this.logFile.Flush();
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
                        FileHelper.WriterLogError(this.logFile, ae.ToString());
                        this.logFile.Flush();
                    }
                }
            }
        }

        private void GetNextBatch(Dictionary<string, Dictionary<string, int>> journal, Subject subject)
        {
            lock (journal["ToProcess"]) lock (journal["InQueue"])
            {
                int toProcessCount = journal["ToProcess"].Count;
                for (int i = 0; i < Math.Min(toProcessCount, ServerManager.batchSize); i++)
                {
                    string nextId = journal["ToProcess"].Keys.ElementAt(0);
                    journal["ToProcess"].Remove(nextId);
                    journal["InQueue"].Add(nextId, 1);
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

        private bool ValidateNewGame(string gameId)
        {
            lock (this.gamesToProcess) lock (this.gamesInQueue) lock (this.gamesUnderProcess)
                lock (this.gamesProcessed)
            {
                return !this.gamesToProcess.ContainsKey(gameId) && !this.gamesInQueue.ContainsKey(gameId)
                    && !this.gamesUnderProcess.ContainsKey(gameId) && !this.gamesProcessed.ContainsKey(gameId)
                    ? true : false;
            }
        }

        private bool VerifyThreadsCapacity() 
        {
            bool canProceed = false;
            while (true)
            {
                lock (ServerManager.currentThreadsLock)
                {
                    if (ServerManager.currentWebCalls < ServerManager.maxServersThreads && 
                        this.queryManager.globalRetryAfter <= QueryManager.rateLimitBuffer)
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

        public static void releaseOneThread() 
        {
            lock (ServerManager.currentThreadsLock)
            {
                ServerManager.currentWebCalls--;
            }
        }
    }
}
