using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;
using System.Collections.Concurrent;

namespace SimConnector
{
    public class SimEventService
    {
        private readonly SimConnectClient _connection;
        private readonly ILogger<SimEventService> _logger;
        private readonly ConcurrentDictionary<string, int> _registeredEvents = new();
        private int _nextEventId = 1;

        public SimEventService(SimConnectClient connection, ILogger<SimEventService> logger)
        {
            _connection = connection;
            _logger = logger;
        }

        public void SendEvent(EventReference eventReference)
        {
            if (eventReference == null)
            {
                _logger.LogWarning("EventReference is null.");
                return;
            }
            if (!_connection.IsConnected)
            {
                _logger.LogWarning(
                    "SimConnect not connected; dropping event {EventName} value {Value}.",
                    eventReference.Name,
                    eventReference.Value);
                return;
            }
            try
            {
                if (!_registeredEvents.TryGetValue(eventReference.Name, out var simEventNum))
                {
                    simEventNum = _nextEventId++;
                    _logger.LogInformation(
                        "Registering SimConnect event {EventName} under id {EventId}",
                        eventReference.Name,
                        simEventNum);
                    _connection.RegisterSimEvent(simEventNum, eventReference.Name);
                    _registeredEvents.TryAdd(eventReference.Name, simEventNum);
                }

                _connection.TransmitSimEvent(simEventNum, eventReference.Value, eventReference.Name);
                _logger.LogInformation(
                    "Queued SimConnect event {EventName} id {EventId} value {Value}",
                    eventReference.Name,
                    simEventNum,
                    eventReference.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error queueing SimConnect event {EventName} value {Value}",
                    eventReference.Name,
                    eventReference.Value);
            }
        }
    }
}
