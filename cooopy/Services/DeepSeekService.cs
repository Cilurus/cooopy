using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Cooopy.Services
{
    /// <summary>Minimal OpenAI-compatible client for the DeepSeek chat API.</summary>
    public class DeepSeekService : IDisposable
    {
        public sealed class ChatMessage
        {
            public ChatMessage(string role, string content)
            {
                Role = role;
                Content = content;
            }

            public string Role { get; }
            public string Content { get; }
        }

        private readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        public static bool IsConfigured(string apiUrl, string apiKey) =>
            !string.IsNullOrWhiteSpace(apiUrl) && !string.IsNullOrWhiteSpace(apiKey);

        public async Task<string> CompleteAsync(
            string apiUrl,
            string apiKey,
            string model,
            IEnumerable<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var endpoint = (apiUrl ?? "").Trim().TrimEnd('/');
            if (endpoint.Length == 0) throw new InvalidOperationException("API 地址为空");
            if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                endpoint += "/chat/completions";

            var payload = new
            {
                model = string.IsNullOrWhiteSpace(model) ? "deepseek-chat" : model.Trim(),
                messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                temperature = 0.2,
                stream = false
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (apiKey ?? "").Trim());
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var snippet = body.Length > 400 ? body[..400] + "…" : body;
                throw new InvalidOperationException($"API 返回 {(int)response.StatusCode}：{snippet}");
            }

            using var document = JsonDocument.Parse(body);
            var choices = document.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) throw new InvalidOperationException("API 未返回内容");
            var content = choices[0].GetProperty("message").GetProperty("content").GetString();
            return content ?? "";
        }

        /// <summary>Verifies that the configured API base URL/key can reach the service.</summary>
        public async Task CheckConnectionAsync(string apiUrl, string apiKey, CancellationToken cancellationToken = default)
        {
            var baseUrl = (apiUrl ?? "").Trim().TrimEnd('/');
            if (baseUrl.Length == 0) throw new InvalidOperationException("API 地址为空");
            if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                baseUrl = baseUrl[..^"/chat/completions".Length].TrimEnd('/');

            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (apiKey ?? "").Trim());
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = body.Length > 160 ? body[..160] + "…" : body;
                throw new InvalidOperationException($"API 返回 {(int)response.StatusCode}：{snippet}");
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
