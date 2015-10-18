using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LoLData.DataCollection
{
    public class QueryManager
    {
        private static int rateLimitBuffer = 1200;

        private static int webQueryTimeLimit = 600000;

        private static string retryHeader = "Retry-After";

        private HttpClient httpClient;

        private Object queryLock;

        private FileManager logFile;

        private int looseGlobalRetryAfter;

        public QueryManager(FileManager logFile) 
        {
            this.httpClient = new HttpClient();
            this.queryLock = new Object();
            this.logFile = logFile;
            this.looseGlobalRetryAfter = 0;
        }

        public JObject makeQuery(string queryString) 
        {
            int statusCode = -1;
            JObject result = null;
            string responseJson = null;
            int retryAfter = 0;
            lock (queryLock)
            {
                if (this.looseGlobalRetryAfter > 0)
                {
                    // Other threads hit rate limiting
                    System.Threading.Thread.Sleep(this.looseGlobalRetryAfter);
                }
                else
                {
                    // Rate limiting requirement
                    System.Threading.Thread.Sleep(QueryManager.rateLimitBuffer);
                }
            }
            Task fetchJson = Task.Run(() => {
                var httpResponse = QueryManager.executeQuery(this.httpClient, queryString);
                statusCode = (int) httpResponse.Result.StatusCode;
                var responseContent = QueryManager.readResponseContent(httpResponse.Result);
                responseJson = responseContent.Result;
                IEnumerable<string> retryHeaderValue;
                if (httpResponse.Result.Headers.TryGetValues(QueryManager.retryHeader, out retryHeaderValue))
                {
                    retryAfter = int.Parse(retryHeaderValue.FirstOrDefault()) * 1000;
                }
            });
            fetchJson.Wait(QueryManager.webQueryTimeLimit);
            System.Diagnostics.Debug.WriteLine(String.Format("{0} ==== Query: {1} {2}", DateTime.Now.ToLongTimeString(), 
                statusCode, queryString));
            lock (this.logFile)
            {
                this.logFile.writeLine(String.Format("{0} {1} == Query: {2} {3}", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString(), statusCode, queryString));
                this.logFile.writerFlush();
            }
            if (statusCode == 200)
            {
                result = JObject.Parse(responseJson);
            }
            else
            {
                if (statusCode == 429)
                {           
                    System.Diagnostics.Debug.WriteLine(String.Format("== Rate Limit hit. Retry after {0} milliseconds. Query: {1}",
                        retryAfter, queryString));
                    lock (queryLock)
                    {
                        this.looseGlobalRetryAfter = retryAfter;
                        // Extra buffer time in case of accidental hitting rate limiting
                        System.Threading.Thread.Sleep(retryAfter);
                        this.looseGlobalRetryAfter = 0;
                    }
                
                }
                return makeQuery(queryString);
            }
            return result;
        }

        private static async Task<HttpResponseMessage> executeQuery(HttpClient client, string uri)
        {
            HttpResponseMessage response = await client.GetAsync(uri).ConfigureAwait(false);
            return response;
        }

        private static async Task<string> readResponseContent(HttpResponseMessage response)
        {
            string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return content;
        }
    }
}
