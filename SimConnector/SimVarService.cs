using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;

namespace SimConnector
{
    public class SimVarService
    {
        private readonly SimConnectClient _connection;
        private readonly ILogger<SimVarService> _logger;
        private readonly ClientDataService _clientDataService;
        private readonly SimVarStatistics _statistics = new();
        private readonly SimConnectIdManager _idManager = new SimConnectIdManager();
        private int _nextRequestId = 0;
        private readonly ConcurrentDictionary<uint, PendingRequest> _pendingRequests = new();

        // Track connection state to avoid log spam
        private bool _lastKnownConnectionState = true;
        private readonly object _connectionStateLock = new();

        private class PendingRequest
        {
            public TaskCompletionSource<double?> TaskCompletion { get; set; } = new();
            public SimVarReference Reference { get; set; }
            public volatile bool IsCompleted;
        }

        public SimVarService(SimConnectClient connection, ILogger<SimVarService> logger)
        {
            _connection = connection;
            _logger = logger;
            _clientDataService = new ClientDataService(connection, logger);
            _connection.OnRecvSimVar += OnRecvSimVar;

            // Subscribe to connection state changes
            _connection.OnConnected += () =>
            {
                lock (_connectionStateLock)
                {
                    if (!_lastKnownConnectionState)
                    {
                        _logger.LogInformation("SimConnect connection established");
                        _lastKnownConnectionState = true;
                    }
                }
            };

            _connection.OnDisconnected += () =>
            {
                lock (_connectionStateLock)
                {
                    if (_lastKnownConnectionState)
                    {
                        _logger.LogWarning("SimConnect disconnected. Will attempt reconnection in the background.");
                        _lastKnownConnectionState = false;
                        // Clear ID cache on disconnect so we re-register definitions on reconnect
                        _idManager.Clear();
                    }
                }
            };
        }

        public SimConnectClient GetConnection() => _connection;

        /// <summary>
        /// Client data reader used for "CLIENT:" references.
        /// </summary>
        public ClientDataService GetClientDataService() => _clientDataService;

        /// <summary>
        /// Lookup counters, fed by every read and write that goes through this service.
        /// </summary>
        public SimVarStatistics GetStatistics() => _statistics;

        private void OnRecvSimVar(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            uint requestId = data.dwRequestID;
            double value = (double)data.dwData[0];
            SimVarReference reference = null;
            if (_pendingRequests.TryGetValue(requestId, out var req))
                reference = req.Reference;
            if (_pendingRequests.TryRemove(requestId, out var request))
            {
                if (!request.IsCompleted)
                {
                    _logger.LogTrace("SimVar value received for {SimVarName}: {Value}", reference?.SimVarName, value);
                    request.TaskCompletion.TrySetResult(value);
                }
            }
        }

        public async Task<SimVarReference?> GetSimVarValueAsync(SimVarReference reference)
        {
            var result = await GetSimVarValueCoreAsync(reference);
            _statistics.RecordRead(reference, result);
            return result;
        }

        /// <summary>
        /// The lookup itself; <see cref="GetSimVarValueAsync"/> wraps it to count the request.
        /// </summary>
        private async Task<SimVarReference?> GetSimVarValueCoreAsync(SimVarReference reference)
        {
            if (reference != null && reference.IsClientData)
            {
                return await _clientDataService.GetClientDataValueAsync(reference);
            }

            _logger.LogTrace("GetSimVarValueAsync called with: SimVarName={SimVarName}, Unit={Unit}", reference?.SimVarName, reference?.Unit);
            if (!_connection.IsConnected)
            {
                _logger.LogTrace("SimConnect not connected; returning null for {SimVarName}", reference?.SimVarName);
                return null;
            }
            var (uniqueId, isNew) = _idManager.GetOrAssignId(reference);
            var defineId = (SimConnectClient.DEFINITION)uniqueId;
            if (isNew)
            {
                _connection.AddToDataDefinition(defineId, reference);
            }
            int localRequestId = System.Threading.Interlocked.Increment(ref _nextRequestId);
            var requestId = (SimConnectClient.REQUEST)localRequestId;
            var pendingRequest = new PendingRequest { Reference = reference };
            _pendingRequests.TryAdd((uint)localRequestId, pendingRequest);
            _connection.RequestDataOnSimObject(requestId, defineId);
            var timeoutTask = Task.Delay(2000);
            var completedTask = await Task.WhenAny(pendingRequest.TaskCompletion.Task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                pendingRequest.IsCompleted = true;
                _pendingRequests.TryRemove((uint)localRequestId, out _);
                return reference with { Value = double.NaN };
            }
            var value = await pendingRequest.TaskCompletion.Task;
            pendingRequest.IsCompleted = true;
            return reference with { Value = value ?? double.NaN };
        }

        public async Task<SimVarReference?> SetSimVarValueAsync(SimVarReference reference)
        {
            var result = await SetSimVarValueCoreAsync(reference);
            _statistics.RecordWrite(reference, result);
            return result;
        }

        /// <summary>
        /// The write itself; <see cref="SetSimVarValueAsync"/> wraps it to count the request.
        /// </summary>
        private async Task<SimVarReference?> SetSimVarValueCoreAsync(SimVarReference reference)
        {
            if (reference != null && reference.IsClientData)
            {
                _logger.LogWarning("Client data areas are read-only through this API; set ignored.");
                return null;
            }

            _logger.LogDebug($"SetSimVarValueAsync called with: SimVarName={reference?.SimVarName}, Unit={reference?.Unit}, Value={reference?.Value}");
            if (!_connection.IsConnected)
            {
                _logger.LogDebug("SimConnect not connected, returning null");
                return null;
            }
            var (uniqueId, isNew) = _idManager.GetOrAssignId(reference);
            var defineId = (SimConnectClient.DEFINITION)uniqueId;
            if (isNew)
            {
                _connection.AddToDataDefinition(defineId, reference);
            }
            int localRequestId = System.Threading.Interlocked.Increment(ref _nextRequestId);
            var requestId = (SimConnectClient.REQUEST)localRequestId;
            var pendingRequest = new PendingRequest { Reference = reference };
            _pendingRequests.TryAdd((uint)localRequestId, pendingRequest);
            _connection.SetDataOnSimObject(defineId, reference.Value);
            _connection.RequestDataOnSimObject(requestId, defineId);
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

        public async Task<List<SimVarReference>> GetMultipleSimVarValuesAsync(List<SimVarReference> references)
        {
            var tasks = references.Select(reference => GetSimVarValueAsync(reference)).ToList();
            var results = await Task.WhenAll(tasks);
            return results.Where(r => r != null).Cast<SimVarReference>().ToList();
        }

        public async Task<List<SimVarReference>> SetMultipleSimVarValuesAsync(List<SimVarReference> references)
        {
            var results = new List<SimVarReference>();
            foreach (var reference in references)
            {
                var result = await SetSimVarValueAsync(reference);
                if (result != null)
                {
                    results.Add(result);
                }
            }
            return results;
        }
    }
}
