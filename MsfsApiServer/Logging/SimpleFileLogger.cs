using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MsfsApiServer.Logging
{
 public sealed class SimpleFileLoggerProvider : ILoggerProvider
 {
 private readonly StreamWriter? _writer;
 private readonly object _lock = new();
 private readonly LogLevel _minLevel;

 public SimpleFileLoggerProvider(string filePath, LogLevel minLevel = LogLevel.Information)
 {
 try
 {
 // Open or create the file for writing. Caller should delete existing file if needed.
 var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
 _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
 }
 catch
 {
 // If we can't write the file (permissions, path issues), silently disable logging to file.
 _writer = null;
 }
 _minLevel = minLevel;
 }

 public ILogger CreateLogger(string categoryName) => new SimpleFileLogger(_writer, _lock, _minLevel, categoryName);

 public void Dispose()
 {
 try { _writer?.Dispose(); } catch { /* ignore */ }
 }

 private sealed class SimpleFileLogger : ILogger
 {
 private readonly StreamWriter? _writer;
 private readonly object _lock;
 private readonly LogLevel _minLevel;
 private readonly string _category;

 public SimpleFileLogger(StreamWriter? writer, object sync, LogLevel minLevel, string category)
 {
 _writer = writer;
 _lock = sync;
 _minLevel = minLevel;
 _category = category;
 }

 public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

 public bool IsEnabled(LogLevel logLevel) => _writer != null && logLevel >= _minLevel;

 public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
 {
 if (!IsEnabled(logLevel)) return;
 var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
 var msg = formatter(state, exception);
 var line = $"{ts} [{logLevel}] {_category}: {msg}";
 if (exception != null)
 {
 line += Environment.NewLine + exception;
 }
 lock (_lock)
 {
 try { _writer!.WriteLine(line); }
 catch { /* silently ignore write errors */ }
 }
 }
 }
 }
}
