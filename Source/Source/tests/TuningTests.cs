using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using grapher.AI;
using Newtonsoft.Json.Linq;

internal class TuningTests
{
    static int passed;
    static void Check(bool ok, string name) { if (!ok) throw new Exception("FAILED: " + name); passed++; Console.WriteLine("PASS: " + name); }
    static void Reject(Action action, string name)
    {
        try { action(); } catch { Check(true, name); return; }
        throw new Exception("FAILED (accepted invalid data): " + name);
    }
    static JObject Profile(JObject baseline) { return (JObject)baseline["profiles"][0].DeepClone(); }
    static void Main(string[] args)
    {
        var baseline = JObject.Parse(DriverConfig.GetDefault().ToJSON());
        var snapshot = baseline.DeepClone();
        File.WriteAllText(args[0], baseline.ToString());
        var profile = Profile(baseline);
        profile["Whole or horizontal accel parameters"]["mode"] = "classic";
        profile["Whole or horizontal accel parameters"]["acceleration"] = 0.3;
        profile["Whole or horizontal accel parameters"]["inputOffset"] = 2.0;
        profile["Whole or horizontal accel parameters"]["Cap mode"] = "output";
        profile["Whole or horizontal accel parameters"]["Cap / Jump"]["y"] = 2.0;
        var next = TuningProposal.Merge(baseline, profile);
        var edits = new JObject {
            ["changes"] = new JArray(
                new JObject { ["field"] = "x.acceleration", ["value"] = 0.3 },
                new JObject { ["field"] = "x.inputOffset", ["value"] = 2.0 },
                new JObject { ["field"] = "x.capOutput", ["value"] = 2.0 }),
            ["modes"] = new JArray(new JObject { ["axis"] = "x", ["mode"] = "classic" }),
            ["caps"] = new JArray(new JObject { ["axis"] = "x", ["mode"] = "output" })
        };
        Check((double)next["profiles"][0]["Whole or horizontal accel parameters"]["acceleration"] == 0.3, "baseline classic curve accepted by native validator");
        Check(JToken.DeepEquals(baseline["devices"], next["devices"]), "device assignments preserved");
        Check(JToken.DeepEquals(baseline["defaultDeviceConfig"], next["defaultDeviceConfig"]), "device defaults preserved");
        Check(!JToken.DeepEquals(baseline["profiles"][0], next["profiles"][0]) && JToken.DeepEquals(snapshot, baseline), "proposal does not mutate baseline");
        Check(TuningProposal.Diff(baseline, next).Contains("acceleration"), "preview reports changed fields");
        Reject(() => TuningProposal.Parse("{\"a\":1,\"a\":2}"), "duplicate JSON fields rejected");
        Reject(() => TuningProposal.Parse("```json\n{}\n```"), "markdown response rejected");
        Reject(() => TuningProposal.Parse(new string('x', 256 * 1024 + 1)), "oversized response rejected");
        var bad = Profile(baseline); bad.Remove("Output DPI");
        Reject(() => TuningProposal.Merge(baseline, bad), "missing fields rejected");
        bad = Profile(baseline); bad["extra"] = 1;
        Reject(() => TuningProposal.Merge(baseline, bad), "unknown fields rejected");
        bad = Profile(baseline); bad["name"] = "changed";
        Reject(() => TuningProposal.Merge(baseline, bad), "profile identity protected");
        bad = Profile(baseline); bad["Output DPI"] = "1600";
        Reject(() => TuningProposal.Merge(baseline, bad), "numeric strings rejected");
        bad = Profile(baseline); bad["Output DPI"] = double.NaN;
        Reject(() => TuningProposal.Merge(baseline, bad), "non-finite values rejected");
        bad = Profile(baseline); bad["Whole or horizontal accel parameters"]["mode"] = "invented";
        Reject(() => TuningProposal.Merge(baseline, bad), "invalid mode rejected by native converter");
        bad = Profile(baseline); bad["Whole or horizontal accel parameters"]["data"] = new JArray(0, 1);
        Reject(() => TuningProposal.Merge(baseline, bad), "LUT array changes rejected");
        var coachEdits = (JObject)edits.DeepClone(); coachEdits["explanation"] = "Keep the base steady; preview a small flick-speed adjustment.";
        var handler = new MockHandler(coachEdits.ToString());
        using (var client = new HttpClient(handler))
        {
            var tuner = new GeminiTuner(client);
            var proposed = tuner.ProposeAsync("test-key", "gemini-test", "boost fast flicks", baseline, CancellationToken.None).GetAwaiter().GetResult();
            Check(JToken.DeepEquals(proposed, next), "mock Gemini response produces validated full settings");
            Check(tuner.LastExplanation == (string)coachEdits["explanation"], "coach rationale survives the API response without entering driver JSON");
            Check(handler.HeaderKey && !handler.Url.Contains("test-key"), "key sent in header, excluded from URL");
            Check(!handler.Body.Contains("\"devices\"") && handler.Body.Contains("deviceConfig"), "only default profile and device defaults sent");
            var requestBody = JObject.Parse(handler.Body);
            Check((string)requestBody["generationConfig"]["responseMimeType"] == "application/json" && requestBody["generationConfig"]["responseSchema"] == null, "request uses compatible JSON mode without provider schema constraints");
            Check(((string)requestBody["contents"][0]["parts"][0]["text"]).Contains("x.capOutput"), "request supplies known editable setting aliases");
            handler.FinishReason = "MAX_TOKENS";
            Reject(() => tuner.ProposeAsync("test-key", "gemini-test", "test", baseline, CancellationToken.None).GetAwaiter().GetResult(), "truncated model response rejected");
            Check(string.IsNullOrEmpty(tuner.LastExplanation), "failed coach request clears prior response rationale");
            handler.Status = (HttpStatusCode)429;
            Reject(() => tuner.ProposeAsync("test-key", "gemini-test", "test", baseline, CancellationToken.None).GetAwaiter().GetResult(), "API rate limit handled without proposal");
            Reject(() => tuner.ProposeAsync("test-key", "../evil", "test", baseline, CancellationToken.None).GetAwaiter().GetResult(), "invalid model endpoint rejected");
        }
        Func<TimeSpan, CancellationToken, Task> noDelay = (duration, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(0); };
        Func<HttpResponseMessage> success = () => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(new JObject { ["candidates"] = new JArray(new JObject {
                ["finishReason"] = "STOP", ["content"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = edits.ToString() }) }
            }) }.ToString())
        };
        var recovered = new SequenceHandler((attempt, token) => {
            if (attempt == 1) throw new TaskCanceledException("raw transport cancellation");
            return Task.FromResult(success());
        });
        using (var client = new HttpClient(recovered))
        {
            var result = new GeminiTuner(client, TimeSpan.FromSeconds(1), noDelay).ProposeAsync("test-key", "gemini-test", "test", baseline, CancellationToken.None).GetAwaiter().GetResult();
            Check(recovered.Calls == 2 && JToken.DeepEquals(result, next), "a timed-out attempt retries once and can recover");
        }
        var timedOut = new SequenceHandler(async (attempt, token) => { await Task.Delay(-1, token); return success(); });
        using (var client = new HttpClient(timedOut))
        {
            try {
                new GeminiTuner(client, TimeSpan.FromMilliseconds(10), noDelay).ProposeAsync("test-key", "gemini-test", "test", baseline, CancellationToken.None).GetAwaiter().GetResult();
                throw new Exception("Timeout was ignored");
            }
            catch (TimeoutException ex) { Check(timedOut.Calls == 2 && ex.Message.Contains("Fast model") && !ex.Message.Contains("A task was canceled"), "bounded timeout produces an actionable message after two attempts"); }
        }
        using (var userCancel = new CancellationTokenSource())
        {
            var canceled = new SequenceHandler((attempt, token) => { userCancel.Cancel(); throw new OperationCanceledException(userCancel.Token); });
            using (var client = new HttpClient(canceled))
            {
                try { new GeminiTuner(client, null, noDelay).ProposeAsync("test-key", "gemini-test", "test", baseline, userCancel.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation was ignored"); }
                catch (OperationCanceledException) { Check(canceled.Calls == 1, "user cancellation stops without an automatic retry"); }
            }
        }
        var unavailable = new SequenceHandler((attempt, token) => Task.FromResult(attempt == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : success()));
        using (var client = new HttpClient(unavailable)) {
            new GeminiTuner(client, null, noDelay).ProposeAsync("test-key", "gemini-test", "test", baseline, CancellationToken.None).GetAwaiter().GetResult();
            Check(unavailable.Calls == 2, "temporary server unavailability retries once");
        }
        foreach (int status in new[] { 400, 401, 403, 404 })
        {
            var permanent = new SequenceHandler((attempt, token) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("Do not echo test-key or provider error bodies") }));
            using (var client = new HttpClient(permanent))
            {
                try { new GeminiTuner(client, null, noDelay).ProposeAsync("test-key", "gemini-test", "test", baseline, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("HTTP error was ignored"); }
                catch (InvalidOperationException ex) { Check(permanent.Calls == 1 && ex.Message.Contains("HTTP " + status) && !ex.Message.Contains("test-key"), "HTTP " + status + " is explained without retrying or exposing response details"); }
            }
        }
        var cooldown = new SequenceHandler((attempt, token) => {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        using (var client = new HttpClient(cooldown)) {
            try { new GeminiTuner(client, null, noDelay).ProposeAsync("test-key", "gemini-test", "test", baseline, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Cooldown ignored"); }
            catch (InvalidOperationException ex) { Check(cooldown.Calls == 1 && ex.Message.Contains("cooldown"), "long provider cooldown is respected without an early retry"); }
        }
        var disconnected = new SequenceHandler((attempt, token) => { throw new HttpRequestException("secret transport detail"); });
        using (var client = new HttpClient(disconnected)) {
            try { new GeminiTuner(client, null, noDelay).ProposeAsync("test-key", "gemini-test", "test", baseline, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Network failure ignored"); }
            catch (InvalidOperationException ex) { Check(disconnected.Calls == 2 && ex.Message.Contains("internet") && !ex.Message.Contains("secret"), "network failure stops after two attempts with connection guidance"); }
        }
        Check(JToken.DeepEquals(TuningProposal.ApplyChanges(baseline, edits), next), "small edit records reconstruct the full validated configuration");
        var empty = new JObject { ["changes"] = new JArray(), ["modes"] = new JArray(), ["caps"] = new JArray() };
        Check(JToken.DeepEquals(TuningProposal.ApplyChanges(baseline, empty), baseline), "empty edit lists preserve every original field");
        var single = (JObject)empty.DeepClone(); single["changes"] = new JArray(new JObject { ["field"] = "x.acceleration", ["value"] = 0.08 });
        var one = TuningProposal.ApplyChanges(baseline, single);
        var expectedProfile = Profile(baseline); expectedProfile["Whole or horizontal accel parameters"]["acceleration"] = 0.08;
        Check(JToken.DeepEquals(one["profiles"][0], expectedProfile), "an AI edit never needs to reproduce unrelated profile fields");
        Check(JToken.DeepEquals(one["profiles"][0]["Input speed calculation parameters"], baseline["profiles"][0]["Input speed calculation parameters"]), "input-speed fields survive edit responses");
        Check(JToken.DeepEquals(one["devices"], baseline["devices"]) && JToken.DeepEquals(one["defaultDeviceConfig"], baseline["defaultDeviceConfig"]), "edit records cannot replace device configuration or assignments");
        var physical = (JObject)baseline.DeepClone(); physical["defaultDeviceConfig"]["DPI (normalizes input speed unit: counts/ms -> in/s)"] = 1600;
        var multiplier = (JObject)empty.DeepClone(); multiplier["changes"] = new JArray(new JObject { ["field"] = "baseMultiplier", ["value"] = 1.1 });
        Check(Math.Abs((double)TuningProposal.ApplyChanges(physical, multiplier)["profiles"][0]["Output DPI"] - 1760) < 0.001, "AI base multiplier respects physical DPI normalization");
        Check(Math.Abs((double)TuningProposal.TuningValues(physical)["baseMultiplier"] - 0.625) < 0.001, "AI context exposes the effective multiplier rather than raw output DPI");
        var duplicate = (JObject)single.DeepClone(); ((JArray)duplicate["changes"]).Add(single["changes"][0].DeepClone());
        Reject(() => TuningProposal.ApplyChanges(baseline, duplicate), "duplicate numeric edits rejected");
        var unknown = (JObject)empty.DeepClone(); unknown["changes"] = new JArray(new JObject { ["field"] = "name", ["value"] = 1 });
        Reject(() => TuningProposal.ApplyChanges(baseline, unknown), "edits cannot target protected fields");
        var numericString = (JObject)single.DeepClone(); numericString["changes"][0]["value"] = "0.08";
        Reject(() => TuningProposal.ApplyChanges(baseline, numericString), "edit values cannot be numeric strings");
        var notFinite = (JObject)single.DeepClone(); notFinite["changes"][0]["value"] = double.PositiveInfinity;
        Reject(() => TuningProposal.ApplyChanges(baseline, notFinite), "non-finite edit values rejected");
        var wrongAxis = (JObject)empty.DeepClone(); wrongAxis["modes"] = new JArray(new JObject { ["axis"] = "../../devices", ["mode"] = "classic" });
        Reject(() => TuningProposal.ApplyChanges(baseline, wrongAxis), "mode changes cannot use arbitrary paths");
        var wrongMode = (JObject)empty.DeepClone(); wrongMode["modes"] = new JArray(new JObject { ["axis"] = "x", ["mode"] = "invented" });
        Reject(() => TuningProposal.ApplyChanges(baseline, wrongMode), "unsupported edit modes rejected");
        var wrongCap = (JObject)empty.DeepClone(); wrongCap["caps"] = new JArray(new JObject { ["axis"] = "x", ["mode"] = "invented" });
        Reject(() => TuningProposal.ApplyChanges(baseline, wrongCap), "unsupported cap modes rejected");
        var missingGroup = (JObject)empty.DeepClone(); missingGroup.Remove("modes");
        Check(JToken.DeepEquals(TuningProposal.ApplyChanges(baseline, missingGroup), baseline), "omitted edit groups mean no changes to those settings");
        Reject(() => TuningProposal.ApplyChanges(baseline, profile), "full profile responses are rejected by the edit contract");
        var huge = (JObject)empty.DeepClone(); huge["changes"] = new JArray(Enumerable.Range(0, 65).Select(i => single["changes"][0].DeepClone()));
        Reject(() => TuningProposal.ApplyChanges(baseline, huge), "oversized edit lists rejected");
        var zero = (JObject)multiplier.DeepClone(); zero["changes"][0]["value"] = 0;
        Reject(() => TuningProposal.ApplyChanges(baseline, zero), "zero base multiplier rejected");
        Check(JToken.DeepEquals(snapshot, baseline), "AI edit processing leaves the original baseline unchanged");
        Check(GeminiTuner.ProviderError("{\"error\":{\"message\":\"API key not valid: test-key\"}}", "test-key").Contains("[redacted]"), "provider error details redact the actual API key");
        Check(GeminiTuner.ProviderError("{\"error\":{\"message\":\"Unknown field responseSchema\"}}", "test-key").Contains("responseSchema"), "provider request rejection details remain actionable");
        Check(GeminiTuner.ProviderError("<html>untrusted server page</html>", "test-key") == "", "non-JSON provider error bodies are not displayed");
        Check(GeminiTuner.ProviderError("{\"error\":{\"message\":\"authorization=secret api_key=hidden\"}}", "test-key").IndexOf("secret", StringComparison.Ordinal) < 0, "credential-shaped details are redacted");
        var detailed=new JObject { ["changes"]=new JArray(new JObject{["field"]="yRatio",["value"]=1.1}),["switches"]=new JArray(new JObject{["field"]="x.gain",["value"]=false},new JObject{["field"]="combinedAxes",["value"]=false}),["gameAdvice"]=new JObject{["suggestedSensitivity"]=0.13,["reason"]="Try only after a repeatable comparison.",["testPlan"]="Compare matching 30 second tests."} };
        var detailedResult=TuningProposal.ApplyChanges(baseline,detailed);
        Check((double)detailedResult["profiles"][0]["Y/X output DPI ratio (vertical sens multiplier)"]==1.1,"AI can explicitly tune directional ratios");
        Check(!(bool)detailedResult["profiles"][0]["Whole or horizontal accel parameters"]["Gain / Velocity"],"AI can explicitly change gain versus velocity");
        Check(!(bool)detailedResult["profiles"][0]["Input speed calculation parameters"]["Whole/combined accel (set false for 'by component' mode)"],"AI can explicitly activate separate X and Y curves");
        Check(detailedResult["gameAdvice"]==null && TuningProposal.GameAdvice(detailed).Contains("0.13"),"game sensitivity advice is separate from driver settings");
        Check(JToken.DeepEquals(baseline,snapshot),"expanded AI controls preserve original complete settings");
        var invalid=(JObject)detailed.DeepClone();invalid["switches"][0]["value"]="false";
        Reject(()=>TuningProposal.ApplyChanges(baseline,invalid),"AI switch strings are rejected instead of coerced");
        invalid=(JObject)detailed.DeepClone();((JArray)invalid["switches"]).Add(invalid["switches"][0].DeepClone());
        Reject(()=>TuningProposal.ApplyChanges(baseline,invalid),"duplicate AI switches are rejected");
        invalid=(JObject)detailed.DeepClone();invalid["gameAdvice"]["suggestedSensitivity"]=-1;
        Reject(()=>TuningProposal.ApplyChanges(baseline,invalid),"invalid game sensitivity suggestions are rejected");
        invalid=(JObject)detailed.DeepClone();invalid["switches"][0]["field"]="disableDriver";
        Reject(()=>TuningProposal.ApplyChanges(baseline,invalid),"AI cannot use curve switches to modify driver device configuration");
        Console.WriteLine(passed + " checks passed; no live API or driver activation.");
    }
    sealed class SequenceHandler : HttpMessageHandler
    {
        readonly Func<int, CancellationToken, Task<HttpResponseMessage>> send;
        public int Calls;
        public SequenceHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) => send(++Calls, cancel);
    }
    sealed class MockHandler : HttpMessageHandler
    {
        public string FinishReason = "STOP", Url, Body;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool HeaderKey;
        readonly string profile;
        public MockHandler(string profile) { this.profile = profile; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Url = request.RequestUri.ToString();
            Body = await request.Content.ReadAsStringAsync();
            HeaderKey = request.Headers.GetValues("x-goog-api-key").Single() == "test-key";
            var json = new JObject { ["candidates"] = new JArray(new JObject {
                ["finishReason"] = FinishReason,
                ["content"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = profile }) }
            }) };
            return new HttpResponseMessage(Status) { Content = new StringContent(json.ToString()) };
        }
    }
}
