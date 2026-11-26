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
        private int _connectionAttempts;

        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<SimConnect, SIMCONNECT_RECV_EXCEPTION>? OnRecvException;
        public event Action<SimConnect, SIMCONNECT_RECV_SIMOBJECT_DATA>? OnRecvSimVar;
        public event Action<SimConnect, SIMCONNECT_RECV_CLIENT_DATA>? OnRecvClientData;
        public event Action<SimConnect, SIMCONNECT_RECV>? OnRecvQuit;
        public event Action<SimConnect, SIMCONNECT_RECV_OPEN>? OnRecvOpen;

        public bool IsConnected { get; private set; } = false;
        public SimConnect? Instance => _simConnect;

        // --- Enums for services ---
        public enum DEFINITION { Dummy = 0 };
        public enum REQUEST { Dummy = 0, Struct1 };
        public enum EVENT { Dummy = 0 };
        public enum GROUP_ID { GROUP0 = 0 };
        public enum CLIENT_DATA_ID { Dummy = 0 };
        public enum CLIENT_DATA_DEFINITION { Dummy = 0 };

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

        public void Disconnect(string reason = "explicit disconnect")
        {
            var wasConnected = IsConnected;
            var hadInstance = _simConnect != null;
            _logger.LogInformation(
                "Disconnecting SimConnect: {Reason}; wasConnected={WasConnected}, hasInstance={HasInstance}",
                reason,
                wasConnected,
                hadInstance);

            _messageTimer?.Dispose();
            _messageTimer = null;
            if (_simConnect != null)
            {
                _simConnect.Dispose();
                _simConnect = null;
            }
            IsConnected = false;
            OnDisconnected?.Invoke();
            _logger.LogInformation("SimConnect disconnected: {Reason}", reason);
        }

        public void RefreshConnection()
        {
            if (_simConnect != null)
            {
                Disconnect("RefreshConnection requested");
            }
            TryConnect();
        }

        private bool TryConnect()
        {
            var attempt = Interlocked.Increment(ref _connectionAttempts);
            _logger.LogInformation("Attempting SimConnect connection (attempt {Attempt})", attempt);
            try
            {
                IntPtr hWnd = simConnectWindow.Handle;
                _simConnect = new SimConnect("MSFS Web API", hWnd, WM_USER_SIMCONNECT, null, 0);
                IsConnected = true;

                _simConnect.OnRecvOpen += SimConnect_OnRecvOpen;
                _simConnect.OnRecvQuit += SimConnect_OnRecvQuit;
                _simConnect.OnRecvException += SimConnect_OnRecvException;
                _simConnect.OnRecvSimobjectData += SimConnect_OnRecvSimVar;
                _simConnect.OnRecvClientData += SimConnect_OnRecvClientData;

                _logger.LogInformation(
                    "SimConnect handle created on attempt {Attempt}; waiting for OPEN notification",
                    attempt);
                _messageTimer = new System.Threading.Timer(
                    _ => ReceiveSimConnectMessages(),
                    null,
                    0,
                    DISPATCH_INTERVAL_MS);
                return true;
            }
            catch (COMException ex)
            {
                IsConnected = false;
                _logger.LogDebug(
                    ex,
                    "SimConnect unavailable on attempt {Attempt} (HRESULT=0x{HResult:X8}); retrying in 5s",
                    attempt,
                    ex.HResult);
                return false;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                _logger.LogError(ex, "SimConnect connection failed on attempt {Attempt}", attempt);
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
                _logger.LogWarning(
                    ex,
                    "ReceiveMessage failed (HRESULT=0x{HResult:X8}); requesting disconnect",
                    ex.HResult);
                Disconnect($"ReceiveMessage COMException 0x{ex.HResult:X8}");
            }
        }

        private void SimConnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            _logger.LogWarning(
                "SimConnect exception {ExceptionId}; sendId={SendId}, index={Index}, currentInstance={CurrentInstance}",
                data.dwException,
                data.dwSendID,
                data.dwIndex,
                ReferenceEquals(sender, _simConnect));
            OnRecvException?.Invoke(sender, data);
        }

        private void SimConnect_OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            _logger.LogInformation(
                "SimConnect_OnRecvQuit received; currentInstance={CurrentInstance}",
                ReferenceEquals(sender, _simConnect));
            OnRecvQuit?.Invoke(sender, data);
            Disconnect("SimConnect_OnRecvQuit");
        }

        private void SimConnect_OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            _logger.LogInformation(
                "SimConnect_OnRecvOpen received; currentInstance={CurrentInstance}",
                ReferenceEquals(sender, _simConnect));
            OnConnected?.Invoke();
            OnRecvOpen?.Invoke(sender, data);
        }

        private void SimConnect_OnRecvSimVar(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            OnRecvSimVar?.Invoke(sender, data);
        }

        private void SimConnect_OnRecvClientData(SimConnect sender, SIMCONNECT_RECV_CLIENT_DATA data)
        {
            OnRecvClientData?.Invoke(sender, data);
        }

        // --- Methods for ClientDataService ---
        public void MapClientDataArea(string areaName, int areaId)
        {
            if (string.IsNullOrWhiteSpace(areaName)) return;
            Enqueue(sim =>
            {
                sim.MapClientDataNameToID(areaName, (CLIENT_DATA_ID)areaId);
            });
        }

        public void AddClientDataDefinition(int defineId, int offset, uint sizeOrType)
        {
            Enqueue(sim =>
            {
                sim.AddToClientDataDefinition(
                    (CLIENT_DATA_DEFINITION)defineId,
                    (uint)offset,
                    sizeOrType,
                    0.0f,
                    0
                );
            });
        }

        public void RequestClientDataOnce(int areaId, int requestId, int defineId)
        {
            Enqueue(sim =>
            {
                sim.RequestClientData(
                    (CLIENT_DATA_ID)areaId,
                    (REQUEST)requestId,
                    (CLIENT_DATA_DEFINITION)defineId,
                    SIMCONNECT_CLIENT_DATA_PERIOD.ONCE,
                    SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT,
                    0, 0, 0
                );
            });
        }

        /// <summary>
        /// Subscribes to a client data field: SimConnect pushes it whenever the add-on that owns
        /// the area writes new data, instead of the caller polling for it.
        /// </summary>
        public void RequestClientDataSubscription(int areaId, int requestId, int defineId)
        {
            Enqueue(sim =>
            {
                sim.RequestClientData(
                    (CLIENT_DATA_ID)areaId,
                    (REQUEST)requestId,
                    (CLIENT_DATA_DEFINITION)defineId,
                    SIMCONNECT_CLIENT_DATA_PERIOD.ON_SET,
                    SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT,
                    0, 0, 0
                );
            });
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
                try
                {
                    sim.MapClientEventToSimEvent((EVENT)simEventNum, eventName);
                    sim.AddClientEventToNotificationGroup((GROUP_ID)0, (EVENT)simEventNum, false);
                    _logger.LogDebug("SimConnect event registration completed: {EventName} id={EventId}", eventName, simEventNum);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SimConnect event registration failed: {EventName} id={EventId}", eventName, simEventNum);
                    throw;
                }
            });
        }

        public void TransmitSimEvent(int simEventNum, uint value, string eventName)
        {
            Enqueue(sim =>
            {
                _logger.LogInformation(
                    "Calling TransmitClientEvent: {EventName} id={EventId} value={Value}",
                    eventName,
                    simEventNum,
                    value);
                try
                {
                    sim.TransmitClientEvent(
                        SimConnect.SIMCONNECT_OBJECT_ID_USER,
                        (EVENT)simEventNum,
                        value,
                        (GROUP_ID)0,
                        SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                    _logger.LogDebug(
                        "TransmitClientEvent call returned: {EventName} id={EventId} value={Value}",
                        eventName,
                        simEventNum,
                        value);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "TransmitClientEvent call failed: {EventName} id={EventId} value={Value}",
                        eventName,
                        simEventNum,
                        value);
                    throw;
                }
            });
        }

        // Cleanup resources if needed (optional public dispose pattern could be added)
        public void Shutdown()
        {
            _queueCts.Cancel();
            _connectionCts.Cancel();
            try { _queueProcessorTask?.Wait(1000); } catch { /* ignore */ }
            try { _connectionMonitorTask?.Wait(1000); } catch { /* ignore */ }
            Disconnect("Shutdown");
            simConnectWindow.Destroy();
        }
    }
}
