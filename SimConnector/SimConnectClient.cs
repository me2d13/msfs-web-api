using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FlightSimulator.SimConnect;
using Microsoft.Extensions.Logging;

/*
Example usage with curl:

curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA STATE","unit": "","index": 0}'
curl -X 'POST' 'http://localhost:5018/api/simvar/set' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA STATE", "value": 2}'
*/

namespace SimConnector
{
    // Handles all communication with MSFS SimConnect.
    public class SimConnectClient
    {
        private readonly ILogger<SimConnectClient> _logger;
        private SimConnect? _simConnect;
        private const int WM_USER_SIMCONNECT = 0x0402;
        private SimConnectWindow simConnectWindow;
        private readonly SimConnectIdManager idManager = new SimConnectIdManager();
        private ManualResetEvent _windowCreatedEvent = new ManualResetEvent(false);
        // Timer to periodically receive messages from SimConnect
        private System.Threading.Timer _messageTimer;
        private const int DISPATCH_INTERVAL_MS = 100; // 100ms for responsiveness

        public enum DEFINITION
        {
            Dummy = 0
        };

        public enum REQUEST
        {
            Dummy = 0,
            Struct1
        };

        // Indicates current connection status.
        public bool IsConnected { get; private set; } = false;

        // Constructor: sets up message window and attempts initial connection.
        public SimConnectClient(ILogger<SimConnectClient> logger)
        {
            _logger = logger;
            simConnectWindow = new SimConnectWindow();
            simConnectWindow.Create(); // Start the hidden message loop
            TryConnect();
        }

        public void Disconnect()
        {
            // Stop the timer first
            _messageTimer?.Dispose();
            _messageTimer = null;

            if (_simConnect != null)
            {
                _simConnect.Dispose();
                _simConnect = null;
            }
            simConnectWindow.Destroy(); // Stop the message loop
        }

        // Refreshes the SimConnect connection (disconnects and reconnects)
        public void RefreshConnection()
        {
            if (_simConnect != null)
            {
                Disconnect();
            }
            TryConnect();
        }

        // Attempts to establish a SimConnect connection and set up event handlers
        private void TryConnect()
        {
            _logger.LogInformation("Attempting SimConnect connection.");
            try
            {
                IntPtr hWnd = simConnectWindow.Handle;
                _simConnect = new SimConnect("MSFS Web API", hWnd, WM_USER_SIMCONNECT, null, 0);
                IsConnected = true;
                // Register event handlers
                _simConnect.OnRecvOpen += new SimConnect.RecvOpenEventHandler(SimConnect_OnRecvOpen);
                _simConnect.OnRecvQuit += new SimConnect.RecvQuitEventHandler(SimConnect_OnRecvQuit);
                _simConnect.OnRecvException += new SimConnect.RecvExceptionEventHandler(SimConnect_OnRecvException);
                _simConnect.OnRecvSimobjectDataBytype += new SimConnect.RecvSimobjectDataBytypeEventHandler(SimConnect_OnRecvSimobjectDataBytype);

                _logger.LogInformation("SimConnect connection established, starting message timer.");

                // Start the timer to periodically receive messages
                _messageTimer = new System.Threading.Timer(
                    // The method to call
                    _ => ReceiveSimConnectMessages(),
                    // State (not used here)
                    null,
                    // Due time (start immediately)
                    0,
                    // Period (repeat every 10ms)
                    DISPATCH_INTERVAL_MS
                );
            }
            catch (COMException ex)
            {
                IsConnected = false;
                _logger.LogWarning(ex, "SimConnect COMException during connection.");
            }
            catch (Exception ex)
            {
                IsConnected = false;
                _logger.LogError(ex, "SimConnect general exception during connection.");
            }
        }

        // Receives messages from SimConnect (called by timer)
        public void ReceiveSimConnectMessages()
        {
            try
            {
                // The SimConnect DLL is designed to handle multi-threading safety when 
                // calling ReceiveMessage() from a separate thread than the one that 
                // created the connection, provided the underlying Win32 message pump is active.
                _simConnect?.ReceiveMessage();
            }
            catch (COMException ex)
            {
                // Handle disconnection or other critical COM errors
                Console.WriteLine($"Error receiving SimConnect messages: {ex.Message}");
            }
        }

        private void SimConnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            _logger.LogError($"SimConnect_OnRecvException: {data.dwException}, SendID: {data.dwSendID}, Index: {data.dwIndex}");
        }

        private void SimConnect_OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            _logger.LogInformation("SimConnect_OnRecvQuit");
        }

        private void SimConnect_OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            _logger.LogInformation("SimConnect_OnRecvOpen");
        }

        // Handles SimVar data responses
        private void SimConnect_OnRecvSimobjectDataBytype(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data)
        {
            _logger.LogDebug("SimConnect_OnRecvSimobjectDataBytype");

            uint iRequest = data.dwRequestID;
            uint iObject = data.dwObjectID;

            if (idManager.TryGetReference((int)iRequest, out SimVarReference? reference))
            {
                var simVarData = (double)data.dwData[0];
                _logger.LogInformation($"Received data for RequestID: {iRequest}, SimVar: {reference.SimVarName}, Value: {simVarData}");
                // Data can be processed or stored here if needed
            }
            else
            {
                _logger.LogWarning($"Received data for unknown RequestID: {iRequest}");
            }
        }

        // Registers SimVar definition with SimConnect if not already registered
        private DEFINITION EnsureSimVarRegistered(SimVarReference reference)
        {
            var (uniqueId, isNew) = idManager.GetOrAssignId(reference);
            var defineId = (DEFINITION)uniqueId;
            if (isNew)
            {
                _logger.LogInformation($"Registering SimVar: {reference.SimVarName}, Unit: {reference.Unit}, Index: {reference.Index}");
                _simConnect.AddToDataDefinition(
                    defineId,
                    reference.SimVarName,
                    reference.Unit,
                    SIMCONNECT_DATATYPE.FLOAT64,
                    0.0f,
                    SimConnect.SIMCONNECT_UNUSED
                );
                _simConnect.RegisterDataDefineStruct<double>(defineId);
            }
            return defineId;
        }

        // Gets a SimVar value asynchronously
        private int _nextRequestId = 0;
        public async Task<SimVarReference?> GetSimVarValueAsync(SimVarReference reference)
        {
            _logger.LogInformation($"GetSimVarValueAsync called with: SimVarName={reference?.SimVarName}, Unit={reference?.Unit}, Index={reference?.Index}");
            if (!IsConnected || _simConnect == null || reference == null)
            {
                _logger.LogWarning("SimConnect not connected or reference is null.");
                return null;
            }

            var defineId = EnsureSimVarRegistered(reference);
            int localRequestId = Interlocked.Increment(ref _nextRequestId);
            var requestId = (REQUEST)localRequestId;

            var tcs = new TaskCompletionSource<double?>();
            void OnRecvSimVar(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
            {
                _logger.LogInformation($"OnRecvSimVar called. RequestID: {data.dwRequestID}");
                if (data.dwRequestID == (uint)requestId)
                {
                    var simVarData = (double)data.dwData[0];
                    _logger.LogInformation($"SimVar value received: {simVarData}");
                    tcs.TrySetResult(simVarData);
                    _simConnect.OnRecvSimobjectData -= OnRecvSimVar;
                }
                else
                {
                    _logger.LogWarning($"Received data for unexpected RequestID: {data.dwRequestID}");
                }
            }

            _logger.LogInformation($"Requesting SimVar data from SimConnect with requestId {localRequestId}...");
            _simConnect.OnRecvSimobjectData += OnRecvSimVar;
            _simConnect.RequestDataOnSimObject(requestId, defineId, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

            var task = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            double? value = null;
            if (task == tcs.Task)
            {
                _logger.LogInformation($"Returning SimVar value: {tcs.Task.Result}");
                value = tcs.Task.Result;
            }
            else
            {
                _logger.LogWarning("Timeout waiting for SimVar value.");
                _simConnect.OnRecvSimobjectData -= OnRecvSimVar;
            }
            // Return a new SimVarReference with the value set
            return reference with { Value = value ?? double.NaN };
        }

        // Sets a SimVar value asynchronously
        public async Task<SimVarReference?> SetSimVarValueAsync(SimVarReference reference)
        {
            _logger.LogInformation($"SetSimVarValueAsync called with: SimVarName={reference?.SimVarName}, Unit={reference?.Unit}, Index={reference?.Index}, Value={reference?.Value}");
            if (!IsConnected || _simConnect == null || reference == null)
            {
                _logger.LogWarning("SimConnect not connected or reference is null.");
                return null;
            }

            var defineId = EnsureSimVarRegistered(reference);
            int localRequestId = Interlocked.Increment(ref _nextRequestId);
            var requestId = (REQUEST)localRequestId;

            var tcs = new TaskCompletionSource<bool>();
            void OnSetSimVar(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
            {
                _logger.LogInformation($"OnSetSimVar called. RequestID: {data.dwRequestID}");
                if (data.dwRequestID == (uint)requestId)
                {
                    tcs.TrySetResult(true);
                    _simConnect.OnRecvSimobjectData -= OnSetSimVar;
                }
                else
                {
                    _logger.LogWarning($"Received data for unexpected RequestID: {data.dwRequestID}");
                }
            }

            _logger.LogInformation($"Setting SimVar value via SimConnect with requestId {localRequestId}...");
            _simConnect.OnRecvSimobjectData += OnSetSimVar;
            _simConnect.SetDataOnSimObject(defineId, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, reference.Value);
            // Optionally, request confirmation (not all SimVars support confirmation)
            _simConnect.RequestDataOnSimObject(requestId, defineId, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

            var task = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            if (task == tcs.Task && tcs.Task.Result)
            {
                _logger.LogInformation($"SetSimVarValueAsync succeeded for SimVar: {reference.SimVarName}");
                return reference;
            }
            else
            {
                _logger.LogWarning("Timeout or failure setting SimVar value.");
                _simConnect.OnRecvSimobjectData -= OnSetSimVar;
                return null;
            }
        }
    }
}