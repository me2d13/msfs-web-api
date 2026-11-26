using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MsfsApiServer.Logging
{
    public sealed class SimpleFileLoggerProvider : ILoggerProvider
    {
        public const long DefaultMaxFileBytes = 20L * 1024 * 1024;
        public const int DefaultArchiveCount = 5;

        private readonly RollingLogFile? _file;
        private readonly LogLevel _minLevel;

        public SimpleFileLoggerProvider(
            string filePath,
            LogLevel minLevel = LogLevel.Information,
            long maxFileBytes = DefaultMaxFileBytes,
            int archiveCount = DefaultArchiveCount)
        {
            _minLevel = minLevel;
            try
            {
                _file = new RollingLogFile(filePath, maxFileBytes, archiveCount);
            }
            catch (Exception ex)
            {
                _file = null;
                Debug.WriteLine($"File logging disabled for '{filePath}': {ex}");
            }
        }

        public ILogger CreateLogger(string categoryName) => new SimpleFileLogger(_file, _minLevel, categoryName);

        public void Dispose() => _file?.Dispose();

        private sealed class RollingLogFile : IDisposable
        {
            private readonly string _filePath;
            private readonly long _maxFileBytes;
            private readonly int _archiveCount;
            private readonly object _sync = new();
            private StreamWriter? _writer;
            private bool _disposed;

            public RollingLogFile(string filePath, long maxFileBytes, int archiveCount)
            {
                if (maxFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
                if (archiveCount < 0) throw new ArgumentOutOfRangeException(nameof(archiveCount));

                _filePath = Path.GetFullPath(filePath);
                _maxFileBytes = maxFileBytes;
                _archiveCount = archiveCount;

                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                lock (_sync)
                {
                    if (File.Exists(_filePath) && new FileInfo(_filePath).Length >= _maxFileBytes)
                        Rotate();
                    else
                        Open(FileMode.Append);
                }
            }

            public void WriteLine(string line)
            {
                lock (_sync)
                {
                    if (_disposed || _writer == null) return;

                    try
                    {
                        var recordBytes = Encoding.UTF8.GetByteCount(line)
                            + Encoding.UTF8.GetByteCount(Environment.NewLine);
                        if (_writer.BaseStream.Length > 0 && _writer.BaseStream.Length + recordBytes > _maxFileBytes)
                            Rotate();

                        _writer?.WriteLine(line);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"File log write failed for '{_filePath}': {ex}");
                        RecoverAndWrite(line);
                    }
                }
            }

            private void Rotate()
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;

                try
                {
                    if (_archiveCount == 0)
                    {
                        if (File.Exists(_filePath)) File.Delete(_filePath);
                    }
                    else
                    {
                        var oldest = ArchivePath(_archiveCount);
                        if (File.Exists(oldest)) File.Delete(oldest);

                        for (var index = _archiveCount - 1; index >= 1; index--)
                        {
                            var source = ArchivePath(index);
                            if (File.Exists(source)) File.Move(source, ArchivePath(index + 1));
                        }

                        if (File.Exists(_filePath)) File.Move(_filePath, ArchivePath(1));
                    }

                    Open(FileMode.Create);
                    _writer!.WriteLine(
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [Information] MsfsApiServer.Logging.SimpleFileLogger: " +
                        $"Log rolled at {_maxFileBytes} bytes; retaining {_archiveCount} archive(s).");
                }
                catch (Exception ex)
                {
                    try { _writer?.Dispose(); } catch { }
                    _writer = null;
                    Debug.WriteLine($"File log rollover failed for '{_filePath}': {ex}");
                    try { Open(FileMode.Append); } catch { _writer = null; }
                }
            }

            private string ArchivePath(int index) => _filePath + "." + index;

            private void Open(FileMode mode)
            {
                var stream = new FileStream(
                    _filePath,
                    mode,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
            }

            private void RecoverAndWrite(string line)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
                try
                {
                    Open(FileMode.Append);
                    _writer?.WriteLine(line);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"File log recovery failed for '{_filePath}': {ex}");
                    _writer = null;
                }
            }

            public void Dispose()
            {
                lock (_sync)
                {
                    if (_disposed) return;
                    _disposed = true;
                    try { _writer?.Dispose(); } catch { }
                    _writer = null;
                }
            }
        }

        private sealed class SimpleFileLogger : ILogger
        {
            private readonly RollingLogFile? _file;
            private readonly LogLevel _minLevel;
            private readonly string _category;

            public SimpleFileLogger(RollingLogFile? file, LogLevel minLevel, string category)
            {
                _file = file;
                _minLevel = minLevel;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) =>
                _file != null && logLevel != LogLevel.None && logLevel >= _minLevel;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var line = $"{timestamp} [{logLevel}] {_category}: {formatter(state, exception)}";
                if (exception != null) line += Environment.NewLine + exception;
                _file!.WriteLine(line);
            }
        }
    }
}
