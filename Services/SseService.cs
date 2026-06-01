using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using Newtonsoft.Json;

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

        public void Broadcast(string type, object data)
        {
            var payload = $"data: {JsonConvert.SerializeObject(new { type, data })}\n\n";
            foreach (var kv in _clients)
            {
                try { kv.Value.Writer.TryWrite(payload); }
                catch { RemoveClient(kv.Key); }
            }
        }

        public int ClientCount => _clients.Count;
    }
}
