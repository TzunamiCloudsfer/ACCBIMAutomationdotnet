using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using AutodeskAutomation.Models;
using AutodeskAutomation.Services;
using Newtonsoft.Json;

namespace AutodeskAutomation.Controllers
{
    [RoutePrefix("events")]
    public class SseController : ApiController
    {
        private readonly SseService _sse = SseService.Instance;
        private readonly ServerState _srv = ServerState.Instance;

        [HttpGet, Route("")]
        public HttpResponseMessage Connect(CancellationToken ct)
        {
            var (clientId, channel) = _sse.AddClient();

            var response = Request.CreateResponse(HttpStatusCode.OK);
            response.Content = new PushStreamContent(
                async (stream, content, transport) =>
                {
                    using var writer = new StreamWriter(stream) { AutoFlush = true };
                    try
                    {
                        // Send full init event so reconnecting clients get current state
                        var initData = BuildInitData();
                        var initMsg = $"data: {JsonConvert.SerializeObject(new { type = "init", data = initData })}\n\n";
                        await writer.WriteAsync(initMsg);

                        // Stream events until client disconnects
                        while (!ct.IsCancellationRequested)
                        {
                            if (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                            {
                                while (channel.Reader.TryRead(out var msg))
                                {
                                    await writer.WriteAsync(msg);
                                    await writer.FlushAsync();
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception) { }
                    finally
                    {
                        _sse.RemoveClient(clientId);
                    }
                },
                "text/event-stream");

            response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            response.Headers.Add("X-Accel-Buffering", "no");
            return response;
        }

        private object BuildInitData()
        {
            return new
            {
                isRunning = _srv.IsRunning,
                accRunning = _srv.AccRunning,
                bim360Running = _srv.Bim360Running,
                isPaused = _srv.IsPaused,
                accPaused = _srv.AccPaused,
                bim360Paused = _srv.Bim360Paused,
                loginPending = _srv.LoginPending,
                loginDetected = _srv.LoginDetected,
                loginElapsed = _srv.LoginElapsedSeconds,
                runningPlatform = _srv.RunningPlatform,
                chainRunning = _srv.ChainRunning,
                activeUser = _srv.ActiveUser,
                acc = _srv.Acc,
                bim360 = _srv.Bim360
            };
        }
    }
}
