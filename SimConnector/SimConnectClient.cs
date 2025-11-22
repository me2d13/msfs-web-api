using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SimConnector
{
    // Handles only SimConnect connection and messaging.
    public class SimConnectClient
    {
        private readonly ILogger<SimConnectClient> _logger;
        private SimConnect? _simConnect;
        private const int WM_USER_SIMCONNECT = 0x0402;
        private SimConnectWindow simConnectWindow;
        private System.Threading.Timer _messageTimer;
        private const int DISPATCH_INTERVAL_MS = 100;

        // Queue for serializing all SimConnect calls (API is not thread-safe)
        private readonly Channel<Action<SimConnect>> _commandQueue = Channel.CreateUnbounded<Action<SimConnect>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        private CancellationTokenSource _queueCts = new();
        private Task? _queueProcessorTask;
        private CancellationTokenSource _connectionCts = new();
        private Task? _connectionMonitorTask;

        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<SimConnect, SIMCONNECT_RECV_EXCEPTION>? OnRecvException;
        public event Action<SimConnect, SIMCONNECT_RECV_SIMOBJECT_DATA>? OnRecvSimVar;
        public event Action<SimConnect, SIMCONNECT_RECV>? OnRecvQuit;
        public event Action<SimConnect, SIMCONNECT_RECV_OPEN>? OnRecvOpen;

        public bool IsConnected { get; private set; } = false;
        public SimConnect? Instance => _simConnect;

        // --- Enums for services ---
        public enum DEFINITION { Dummy = 0 };
        public enum REQUEST { Dummy = 0, Struct1 };
        public enum EVENT { Dummy = 0 };
        public enum GROUP_ID { GROUP0 = 0 };

        public SimConnectClient(ILogger<SimConnectClient> logger)
        {
            _logger = logger;
            simConnectWindow = new SimConnectWindow();
            simConnectWindow.Create();
            StartQueueProcessor();

            StartConnectionMonitor();
        }

        private void StartConnectionMonitor()
        {
            _connectionMonitorTask = Task.Run(async () =>
            {
                while (!_connectionCts.IsCancellationRequested)
                {
                    if (!IsConnected)
                    {
                        TryConnect();
                    }
                    try
                    {
                        await Task.Delay(5000, _connectionCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, _connectionCts.Token);
        }

        private void StartQueueProcessor()
        {
            // Start single reader processor for queued commands
            _queueProcessorTask = Task.Run(async () =>
            {
                try
                {
                    while (await _commandQueue.Reader.WaitToReadAsync(_queueCts.Token).ConfigureAwait(false))
                    {
                        while (_commandQueue.Reader.TryRead(out var action))
                        {
                            try
                            {
                                // Wait until we have a connected SimConnect instance
                                if (_simConnect == null || !IsConnected)
                                {
                                    // Poll until connected or cancelled
                                    while ((_simConnect == null || !IsConnected) && !_queueCts.IsCancellationRequested)
                                    {
                                        await Task.Delay(50, _queueCts.Token).ConfigureAwait(false);
                                    }
                                }

                                if (_simConnect != null && IsConnected && !_queueCts.IsCancellationRequested)
                                {
                                    action(_simConnect);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                return; // shutting down
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Exception executing SimConnect queued action.");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // normal shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SimConnect command queue processor terminated unexpectedly.");
                }
            }, _queueCts.Token);
        }

        private void Enqueue(Action<SimConnect> action)
        {
            if (_queueCts.IsCancellationRequested)
            {
                _logger.LogWarning("Attempted to enqueue after queue cancellation.");
                return;
            }
            if (!_commandQueue.Writer.TryWrite(action))
            {
                // fallback to async write if backpressure somehow applies (unbounded shouldn't)
                _ = _commandQueue.Writer.WriteAsync(action);
            }
        }

        public void Disconnect()
        {
            _messageTimer?.Dispose();
            _messageTimer = null;
            if (_simConnect != null)
            {
                _simConnect.Dispose();
                _simConnect = null;
            }
            IsConnected = false;
            OnDisconnected?.Invoke();
        }

        public void RefreshConnection()
        {
            if (_simConnect != null)
            {
                Disconnect();
            }
            TryConnect();
        }

        private bool TryConnect()
        {
            _logger.LogDebug("Attempting SimConnect connection.");
            try
            {
                IntPtr hWnd = simConnectWindow.Handle;
                _simConnect = new SimConnect("MSFS Web API", hWnd, WM_USER_SIMCONNECT, null, 0);
                IsConnected = true;

                _simConnect.OnRecvOpen += SimConnect_OnRecvOpen;
                _simConnect.OnRecvQuit += SimConnect_OnRecvQuit;
                _simConnect.OnRecvException += SimConnect_OnRecvException;
                _simConnect.OnRecvSimobjectData += SimConnect_OnRecvSimVar;

                _logger.LogInformation("SimConnect connection established, starting message timer.");

                _messageTimer = new System.Threading.Timer(
                    _ => ReceiveSimConnectMessages(),
                    null,
                    0,
                    DISPATCH_INTERVAL_MS
                );
                // OnConnected will be invoked when OnRecvOpen is received
                return true;
            }
            catch (COMException)
            {
                IsConnected = false;
                // This is expected if MSFS is not running
                _logger.LogDebug("SimConnect unavailable (MSFS not running?). Retrying in 5s.");
                return false;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                _logger.LogError(ex, "SimConnect general exception during connection.");
                return false;
            }
        }

        public void ReceiveSimConnectMessages()
        {
            try
            {
                _simConnect?.ReceiveMessage();
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "COMException from ReceiveMessage - treating as disconnect.");
                Disconnect();
            }
        }

        private void SimConnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            OnRecvException?.Invoke(sender, data);
        }

        private void SimConnect_OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            _logger.LogInformation("SimConnect_OnRecvQuit");
            OnRecvQuit?.Invoke(sender, data);
            Disconnect();
        }

        private void SimConnect_OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            _logger.LogInformation("SimConnect_OnRecvOpen");
            OnConnected?.Invoke();
            OnRecvOpen?.Invoke(sender, data);
        }

        private void SimConnect_OnRecvSimVar(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            OnRecvSimVar?.Invoke(sender, data);
        }

        // --- Methods for SimVarService and SimEventService ---
        public void AddToDataDefinition(DEFINITION defineId, SimVarReference reference)
        {
            if (reference == null) return;
            Enqueue(sim =>
            {
                sim.AddToDataDefinition(
                    defineId,
                    reference.SimVarName,
                    reference.Unit,
                    SIMCONNECT_DATATYPE.FLOAT64,
                    0.0f,
                    SimConnect.SIMCONNECT_UNUSED
                );
                sim.RegisterDataDefineStruct<double>(defineId);
            });
        }

        public void RequestDataOnSimObject(REQUEST requestId, DEFINITION defineId)
        {
            Enqueue(sim =>
            {
                sim.RequestDataOnSimObject(
                    requestId,
                    defineId,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.ONCE,
                    SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                    0, 0, 0
                );
            });
        }

        public void SetDataOnSimObject(DEFINITION defineId, double value)
        {
            Enqueue(sim =>
            {
                sim.SetDataOnSimObject(
                    defineId,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                    value
                );
            });
        }

        public void RegisterSimEvent(int simEventNum, string eventName)
        {
            if (string.IsNullOrWhiteSpace(eventName)) return;
            Enqueue(sim =>
            {
                sim.MapClientEventToSimEvent((EVENT)simEventNum, eventName);
                sim.AddClientEventToNotificationGroup((GROUP_ID)0, (EVENT)simEventNum, false);
            });
        }

        public void TransmitSimEvent(int simEventNum, uint value)
        {
            Enqueue(sim =>
            {
                sim.TransmitClientEvent(
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    (EVENT)simEventNum,
                    value,
                    (GROUP_ID)0,
                    SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY
                );
            });
        }

        // Cleanup resources if needed (optional public dispose pattern could be added)
        public void Shutdown()
        {
            _queueCts.Cancel();
            _connectionCts.Cancel();
            try { _queueProcessorTask?.Wait(1000); } catch { /* ignore */ }
            try { _connectionMonitorTask?.Wait(1000); } catch { /* ignore */ }
            Disconnect();
            simConnectWindow.Destroy();
        }
    }
}
