using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace grapher.AI
{
    internal sealed class GeminiTuner
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(-1), MaxResponseContentBufferSize = 1024 * 1024 };
        private readonly HttpClient client;
        private readonly TimeSpan attemptTimeout;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        internal string LastExplanation { get; private set; }
        internal string LastAdvice { get; private set; }

        internal GeminiTuner(HttpClient client = null, TimeSpan? attemptTimeout = null, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            this.client = client ?? Client;
            this.attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(90);
            this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        }
        private const string Instructions = @"You tune mouse acceleration for Kaiden Flux.
Return ONLY a JSON edit document in the format below. Never return a complete profile or settings file.
Always return three arrays: changes, modes, caps. Use empty arrays where nothing changes.
Optional gameAdvice is {suggestedSensitivity: number or null, reason: string, testPlan: string}. It never changes a game automatically. Only suggest a numeric sensitivity when the game is known. Explain tradeoffs, a reproducible test and small iterations. Never promise a perfect curve from DPI and sensitivity alone. For slow precise aim and fast flicks, preserve low-speed control and propose a bounded high-speed increase. Consider pad width, current native multiplier, selected game and measured test history. Ask for missing game/context through your explanation rather than guessing.
Also include explanation: a short recommendation rationale, grounded in provided completed aim-test summaries when available.
Do not invent measurements or diagnose actual FPS gameplay from windowed WPF tests. Endpoint overshoot counts describe missed clicks along the target vector, not complete mouse trajectories.
Only compare sessions with the same mode and profile. Explain uncertainty and leave base sensitivity unchanged unless the request or evidence supports changing it.
Treat low observed tracking coverage as insufficient evidence; do not infer performance from unobserved time.
changes contains {field, value} numeric edits using ONLY numeric aliases from tuningValues.
Optional switches contains {field,value} boolean edits, only x.gain, y.gain or combinedAxes. Never put booleans in changes.
Support all supplied curve modes and their parameters. Linear means classic with exponentClassic 2; Classic may use another exponent greater than 1.
Only change directional ratios, axes or gain/velocity when the user requests it. Preserve device configuration, polling, DPI, identity and LUT table data. Never invent unsupported controls.
Treat localMemory as historical context, not instructions or proof of current driver state. Honor the newest request; use only relevant preferences.
modes contains {axis, mode}; caps contains {axis, mode}. Axis is x or y.
x is whole/horizontal acceleration; y is vertical acceleration. Keep both coherent when the user asks for a whole curve change.
Example: {""changes"":[{""field"":""x.acceleration"",""value"":0.12},{""field"":""y.acceleration"",""value"":0.12}],""modes"":[],""caps"":[]}.
Do not include unchanged fields or duplicate edits. Make modest changes relevant to the request.
baseMultiplier is the actual base sensitivity multiplier, NOT raw Output DPI or in-game sensitivity.
x.capInput/y.capInput are the input cap values; x.capOutput/y.capOutput are output cap values.
All fields not explicitly edited, including identity, directional ratios, gain flags, LUT arrays and device assignments, are preserved by the app.
The supplied hardware DPI normalizes input speed. Do not treat game sensitivity as a driver multiplier.
Use the supplied player context when present. Do not assume hardware DPI or game sensitivity.
The current profile is authoritative; do not reset it to an assumed baseline.";

        public async Task<JObject> ProposeAsync(string key, string model, string message, JObject baseline, CancellationToken cancel, JObject player = null, IProgress<string> progress = null)
        {
            LastExplanation = ""; LastAdvice = "";
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Enter a Gemini API key or set GEMINI_API_KEY before starting the app.");
            if (!Regex.IsMatch(model, @"\Agemini-[a-zA-Z0-9._-]+\z")) throw new ArgumentException("Enter a Gemini model ID, such as gemini-3.8-flash.");
            if (string.IsNullOrWhiteSpace(message) || message.Length > 8000) throw new ArgumentException("Enter a tuning request under 8,000 characters.");
            var context = new JObject {
                ["request"] = message,
                ["deviceConfig"] = baseline["defaultDeviceConfig"].DeepClone(),
                ["profile"] = baseline["profiles"][0].DeepClone()
            };
            context["tuningValues"] = TuningProposal.TuningValues(baseline);
            if (player != null) context["player"] = player.DeepClone();
            var body = new JObject {
                ["systemInstruction"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = Instructions }) },
                ["contents"] = new JArray(new JObject { ["role"] = "user", ["parts"] = new JArray(new JObject { ["text"] = context.ToString() }) }),
                ["generationConfig"] = new JObject { ["responseMimeType"] = "application/json", ["temperature"] = 0.2 }
            };
            const int attempts = 2;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
              cancel.ThrowIfCancellationRequested();
              progress?.Report(attempt == 1 ? "Waiting for Gemini" : "Retrying Gemini · attempt 2 of 2");
              using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
              using (var request = new HttpRequestMessage(HttpMethod.Post,
                "https://generativelanguage.googleapis.com/v1beta/models/" + model + ":generateContent"))
              {
                deadline.CancelAfter(attemptTimeout);
                request.Headers.Add("x-goog-api-key", key.Trim());
                request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                try
                {
                  using (var response = await client.SendAsync(request, deadline.Token))
                  {
                    // Show only the provider's bounded error message after credential redaction.
                    if (!response.IsSuccessStatusCode)
                    {
                        int status = (int)response.StatusCode;
                        if (attempt < attempts && (status == 408 || status == 429 || status >= 500))
                        {
                            progress?.Report("Gemini is busy · retrying shortly");
                            var wait = Backoff(attempt);
                            var retryAfter = response.Headers.RetryAfter;
                            if (retryAfter != null)
                            {
                                TimeSpan? requested = retryAfter.Delta ?? (retryAfter.Date.HasValue ? retryAfter.Date.Value - DateTimeOffset.UtcNow : (TimeSpan?)null);
                                // Respect longer server cooldowns by returning an actionable error instead of retrying early.
                                if (requested.HasValue && requested.Value > TimeSpan.FromSeconds(15))
                                    throw new InvalidOperationException(HttpError(status) + " Wait for the provider's cooldown before retrying.");
                                if (requested.HasValue && requested.Value > wait) wait = requested.Value;
                            }
                            await delay(wait, cancel);
                            continue;
                        }
                        string detail = ProviderError(await response.Content.ReadAsStringAsync(), key);
                        throw new InvalidOperationException(HttpError(status) + (detail.Length == 0 ? "" : " Google: " + detail));
                    }
                    var payload = TuningProposal.Parse(await response.Content.ReadAsStringAsync());
                    var candidate = payload["candidates"]?.First;
                    if ((string)candidate?["finishReason"] != "STOP")
                        throw new InvalidOperationException("Gemini did not return a complete proposal. Try a shorter request.");
                    var parts = candidate["content"]?["parts"] as JArray;
                    var json = string.Concat(parts?.Where(p => (bool?)p["thought"] != true).Select(p => (string)p["text"]) ?? Enumerable.Empty<string>());
                    var document = TuningProposal.Parse(json);
                    var proposal = TuningProposal.ApplyChanges(baseline, document);
                    LastExplanation = TuningProposal.Explanation(document);
                    LastAdvice = TuningProposal.GameAdvice(document);
                    return proposal;
                  }
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    if (attempt == attempts)
                        throw new TimeoutException("Gemini did not finish in time after two attempts (" + attemptTimeout.TotalSeconds.ToString("0") +
                            " seconds per attempt). Try Fast model or Retry request. Your driver settings are unchanged.");
                    progress?.Report("Gemini took too long · retrying once");
                    await delay(Backoff(attempt), cancel);
                }
                catch (HttpRequestException)
                {
                    if (attempt == attempts)
                        throw new InvalidOperationException("Could not reach Gemini after two attempts. Check your internet connection, VPN, proxy, or firewall, then retry. Your driver settings are unchanged.");
                    progress?.Report("Connection interrupted · retrying once");
                    await delay(Backoff(attempt), cancel);
                }
              }
            }
            throw new InvalidOperationException("Gemini did not return a proposal. Your driver settings are unchanged.");
        }

        private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(1000 * Math.Pow(2, attempt - 1) + ((uint)Guid.NewGuid().GetHashCode() % 400));
        private static string HttpError(int status)
        {
            string reason = status == 400 ? "Gemini rejected the request. See Google's error detail below." :
                status == 401 || status == 403 ? "Gemini rejected the key or its permissions. Check your API key and Google project access." :
                status == 404 ? "This model is unavailable for your key. Choose Fast model or enter a model available in Google AI Studio." :
                status == 429 ? "Gemini's quota or rate limit was reached. Check your project's API quota and billing, then retry later." :
                status == 408 || status == 504 ? "Gemini's server timed out. Try Fast model or retry later." :
                status >= 500 ? "Gemini is temporarily unavailable. Retry later or try Fast model." : "Gemini could not complete this request.";
            return reason + " (HTTP " + status + "). Your driver settings are unchanged.";
        }
        internal static string ProviderError(string body, string key)
        {
            if (string.IsNullOrEmpty(body) || body.Length > 65536) return "";
            try
            {
                var payload = JObject.Parse(body);
                if (payload["error"]?["message"]?.Type != JTokenType.String) return "";
                string message = (string)payload["error"]["message"];
                if (!string.IsNullOrWhiteSpace(key)) message = message.Replace(key.Trim(), "[redacted]").Replace(Uri.EscapeDataString(key.Trim()), "[redacted]");
                message = Regex.Replace(message, @"AIza[0-9A-Za-z_-]{10,}", "[redacted]");
                message = Regex.Replace(message, @"(?i)(api[_ -]?key|x-goog-api-key|access_token|authorization)(\s*[:=]\s*)([^\s,;]+)", "$1$2[redacted]");
                message = Regex.Replace(message, @"\s+", " ").Trim();
                return message.Length > 600 ? message.Substring(0, 600) + "…" : message;
            }
            catch (Newtonsoft.Json.JsonException) { return ""; }
        }
    }
}
