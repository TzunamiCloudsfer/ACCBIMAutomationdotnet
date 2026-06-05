using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AutodeskAutomation.Services
{
    public class SseService
    {
        private static readonly SseService _instance = new SseService();
        public static SseService Instance => _instance;

        private SseService() { }

        private readonly ConcurrentDictionary<string, Channel<string>> _clients
            = new ConcurrentDictionary<string, Channel<string>>();

        public (string clientId, Channel<string> channel) AddClient()
        {
            var clientId = Guid.NewGuid().ToString("N");
            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _clients[clientId] = channel;
            return (clientId, channel);
        }

        public void RemoveClient(string clientId)
        {
            if (_clients.TryRemove(clientId, out var ch))
                ch.Writer.TryComplete();
        }

        // Use camelCase so app.js (which reads data.results.success, data.platform etc.) works correctly
        private static readonly JsonSerializerSettings _camelCase = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        public void Broadcast(string type, object data)
        {
            var payload = $"data: {JsonConvert.SerializeObject(new { type, data }, _camelCase)}\n\n";
            foreach (var kv in _clients)
            {
                try { kv.Value.Writer.TryWrite(payload); }
                catch { RemoveClient(kv.Key); }
            }
        }

        public int ClientCount => _clients.Count;
    }
}
