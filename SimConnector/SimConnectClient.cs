using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using static SimConnector.SimConnectClient;

/*
Example usage with curl:

Single variable operations:
curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA STATE"}'
curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA VIEW TYPE AND INDEX:1"}'
curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "FLAPS HANDLE INDEX","unit": "Number"}'
curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "GENERAL ENG THROTTLE LEVER POSITION:1","unit": "Percent"}'
curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA VIEW TYPE AND INDEX:0","unit": ""}'

curl -X 'POST' 'http://localhost:5018/api/simvar/set' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA STATE", "value": 3}'
curl -X 'POST' 'http://localhost:5018/api/simvar/set' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "FLAPS HANDLE INDEX", "value": 1}'
curl -X 'POST' 'http://localhost:5018/api/simvar/set' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA VIEW TYPE AND INDEX:1", "value": 2}'

Multiple variable operations:
curl -X 'POST' 'http://localhost:5018/api/simvar/getMultiple' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '[{"simVarName": "CAMERA STATE","unit": ""}, {"simVarName": "FLAPS HANDLE INDEX","unit": "Number"}]'
curl -X 'POST' 'http://localhost:5018/api/simvar/getMultiple' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '[{"simVarName": "CAMERA STATE"}, {"simVarName": "CAMERA VIEW TYPE AND INDEX:0"}, {"simVarName": "CAMERA VIEW TYPE AND INDEX:1"}]'
// Switch to cockpit and set flaps to 2
curl -X 'POST' 'http://localhost:5018/api/simvar/setMultiple' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '[{"simVarName": "CAMERA STATE", "value": 2}, {"simVarName": "FLAPS HANDLE INDEX", "unit": "Number", "value": 2}]'
cockipt instrument view 3:
curl -X 'POST' 'http://localhost:5018/api/simvar/setMultiple' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '[{"simVarName": "CAMERA STATE", "value": 2}, {"simVarName": "CAMERA VIEW TYPE AND INDEX:1", "value": 2}]'
curl -X 'POST' 'http://localhost:5018/api/simvar/setMultiple' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '[{"simVarName": "CAMERA STATE", "value": 2}, {"simVarName": "CAMERA VIEW TYPE AND INDEX:0", "value": 2}, {"simVarName": "CAMERA VIEW TYPE AND INDEX:1", "value": 2}]'


Events:
curl -X 'POST' 'http://localhost:5018/api/event/send' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"name": "TAXI_LIGHTS_SET", "value": 1}'
curl -X 'POST' 'http://localhost:5018/api/event/send' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"name": "AXIS_PAN_HEADING", "value": 90}'
curl -X 'POST' 'http://localhost:5018/api/event/send' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"name": "EYEPOINT_RESET"}'
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
        private System.Threading.Timer _messageTimer;
        private const int DISPATCH_INTERVAL_MS = 100;

        // Request tracking for parallel operations
        private class PendingRequest
        {
            public TaskCompletionSource<double?> TaskCompletion { get; set; } = new();
            public SimVarReference Reference { get; set; }
            public volatile bool IsCompleted;  // Track completion state
        }
        private readonly ConcurrentDictionary<uint, PendingRequest> _pendingRequests = new();

        public enum DEFINITION
        {
            Dummy = 0
        };

        public enum REQUEST
        {
            Dummy = 0,
            Struct1
        };

        public enum EVENT
        {
            Dummy = 0
        };

        // Define an enum for Notification Groups
        public enum GROUP_ID
        {
            GROUP0 = 0 // Using 0 as the standard default group ID
        }

        public bool IsConnected { get; private set; } = false;

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
                _simConnect.OnRecvSimobjectData += OnRecvSimVar; // Centralized handler

                _logger.LogInformation("SimConnect connection established, starting message timer.");

                _messageTimer = new System.Threading.Timer(
                    _ => ReceiveSimConnectMessages(),
                    null,
                    0,
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

        // EnsureConnected: attempt a single immediate reconnect if disconnected
        private bool EnsureConnected()
        {
            if (IsConnected && _simConnect != null)
                return true;

            _logger.LogInformation("Connection lost. Attempting reconnect on demand.");
            TryConnect();

            // Wait briefly for connection to establish
            int waited = 0;
            const int waitStep = 50;
            const int maxWait = 500; // ms
            while (!IsConnected && waited < maxWait)
            {
                Thread.Sleep(waitStep);
                waited += waitStep;
            }

            if (!IsConnected)
                _logger.LogWarning("Reconnect attempt failed.");

            return IsConnected;
        }

        public void ReceiveSimConnectMessages()
        {
            try
            {
                _simConnect?.ReceiveMessage();
            }
            catch (COMException ex)
            {
                // Detect disconnection and perform cleanup to prevent repeated exceptions
                _logger.LogWarning(ex, "COMException from ReceiveMessage - treating as disconnect.");
                OnDisconnectDetected("COMException in ReceiveMessage");
            }
        }

        private void SimConnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            _logger.LogError($"SimConnect_OnRecvException: {data.dwException}, SendID: {data.dwSendID}, Index: {data.dwIndex}");
            // Clean up pending request if it exists
            if (_pendingRequests.TryRemove(data.dwSendID, out var request))
            {
                request.TaskCompletion.TrySetException(new Exception($"SimConnect exception: {data.dwException}"));
            }
        }

        private void SimConnect_OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            _logger.LogInformation("SimConnect_OnRecvQuit");
            // MSFS requested quit - treat as disconnect
            OnDisconnectDetected("Received Quit from SimConnect");
        }

        private void SimConnect_OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            _logger.LogInformation("SimConnect_OnRecvOpen");
        }

        // Centralized handler for SimVar responses
        private void OnRecvSimVar(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            uint requestId = data.dwRequestID;
            _logger.LogInformation($"OnRecvSimVar called. RequestID: {requestId}");

            if (_pendingRequests.TryRemove(requestId, out var request))
            {
                if (!request.IsCompleted)  // Only process if not already completed
                {
                    var simVarData = (double)data.dwData[0];
                    _logger.LogInformation($"SimVar value received for {request.Reference.SimVarName}: {simVarData}");
                    request.TaskCompletion.TrySetResult(simVarData);
                }
            }
            else
            {
                _logger.LogWarning($"Received data for unknown RequestID: {requestId}");
            }
        }

        private DEFINITION EnsureSimVarRegistered(SimVarReference reference)
        {
            var (uniqueId, isNew) = idManager.GetOrAssignId(reference);
       var defineId = (DEFINITION)uniqueId;
    if (isNew)
   {
        _logger.LogInformation($"Registering SimVar: {reference.SimVarName}, Unit: {reference.Unit}");
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

        private int _nextRequestId = 0;
        public async Task<SimVarReference?> GetSimVarValueAsync(SimVarReference reference)
     {
            _logger.LogInformation($"GetSimVarValueAsync called with: SimVarName={reference?.SimVarName}, Unit={reference?.Unit}");
            if (reference == null)
            {
                _logger.LogWarning("Reference is null.");
                return null;
            }

            if (!EnsureConnected())
            {
                _logger.LogWarning("SimConnect not connected after reconnect attempt.");
                return null;
            }

            if (_simConnect == null)
            {
                _logger.LogWarning("SimConnect instance is null after reconnect.");
                return null;
            }

            var defineId = EnsureSimVarRegistered(reference);
            int localRequestId = Interlocked.Increment(ref _nextRequestId);
            var requestId = (REQUEST)localRequestId;

            // Create request tracking object
            var pendingRequest = new PendingRequest
            {
                Reference = reference,
            };

            // Add to pending requests before making the request
            _pendingRequests.TryAdd((uint)localRequestId, pendingRequest);

            try
            {
                _simConnect.RequestDataOnSimObject(requestId, defineId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

                // Wait for response or timeout
                var timeoutTask = Task.Delay(2000);
                var completedTask = await Task.WhenAny(pendingRequest.TaskCompletion.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    pendingRequest.IsCompleted = true;  // Mark as completed
                    _pendingRequests.TryRemove((uint)localRequestId, out _);
                    return reference with { Value = double.NaN };
                }

                var value = await pendingRequest.TaskCompletion.Task;
                pendingRequest.IsCompleted = true;  // Mark as completed
                return reference with { Value = value ?? double.NaN };
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "COMException during GetSimVarValueAsync - treating as disconnect.");
                OnDisconnectDetected("COMException during GetSimVarValueAsync");
                _pendingRequests.TryRemove((uint)localRequestId, out _);
                return reference with { Value = double.NaN };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting SimVar value");
                _pendingRequests.TryRemove((uint)localRequestId, out _);
                return reference with { Value = double.NaN };
            }
        }

        public async Task<SimVarReference?> SetSimVarValueAsync(SimVarReference reference)
        {
            _logger.LogInformation($"SetSimVarValueAsync called with: SimVarName={reference?.SimVarName}, Unit={reference?.Unit}, Value={reference?.Value}");
            if (reference == null)
            {
                _logger.LogWarning("Reference is null.");
                return null;
            }

            if (!EnsureConnected())
            {
                _logger.LogWarning("SimConnect not connected after reconnect attempt.");
                return null;
            }

            if (_simConnect == null)
            {
                _logger.LogWarning("SimConnect instance is null after reconnect.");
                return null;
            }

            var defineId = EnsureSimVarRegistered(reference);
            int localRequestId = Interlocked.Increment(ref _nextRequestId);
            var requestId = (REQUEST)localRequestId;

            // Create request tracking object
            var pendingRequest = new PendingRequest
            {
                Reference = reference,
            };

            _pendingRequests.TryAdd((uint)localRequestId, pendingRequest);

            try
            {
                _simConnect.SetDataOnSimObject(defineId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_DATA_SET_FLAG.DEFAULT, reference.Value);

                // Request confirmation
                _simConnect.RequestDataOnSimObject(requestId, defineId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

                var timeoutTask = Task.Delay(2000);
                var completedTask = await Task.WhenAny(pendingRequest.TaskCompletion.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning("Timeout waiting for SimVar set confirmation.");
                    _pendingRequests.TryRemove((uint)localRequestId, out _);
                    return null;
                }

                await pendingRequest.TaskCompletion.Task;
                return reference;
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "COMException during SetSimVarValueAsync - treating as disconnect.");
                OnDisconnectDetected("COMException during SetSimVarValueAsync");
                _pendingRequests.TryRemove((uint)localRequestId, out _);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting SimVar value");
                _pendingRequests.TryRemove((uint)localRequestId, out _);
                return null;
            }
        }

        // Gets multiple SimVar values asynchronously (parallel execution)
        public async Task<List<SimVarReference>> GetMultipleSimVarValuesAsync(List<SimVarReference> references)
        {
            _logger.LogInformation($"GetMultipleSimVarValuesAsync called with {references?.Count ?? 0} references.");
            if (references == null || references.Count == 0)
            {
                _logger.LogWarning("References list is null/empty.");
                return new List<SimVarReference>();
            }

            if (!EnsureConnected())
            {
                _logger.LogWarning("SimConnect not connected after reconnect attempt.");
                return new List<SimVarReference>();
            }

            // Execute all get requests in parallel since order doesn't matter
            var tasks = references.Select(reference => GetSimVarValueAsync(reference)).ToList();
            var results = await Task.WhenAll(tasks);

            // Filter out null results and return the list
            var validResults = results.Where(r => r != null).Cast<SimVarReference>().ToList();
            _logger.LogInformation($"GetMultipleSimVarValuesAsync completed. Retrieved {validResults.Count} out of {references.Count} values.");
            return validResults;
        }

        // Sets multiple SimVar values asynchronously (sequential execution)
        public async Task<List<SimVarReference>> SetMultipleSimVarValuesAsync(List<SimVarReference> references)
        {
            _logger.LogInformation($"SetMultipleSimVarValuesAsync called with {references?.Count ?? 0} references.");
            if (references == null || references.Count == 0)
            {
                _logger.LogWarning("References list is null/empty.");
                return new List<SimVarReference>();
            }

            if (!EnsureConnected())
            {
                _logger.LogWarning("SimConnect not connected after reconnect attempt.");
                return new List<SimVarReference>();
            }

            var results = new List<SimVarReference>();

            // Execute set requests sequentially to maintain order
            foreach (var reference in references)
            {
                var result = await SetSimVarValueAsync(reference);
                if (result != null)
                {
                    results.Add(result);
                }
                else
                {
                    _logger.LogWarning($"Failed to set SimVar: {reference.SimVarName}. Continuing with next variable.");
                }
            }

            _logger.LogInformation($"SetMultipleSimVarValuesAsync completed. Successfully set {results.Count} out of {references.Count} values.");
            return results;
        }

        private readonly ConcurrentDictionary<string, int> _registeredEvents = new();
        private int _nextEventId = 1; // Start from 1 as 0 is Dummy

        public void SendEvent(EventReference eventReference)
        {
            if (eventReference == null)
            {
                _logger.LogWarning("EventReference is null.");
                return;
            }

            if (!EnsureConnected())
            {
                _logger.LogWarning("SimConnect not connected after reconnect attempt. Event not sent.");
                return;
            }

            try
            {
                // Check if the event is already registered
                if (!_registeredEvents.TryGetValue(eventReference.Name, out var simEventNum))
                {
                    simEventNum = _nextEventId++;
                    _logger.LogInformation($"Registering {eventReference.Name} under id {simEventNum}");
                    _simConnect.MapClientEventToSimEvent((EVENT)simEventNum, eventReference.Name);
                    _simConnect.AddClientEventToNotificationGroup(
                        // Notification Group ID (a custom enum/int for grouping events)
                        (GROUP_ID)0,
                        // Your custom event ID
                        (EVENT)simEventNum,
                        false
                    );

                    // Register the event with a new ID
                    _registeredEvents.TryAdd(eventReference.Name, simEventNum);
                }

                _simConnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, (EVENT)simEventNum, eventReference.Value, (GROUP_ID)0, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                _logger.LogInformation($"Sent event: {eventReference.Name}");
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "COMException during SendEvent - treating as disconnect.");
                OnDisconnectDetected("COMException during SendEvent");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending event: {eventReference.Name}");
            }

        }

        private void OnDisconnectDetected(string reason)
        {
            _logger.LogWarning($"OnDisconnectDetected called: {reason}. Cleaning up resources.");

            if (!IsConnected) return; // Already disconnected

            IsConnected = false;

            // Dispose the message timer
            _messageTimer?.Dispose();
            _messageTimer = null;

            // Clear registered events
            _registeredEvents.Clear();
            _nextEventId = 1; // Reset event ID counter

            // Clear request ID manager
            idManager.Clear();

            // Reset pending requests
            foreach (var request in _pendingRequests)
            {
                request.Value.TaskCompletion.TrySetCanceled();
            }
            _pendingRequests.Clear();

            // Unsubscribe from SimConnect events and dispose SimConnect instance
            if (_simConnect != null)
            {
                try
                {
                    _simConnect.OnRecvOpen -= SimConnect_OnRecvOpen;
                    _simConnect.OnRecvQuit -= SimConnect_OnRecvQuit;
                    _simConnect.OnRecvException -= SimConnect_OnRecvException;
                    _simConnect.OnRecvSimobjectData -= OnRecvSimVar;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error unsubscribing SimConnect events");
                }
                finally
                {
                    _simConnect.Dispose();
                    _simConnect = null;
                }
            }

            _logger.LogInformation("Cleanup complete. Resources released.");
        }
    }

}
