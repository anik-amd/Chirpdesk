using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Chirpdesk.Notifications;

/// <summary>
/// The callback Windows invokes when the user clicks a toast that is no longer on screen —
/// i.e. the normal Action Center click. An unpackaged app gets no callback at all unless its
/// AUMID resolves to a registered COM class, which is why clicking a timed-out toast did
/// nothing before this existed. The class itself holds no state: it signals the resident
/// instance through the same named event a second launch uses.
/// </summary>
[ComVisible(true)]
[Guid("6F1D3A3E-6B0F-4A7A-9A9E-2F0B7C5D4E21")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ToastActivationCallback : INotificationActivationCallback
{
    public const string Clsid = "{6F1D3A3E-6B0F-4A7A-9A9E-2F0B7C5D4E21}";

    internal static Action<string?>? Activated { get; set; }

    public void Activate(string appUserModelId, string invokedArgs, IntPtr data, uint dataCount)
    {
        // Never log the launch string: a future deep link may carry a chat id.
        ToastActivation.Log?.Invoke($"toast activation: activate called, args length {invokedArgs?.Length ?? 0}");
        Activated?.Invoke(invokedArgs);
    }
}

/// <summary>Registry, shortcut property and process-mode plumbing for toast activation.</summary>
internal static class ToastActivation
{
    public const string ServerArgument = "-ToastActivated";
    private const string EmbeddingArgument = "-Embedding";

    /// <summary>Set by the owning process; the COM callback has no logger of its own.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>The shortcut property is only honoured as a CLSID value, never a string.</summary>
    public static Guid Clsid => new("6F1D3A3E-6B0F-4A7A-9A9E-2F0B7C5D4E21");

    private static string ClsidKey => $@"Software\Classes\CLSID\{ToastActivationCallback.Clsid}";

    /// <summary>
    /// Per-user registration: no elevation, and removed by uninstall alongside the shortcut.
    /// A machine-wide install is not needed for a tray app.
    /// </summary>
    public static void RegisterServer(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ClsidKey + @"\LocalServer32");
        key.SetValue(null, $"\"{executablePath}\" {ServerArgument}");
    }

    public static void UnregisterServer() =>
        Registry.CurrentUser.DeleteSubKeyTree(ClsidKey, throwOnMissingSubKey: false);

    public static bool IsRegistered(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ClsidKey + @"\LocalServer32");
        var value = key?.GetValue(null) as string;
        return !string.IsNullOrEmpty(value)
            && value.Contains(executablePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// COM starts a local server with <c>-Embedding</c> appended, so both tokens mean the
    /// same thing: this process exists to answer one activation, not to become the app.
    /// </summary>
    public static bool IsServerMode(string[] args) =>
        Array.Exists(args, a =>
            a.Equals(ServerArgument, StringComparison.OrdinalIgnoreCase)
            || a.Equals(EmbeddingArgument, StringComparison.OrdinalIgnoreCase));

    private const uint ClsctxLocalServer = 0x4;
    private const uint RegclsMultipleUse = 0x1;

    private static ToastActivationFactory? s_factory;
    private static uint s_cookie;

    /// <summary>
    /// Registers the class factory for this process. .NET (Core) has no
    /// <c>RegistrationServices</c>, so this speaks to COM directly; the factory must stay
    /// referenced or COM gets a dangling pointer.
    /// </summary>
    public static void RegisterClassObject()
    {
        s_factory = new ToastActivationFactory();
        var clsid = Clsid;

        var hr = CoRegisterClassObject(
            ref clsid,
            s_factory,
            ClsctxLocalServer,
            RegclsMultipleUse,
            out s_cookie);

        if (hr < 0)
        {
            s_factory = null;
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public static void RevokeClassObject()
    {
        if (s_cookie != 0)
        {
            _ = CoRevokeClassObject(s_cookie);
            s_cookie = 0;
        }

        s_factory = null;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoRegisterClassObject(
        ref Guid rclsid,
        [MarshalAs(UnmanagedType.Interface)] IClassFactory pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoRevokeClassObject(uint dwRegister);
}

/// <summary>
/// Supplies one <see cref="ToastActivationCallback"/> instance to COM. Raw <see cref="IntPtr"/>
/// signatures and <see cref="Marshal.GetComInterfaceForObject(object, Type)"/> follow the
/// Community Toolkit implementation that is known to work on .NET Core; the tidier managed
/// signature is not the shape COM gets routed through.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ToastActivationFactory : IClassFactory
{
    private static readonly Guid UnknownIid = new("00000000-0000-0000-C000-000000000046");

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppv)
    {
        ToastActivation.Log?.Invoke($"toast activation factory: create instance for {riid:B}");
        ppv = IntPtr.Zero;

        if (pUnkOuter != IntPtr.Zero)
        {
            return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
        }

        if (riid != UnknownIid && riid != typeof(INotificationActivationCallback).GUID)
        {
            ToastActivation.Log?.Invoke("toast activation factory: unsupported interface");
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        ppv = Marshal.GetComInterfaceForObject(
            new ToastActivationCallback(),
            typeof(INotificationActivationCallback));

        return 0; // S_OK
    }

    public int LockServer(bool fLock) => 0;
}

[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int LockServer(bool fLock);
}

[ComImport]
[Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INotificationActivationCallback
{
    void Activate(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
        IntPtr data,
        uint dataCount);
}
