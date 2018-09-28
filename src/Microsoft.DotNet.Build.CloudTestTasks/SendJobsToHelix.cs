// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Text;
using Microsoft.Build.Utilities;
using System.Threading;

namespace Microsoft.DotNet.Build.CloudTestTasks
{
    // This is very similar to what SendToHelix does, with one difference:
    // It accepts JSON containing an array of job objects, and deserializes them to a strongly typed object.
    // This gives us error checking before pushing to Helix, and ensures "old" code does not break.
    // We should eventually refactor both to use shared functionality.
    public sealed class SendJobsToHelix : Microsoft.Build.Utilities.Task
    {
        /// <summary>
        /// Api endpoint for sending jobs.
        /// e.g. https://helix.dot.net/api/2017-03-34/jobs
        /// </summary>
        [Required]
        public string ApiEndpoint { get; set; }

        /// <summary>
        /// Access token for API. To obtain, see the profile on the server corresponding to the API endpoint.
        /// e.g. https://helix.dot.net/UserProfile
        /// </summary>
        public string AccessToken { get; set; }

        /// <summary>
        /// The event data path used to form the body stream.
        /// </summary>
        [Required]
        public string EventDataPath { get; set; }

        /// <summary>
        /// Once a helix job is started, this is the identifier of that job
        /// </summary>
        [Output]
        public ITaskItem [] JobIds { get; set; }

        public override bool Execute()
        {
            return ExecuteAsync().GetAwaiter().GetResult();
        }

        private async Task<bool> ExecuteAsync()
        {
            List<ITaskItem> jobIds = new List<ITaskItem>();
            string apiUrl = ApiEndpoint;
            if (!String.IsNullOrEmpty(AccessToken))
            {
                string joinCharacter = ApiEndpoint.Contains("?") ? "&" : "?";
                apiUrl = ApiEndpoint + joinCharacter + "access_token=" + Uri.EscapeDataString(AccessToken);
            }

            Log.LogMessage(MessageImportance.Normal, "Posting job to {0}", ApiEndpoint);
            Log.LogMessage(MessageImportance.Low,    "Using Job Event json from ", EventDataPath);

            string buildJsonText = File.ReadAllText(EventDataPath);

            List<JObject> allBuilds = new List<JObject>();
            try
            {
                allBuilds.AddRange(JsonConvert.DeserializeObject<List<JObject>>(buildJsonText));
            }
            catch (JsonSerializationException)
            {
                // If this fails, we'll let it tear us down.  
                // Since if this isn't even valid JSON there's no use posting it.
                allBuilds.Add(JsonConvert.DeserializeObject<JObject>(buildJsonText));
            }

            using (HttpClient client = new HttpClient()
            {
                Timeout = TimeSpan.FromSeconds(30) // Default is 100 seconds.  15 timeouts @ 30 seconds = ~7:30
            })
            {
                const int MaxAttempts = 15;
                // add a bit of randomness to the retry delay
                var rng = new Random();

                // We'll use this to be sure TaskCancelledException comes from timeouts
                CancellationTokenSource cancelTokenSource = new CancellationTokenSource();

                foreach (JObject jobStartMessage in allBuilds)
                {
                    int retryCount = MaxAttempts;
                    bool keepTrying = true;
                    string queueId = (string)jobStartMessage["QueueId"];
                    if (string.IsNullOrEmpty(queueId))
                    {
                        Log.LogError("Helix Job Start messages must have a value for 'QueueId' ");
                        keepTrying = false; // this will fail in the API, so we won't even try.
                    }
                    // Provides a way for the API to realize that a given job has been recently queued
                    // which allows us to retry in the case of ambiguous results such as HttpClient timeout.
                    string jobStartIdentifier = Guid.NewGuid().ToString("N");
                    jobStartMessage["JobStartIdentifier"] = jobStartIdentifier;
                    Log.LogMessage(MessageImportance.Low, $"Sending job start with identifier '{jobStartIdentifier}'");

                    while (keepTrying)
                    {
                        HttpResponseMessage response = new HttpResponseMessage();
                        try
                        {
                            // This workaround to get the HTTPContent is to work around that StringContent doesn't allow application/json
                            HttpContent contentStream = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(jobStartMessage.ToString())));
                            contentStream.Headers.Add("Content-Type", "application/json");
                            response = await client.PostAsync(apiUrl, contentStream, cancelTokenSource.Token);

                            if (response.IsSuccessStatusCode)
                            {
                                JObject responseObject = new JObject();
                                using (Stream stream = await response.Content.ReadAsStreamAsync())
                                using (StreamReader streamReader = new StreamReader(stream))
                                {
                                    string jsonResponse = streamReader.ReadToEnd();
                                    try
                                    {
                                        using (JsonReader jsonReader = new JsonTextReader(new StringReader(jsonResponse)))
                                        {
                                            responseObject = JObject.Load(jsonReader);
                                        }
                                    }
                                    catch
                                    {
                                        Log.LogWarning($"Hit exception attempting to parse JSON response.  Raw response string: {Environment.NewLine} {jsonResponse}");
                                    }
                                }

                                string jobId = (string)responseObject["Name"];
                                if (String.IsNullOrEmpty(jobId))
                                {
                                    Log.LogError("Publish to '{0}' did not return a job ID", ApiEndpoint);
                                }
                                TaskItem helixJobStartedInfo = new TaskItem(jobId);
                                helixJobStartedInfo.SetMetadata("CorrelationId", jobId);
                                helixJobStartedInfo.SetMetadata("QueueId", queueId);
                                helixJobStartedInfo.SetMetadata("QueueTimeUtc", DateTime.UtcNow.ToString());
                                jobIds.Add(helixJobStartedInfo);

                                Log.LogMessage(MessageImportance.High, "Started Helix job: CorrelationId = {0}", jobId);
                                keepTrying = false;
                            }
                            else
                            {
                                string responseContent = await response.Content.ReadAsStringAsync();
                                Log.LogWarning($"Helix Api Response: StatusCode {response.StatusCode} {responseContent}");
                            }
                        }
                        // still allow other types of exceptions to tear down the task for now
                        catch (HttpRequestException toLog)
                        {
                            Log.LogWarning("Exception thrown attempting to submit job to Helix:");
                            Log.LogWarningFromException(toLog, true);
                        }
                        catch (TaskCanceledException possibleClientTimeout)
                        {
                            if (possibleClientTimeout.CancellationToken != cancelTokenSource.Token)
                            {
                                // This is a timeout.  Since we provided a JobIdentifier value, we can retry.
                                Log.LogWarning($"HttpClient timeout while attempting to POST new job to '{queueId}', will retry. Job Start Identifier: {jobStartIdentifier}");
                            }
                            else
                            {
                                // Something else caused cancel, throw it.  Should not ever get here.
                                throw;
                            }
                        }
                        if (retryCount-- <= 0)
                        {
                            Log.LogError($"Unable to publish to '{ApiEndpoint}' after {MaxAttempts} retries. Received status code: {response.StatusCode} {response.ReasonPhrase}");
                            keepTrying = false;
                        }
                        if (keepTrying)
                        {
                            Log.LogWarning("Failed to publish to '{0}', {1} retries remaining", ApiEndpoint, retryCount);
                            int delay = (MaxAttempts - retryCount) * rng.Next(1, 7);
                            await System.Threading.Tasks.Task.Delay(delay * 1000);
                        }
                    }
                }
                JobIds = jobIds.ToArray();
                // Number of queued builds = number in that file == success.
                // If any timeouts or other failures occur this will cause the task to fail.
                return allBuilds.Count == jobIds.Count;
            }
        }
    }
}