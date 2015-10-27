using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Net;
using System.IO;

namespace LoLData.DataCollection
{
    public class QueryManager
    {
        private static int clientTimeout = 600000;

        private static int rateLimitBuffer = 1210;

        private static int maxQueryRetry = 3;

        private static string retryHeader = "Retry-After";

        private HttpClient httpClient;

        private Object queryLock;

        private StreamWriter logFile;

        private int globalRetryAfter;

        public QueryManager(StreamWriter logFile, string apiRootDomain) 
        {
            this.httpClient = new HttpClient();
            this.httpClient.Timeout = TimeSpan.FromMilliseconds(QueryManager.clientTimeout);
            this.queryLock = new Object();
            this.logFile = logFile;
            this.globalRetryAfter = 0;
        }

        public async Task<JObject> MakeQuery(string queryString, int retry = 0) 
        {
            JObject result = null;
            // Only a loose rate limiting management, because of async calls
            lock (queryLock)
            {
                System.Threading.Thread.Sleep(this.globalRetryAfter);
            }

            var httpResponse = await QueryManager.ExecuteQuery(this.httpClient, queryString);
            int statusCode = (int) httpResponse.StatusCode;
            lock(ServerManager.currentThreadsLock){
                ServerManager.currentWebCalls --;
            }
            System.Diagnostics.Debug.WriteLine(String.Format("{0} ==== Query: {1} {2}", DateTime.Now.ToLongTimeString(),
                statusCode, queryString));
            if (statusCode == 429)
            {
                int retryAfter = int.Parse(httpResponse.Headers.GetValues(QueryManager.retryHeader).FirstOrDefault()) * 1000;
                System.Diagnostics.Debug.WriteLine(String.Format("== Rate Limit hit. Retry after {0} milliseconds. Query: {1}",
                    retryAfter, queryString));
                lock (queryLock)
                {
                    this.globalRetryAfter = retryAfter;
                }

            }

            var responseContent = await QueryManager.ReadResponseContent(httpResponse);
            lock (this.logFile)
            {
                this.logFile.WriteLine(String.Format("{0} {1} == Query: {2} {3}", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString(), statusCode, queryString));
                this.logFile.Flush();
            }
            if (statusCode == 200)
            {
                result = JObject.Parse(responseContent);
                lock (queryLock)
                {
                    this.globalRetryAfter = QueryManager.rateLimitBuffer;
                }
            }
            else if (retry < QueryManager.maxQueryRetry)
            {
                return await MakeQuery(queryString, retry + 1);
            }                
            return result;
        }

        private static async Task<HttpResponseMessage> ExecuteQuery(HttpClient client, string uri)
        {
            HttpResponseMessage response = await client.GetAsync(uri);
            return response;
        }

        private static async Task<string> ReadResponseContent(HttpResponseMessage response)
        {
            string content = await response.Content.ReadAsStringAsync();
            return content;
        }
    }
}
