using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Provides a hidden message-only window for SimConnect message handling.
/// </summary>
public class SimConnectWindow
{
    private const string WindowClassName = "SimConnectMessageClass";
    private const int WM_QUIT = 0x0012;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // Structs for Win32 API calls
    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public System.Drawing.Point p;
    }

    private IntPtr _windowHandle = IntPtr.Zero;
    private Thread _messageLoopThread;

    // Synchronization object to signal when the handle is ready
    private ManualResetEvent _handleReadyEvent = new ManualResetEvent(false);

    /// <summary>
    /// Gets the handle of the message-only window.
    /// </summary>
    public IntPtr Handle => _windowHandle;

    /// <summary>
    /// Creates the message-only window and starts the message loop in a background thread.
    /// </summary>
    public void Create()
    {
        _handleReadyEvent.Reset();
        _messageLoopThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "SimConnectMessageThread"
        };
        _messageLoopThread.Start();
        // Wait until the window handle is ready
        _handleReadyEvent.WaitOne();
    }

    /// <summary>
    /// Destroys the message-only window and stops the message loop thread.
    /// </summary>
    public void Destroy()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            // Post a WM_QUIT message to gracefully exit the message loop thread
            PostMessage(_windowHandle, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _messageLoopThread.Join();
            _windowHandle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// The message loop for the hidden window. Processes Windows messages.
    /// </summary>
    private void MessageLoop()
    {
        // Register the window class
        WNDCLASSEX wcx = new WNDCLASSEX();
        wcx.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
        wcx.lpszClassName = WindowClassName;
        wcx.lpfnWndProc = Marshal.GetFunctionPointerForDelegate((WndProc)DefWindowProc);

        // For SimConnect, a custom WndProc is not required; the handle is sufficient.
        ushort atom = RegisterClassEx(ref wcx);
        if (atom == 0)
        {
            // Registration may fail if class is already registered
        }

        // Create the hidden message-only window
        const uint HWND_MESSAGE = 0xFFFFFFFF;
        _windowHandle = CreateWindowEx(
            0, WindowClassName, null, 0,
            0, 0, 0, 0,
            (IntPtr)HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        // Signal the main thread that the handle is now set
        _handleReadyEvent.Set();

        // Start the message pump
        MSG msg;
        while (GetMessage(out msg, _windowHandle, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    // Delegate for the window procedure
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}