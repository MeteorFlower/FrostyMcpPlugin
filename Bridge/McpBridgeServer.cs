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

        public const string Version = "2.3.22";
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

                    long connectionId = Interlocked.Increment(ref _clientsServed);
                    HandleClient(pipe, token, connectionId);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Client disconnect or pipe recreate; keep listening
                }
                finally
                {
                    try { pipe?.Dispose(); } catch { }
                }
            }
        }

        private void HandleClient(NamedPipeServerStream pipe, CancellationToken token, long connectionId)
        {
            // Pending two-phase plans belong to the connection that made them, so a
            // second client cannot invalidate them and a disconnect cannot wipe them.
            McpCommit.EnterConnection(connectionId);
            try
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
            finally
            {
                McpCommit.ExitConnection();
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
                McpCommit.BeginCall(method);

                // These read-only scans were the two tools that could pin the one
                // bridge thread for 120+ seconds when a lazy object getter blocked.
                // A cooperative budget inside the handler is the first line; this
                // outer wait is the guarantee that the client gets a response even
                // if one GetValue call never returns.
                string[] timeoutable =
                {
                    "list_connections", "search_ebx_fields", "find_pointer_references",
                    "research_component_usage"
                };
                if (Array.IndexOf(timeoutable, method) >= 0)
                {
                    int budget = paramsObj.Value<int?>("max_scan_ms") ?? 20000;
                    if (budget < 1000) budget = 1000;
                    if (budget > 60000) budget = 60000;
                    int waitMs = budget + 10000;

                    object result = null;
                    Exception failure = null;
                    Task task = Task.Run(() =>
                    {
                        try { result = McpCommandRouter.Dispatch(method, paramsObj); }
                        catch (Exception ex) { failure = ex; }
                    });

                    if (!task.Wait(waitMs))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            jsonrpc = "2.0",
                            error = new
                            {
                                code = -32000,
                                message = "COMMAND_TIMEOUT: " + method + " did not finish within " + waitMs +
                                          " ms (max_scan_ms=" + budget + "). The editor worker may still be running; " +
                                          "close or restart that editor before issuing more calls."
                            },
                            id
                        });
                    }
                    if (failure != null)
                        throw failure;
                    return JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        result,
                        id
                    });
                }

                object syncResult = McpCommandRouter.Dispatch(method, paramsObj);
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    result = syncResult,
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
