using System;
using System.Net;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AITranslate.Core
{
    internal sealed class ApiException : Exception
    {
        public int StatusCode { get; }

        /// <summary>鉴权失败、余额不足等重试也无用的错误。</summary>
        public bool Fatal { get; }

        public ApiException(string message, int statusCode, bool fatal) : base(message)
        {
            StatusCode = statusCode;
            Fatal = fatal;
        }
    }

    /// <summary>OpenAI 兼容的 Chat Completions 客户端（DeepSeek 默认）。</summary>
    internal sealed class ChatClient
    {
        private static readonly TimeSpan kTimeout = TimeSpan.FromSeconds(180);

        static ChatClient()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 16);
                ServicePointManager.Expect100Continue = false;
            }
            catch (Exception)
            {
                // 部分运行时不允许修改，忽略。
            }
        }

        private readonly string m_Endpoint;
        private readonly string m_ApiKey;
        private readonly string m_Model;
        private readonly float m_Temperature;

        public ChatClient(string apiUrl, string apiKey, string model, float temperature)
        {
            m_Endpoint = NormalizeEndpoint(apiUrl);
            m_ApiKey = (apiKey ?? string.Empty).Trim();
            m_Model = string.IsNullOrWhiteSpace(model) ? Setting.kDefaultModel : model.Trim();
            m_Temperature = temperature;
        }

        public string Endpoint => m_Endpoint;

        public string Model => m_Model;

        /// <summary>允许填写 https://api.deepseek.com、.../v1 或完整的 .../chat/completions。</summary>
        public static string NormalizeEndpoint(string url)
        {
            string u = string.IsNullOrWhiteSpace(url) ? Setting.kDefaultApiUrl : url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                u = "https://" + u;
            }
            u = u.TrimEnd('/');
            if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return u;
            }
            return u + "/chat/completions";
        }

        // 推理模型（deepseek-reasoner 等）不一定支持 JSON 模式与 temperature。
        private bool IsReasoner => m_Model.IndexOf("reason", StringComparison.OrdinalIgnoreCase) >= 0
            || m_Model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || m_Model.StartsWith("o3", StringComparison.OrdinalIgnoreCase);

        public async Task<string> CompleteAsync(string system, string user, bool jsonMode, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(m_ApiKey))
            {
                throw new ApiException("API Key is empty", 0, fatal: true);
            }

            var body = new JObject
            {
                ["model"] = m_Model,
                ["stream"] = false,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = system },
                    new JObject { ["role"] = "user", ["content"] = user },
                },
            };
            if (!IsReasoner)
            {
                body["temperature"] = Math.Round(m_Temperature, 2);
                if (jsonMode)
                {
                    body["response_format"] = new JObject { ["type"] = "json_object" };
                }
            }

            (int code, string text) = await PostAsync(body.ToString(Newtonsoft.Json.Formatting.None), ct).ConfigureAwait(false);
            if (code < 200 || code >= 300)
            {
                throw new ApiException(DescribeError(code, text), code, fatal: code == 401 || code == 402 || code == 403 || code == 404);
            }
            try
            {
                var json = JObject.Parse(text);
                var content = json["choices"]?[0]?["message"]?["content"]?.ToString();
                if (string.IsNullOrEmpty(content))
                {
                    throw new ApiException("Empty response: " + Truncate(text, 300), code, fatal: false);
                }
                return content;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                throw new ApiException("Invalid response: " + Truncate(text, 300), code, fatal: false);
            }
        }

        // 使用 System.dll 的 HttpWebRequest，避免 System.Net.Http 在游戏 Mono 运行时中的版本差异。
        private async Task<(int, string)> PostAsync(string payload, CancellationToken ct)
        {
            var request = (HttpWebRequest)WebRequest.Create(m_Endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + m_ApiKey;
            request.Timeout = (int)kTimeout.TotalMilliseconds;
            request.ReadWriteTimeout = (int)kTimeout.TotalMilliseconds;

            using (var timeout = new CancellationTokenSource(kTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
            using (linked.Token.Register(() => request.Abort()))
            {
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(payload);
                    request.ContentLength = bytes.Length;
                    using (Stream stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    {
                        await stream.WriteAsync(bytes, 0, bytes.Length, linked.Token).ConfigureAwait(false);
                    }
                    using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                    {
                        return ((int)response.StatusCode, await ReadAllAsync(response).ConfigureAwait(false));
                    }
                }
                catch (WebException ex) when (ex.Response is HttpWebResponse error)
                {
                    using (error)
                    {
                        return ((int)error.StatusCode, await ReadAllAsync(error).ConfigureAwait(false));
                    }
                }
                catch (Exception ex) when (ex is WebException || ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested)
                    {
                        throw new ApiException("请求超时 (Request timeout)", 0, fatal: false);
                    }
                    throw new ApiException("网络错误 (Network error): " + ex.Message, 0, fatal: false);
                }
            }
        }

        private static async Task<string> ReadAllAsync(WebResponse response)
        {
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        private static string DescribeError(int code, string body)
        {
            string detail = body;
            try
            {
                detail = JObject.Parse(body)["error"]?["message"]?.ToString() ?? body;
            }
            catch (Exception)
            {
                // 非 JSON 错误体，原样保留。
            }
            detail = Truncate(detail, 300);
            switch (code)
            {
                case 400: return "HTTP 400 请求格式错误 (Bad Request): " + detail;
                case 401: return "HTTP 401 API Key 无效 (Unauthorized): " + detail;
                case 402: return "HTTP 402 账户余额不足 (Insufficient Balance): " + detail;
                case 403: return "HTTP 403 无权访问 (Forbidden): " + detail;
                case 404: return "HTTP 404 地址或模型不存在 (Not Found): " + detail;
                case 422: return "HTTP 422 参数错误: " + detail;
                case 429: return "HTTP 429 请求过于频繁 (Rate Limited): " + detail;
                default: return "HTTP " + code + ": " + detail;
            }
        }

        internal static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
            {
                return s ?? string.Empty;
            }
            return s.Substring(0, max) + "…";
        }
    }
}
