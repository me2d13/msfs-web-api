MSFS Web API Server
===================

A small tray app that exposes Microsoft Flight Simulator's SimConnect through a local web API
(REST, WebSocket and UDP), so browser-based cockpit panels can read and write simulator variables.
Project and documentation: https://github.com/me2d13/msfs-web-api


What you need
-------------
- Windows 10 or 11, 64-bit
- Microsoft Flight Simulator 2020 or 2024. The app starts and serves the API without the sim, but
  nothing is answered until the sim is running.
- .NET 8 runtimes - unless you downloaded the "self-contained" zip, which needs none:
    * .NET 8 Desktop Runtime   (the tray icon and the windows)
    * .NET 8 ASP.NET Core Runtime (the web server)
  Both are on https://dotnet.microsoft.com/download/dotnet/8.0 under "Run apps - Runtime".
  Note: the Desktop Runtime installer does NOT contain the ASP.NET Core Runtime; both are needed.


Running it
----------
1. Unpack this folder anywhere, for example C:\Tools\MsfsApiServer. Keep the files together.
2. Start MsfsApiServer.exe. A plane icon appears in the tray and the API listens on port 5018.
3. Double click the tray icon (or use its "Open status page" menu item) to open
   http://localhost:5018/ - it shows the connection state, what variables have been asked for,
   the running configuration and your own pages.
4. API documentation (Swagger): http://localhost:5018/swagger
5. To reach it from another machine (a tablet on the same network), allow it through the Windows
   Firewall when Windows asks, and use http://<your-pc-ip>:5018/ instead of localhost.
6. Quit it from the tray menu; the log is MsfsApiServer.log next to the exe.


Configuration (optional)
------------------------
Copy config.yaml.example to config.yaml next to the exe and edit it to set the port, the log file,
UDP or serial output and the variables to stream. Command line arguments override the file:

    MsfsApiServer.exe --config my.yaml --logLevel Debug


Your own pages
--------------
Put html files into the www folder next to the exe. They are served at
http://localhost:5018/<file> and listed at the top of the status page (a file called views.html
appears as "views"). css, js, json, images, fonts, audio and video are served as well, and
subfolders work, so a page can use <img src="/img/plane.jpg"> or <link href="/css/panel.css">.

www/test.html is a small example page: it reads a variable (optionally polling), writes one, and
reads several at once, including a client data area field.
