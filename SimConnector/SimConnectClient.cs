using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;
using System;
using System.Runtime.InteropServices;
using System.Threading;

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
            TryConnect();
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
            simConnectWindow.Destroy();
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

        private void TryConnect()
        {
            _logger.LogInformation("Attempting SimConnect connection.");
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
                OnConnected?.Invoke();
            }
            catch (COMException ex)
            {
                IsConnected = false;
                _logger.LogWarning(ex, "SimConnect COMException during connection.");
                OnDisconnected?.Invoke();
            }
            catch (Exception ex)
            {
                IsConnected = false;
                _logger.LogError(ex, "SimConnect general exception during connection.");
                OnDisconnected?.Invoke();
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
            OnRecvOpen?.Invoke(sender, data);
        }

        private void SimConnect_OnRecvSimVar(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            OnRecvSimVar?.Invoke(sender, data);
        }

        // --- Methods for SimVarService and SimEventService ---
        public void AddToDataDefinition(DEFINITION defineId, SimVarReference reference)
        {
            _simConnect?.AddToDataDefinition(
                defineId,
                reference.SimVarName,
                reference.Unit,
                SIMCONNECT_DATATYPE.FLOAT64,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED
            );
            _simConnect?.RegisterDataDefineStruct<double>(defineId);
        }

        public void RequestDataOnSimObject(REQUEST requestId, DEFINITION defineId)
        {
            _simConnect?.RequestDataOnSimObject(
                requestId,
                defineId,
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.ONCE,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                0, 0, 0
            );
        }

        public void SetDataOnSimObject(DEFINITION defineId, double value)
        {
            _simConnect?.SetDataOnSimObject(
                defineId,
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                value
            );
        }

        public void RegisterSimEvent(int simEventNum, string eventName)
        {
            _simConnect?.MapClientEventToSimEvent((EVENT)simEventNum, eventName);
            _simConnect?.AddClientEventToNotificationGroup((GROUP_ID)0, (EVENT)simEventNum, false);
        }

        public void TransmitSimEvent(int simEventNum, uint value)
        {
            _simConnect?.TransmitClientEvent(
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                (EVENT)simEventNum,
                value,
                (GROUP_ID)0,
                SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY
            );
        }
    }
}
