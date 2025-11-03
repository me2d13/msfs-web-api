using System;

class Program
{
    public static void Main(string[] args)
    {
        // Start the web API host and get port and local IP
        var (port, localIp) = WebApiHost.Start(args);
        // Start the tray icon UI
        TrayIconManager.Start(port, localIp);
        // The web server runs in the background (non-blocking)
        // The tray icon thread runs its own message loop
        // Main thread can just wait
        Thread.Sleep(Timeout.Infinite);
    }
}


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
