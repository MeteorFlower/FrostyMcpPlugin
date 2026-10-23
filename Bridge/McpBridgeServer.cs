using FrostySdk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Named-pipe JSON-RPC bridge for Cursor / MCP clients.
    /// Wire protocol matches cheatengine-mcp-bridge:
    ///   request  = uint32_le(length) + utf8(json-rpc request)
    ///   response = uint32_le(length) + utf8(json-rpc response)
    /// </summary>
    public sealed class McpBridgeServer
    {
        public static string PipeName
        {
            get
            {
                string profile = ProfilesLibrary.ProfileName;
                if (!string.IsNullOrEmpty(profile))
                    return PipeProfiles.PipeNameFor(profile);

                return PipeProfiles.BasePipeName;
            }
        }

        public const string Version = "1.13.2";
        public const int MaxRequestBytes = 32 * 1024 * 1024;

        public static McpBridgeServer Instance { get; } = new McpBridgeServer();

        private readonly object _stateLock = new object();
        private CancellationTokenSource _cts;
        private string _pipeName;
        private Task _listenTask;
        private long _clientsServed;

        public bool IsRunning
        {
            get
            {
                lock (_stateLock)
                    return _cts != null && !_cts.IsCancellationRequested;
            }
        }

        public long ClientsServed => Interlocked.Read(ref _clientsServed);

        public void Start()
        {
            lock (_stateLock)
            {
                if (_cts != null)
                    return;

                _pipeName = PipeName;
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;
                _listenTask = Task.Factory.StartNew(
                    () => ListenLoop(token),
                    token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                if (_cts == null)
                    return;

                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        private void ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        262144,
                        262144);

                    // Wait for a client with cancellation (net48-compatible)
                    Task waitTask = Task.Factory.FromAsync(
                        pipe.BeginWaitForConnection,
                        pipe.EndWaitForConnection,
                        null);
                    using (token.Register(() => { try { pipe.Dispose(); } catch { } }))
                    {
                        waitTask.GetAwaiter().GetResult();
                    }
                    if (token.IsCancellationRequested || !pipe.IsConnected)
                        continue;

                    Interlocked.Increment(ref _clientsServed);
                    HandleClient(pipe, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Client disconnect / pipe recreate — keep listening
                }
                finally
                {
                    try { pipe?.Dispose(); } catch { }
                }
            }
        }

        private void HandleClient(NamedPipeServerStream pipe, CancellationToken token)
        {
            while (!token.IsCancellationRequested && pipe.IsConnected)
            {
                byte[] header = ReadExact(pipe, 4, token);
                if (header == null)
                    break;

                int length = BitConverter.ToInt32(header, 0);
                if (length <= 0 || length > MaxRequestBytes)
                    break;

                byte[] body = ReadExact(pipe, length, token);
                if (body == null)
                    break;

                string requestJson = Encoding.UTF8.GetString(body);
                string responseJson = ProcessRequest(requestJson);
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);

                byte[] respHeader = BitConverter.GetBytes(responseBytes.Length);
                pipe.Write(respHeader, 0, 4);
                pipe.Write(responseBytes, 0, responseBytes.Length);
                pipe.Flush();
            }
        }

        private static byte[] ReadExact(Stream stream, int size, CancellationToken token)
        {
            byte[] buffer = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                if (token.IsCancellationRequested)
                    return null;

                int read;
                try
                {
                    read = stream.Read(buffer, offset, size - offset);
                }
                catch
                {
                    return null;
                }

                if (read <= 0)
                    return null;

                offset += read;
            }
            return buffer;
        }

        private static string ProcessRequest(string requestJson)
        {
            JObject request;
            try
            {
                request = JObject.Parse(requestJson);
            }
            catch
            {
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    error = new { code = -32700, message = "Parse error" },
                    id = (object)null
                });
            }

            object id = request["id"]?.Type == JTokenType.Null ? null : request["id"]?.ToObject<object>();
            string method = request["method"]?.ToString();
            JObject paramsObj = request["params"] as JObject ?? new JObject();

            if (string.IsNullOrEmpty(method))
            {
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    error = new { code = -32600, message = "Invalid Request: missing method" },
                    id
                });
            }

            try
            {
                object result = McpCommandRouter.Dispatch(method, paramsObj);
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    result,
                    id
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    error = new { code = -32603, message = "Internal error: " + ex.Message },
                    id
                });
            }
        }
    }
}
