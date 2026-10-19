using System.Runtime.InteropServices;

namespace O2C.Service.Tests;

/// <summary>Временный адаптер Wintun для интеграционных тестов (нужны права администратора и wintun.dll).</summary>
internal sealed unsafe class WintunAdapter : IDisposable
{
    private readonly IntPtr _library;
    private readonly IntPtr _adapter;
    private readonly IntPtr _session;
    private readonly delegate* unmanaged<IntPtr, void> _closeAdapter;
    private readonly delegate* unmanaged<IntPtr, void> _endSession;

    public ulong Luid { get; }

    public WintunAdapter(string dllPath, string name)
    {
        _library = NativeLibrary.Load(dllPath);
        var create = (delegate* unmanaged<char*, char*, Guid*, IntPtr>)NativeLibrary.GetExport(_library, "WintunCreateAdapter");
        var getLuid = (delegate* unmanaged<IntPtr, ulong*, void>)NativeLibrary.GetExport(_library, "WintunGetAdapterLUID");
        var startSession = (delegate* unmanaged<IntPtr, uint, IntPtr>)NativeLibrary.GetExport(_library, "WintunStartSession");
        _closeAdapter = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(_library, "WintunCloseAdapter");
        _endSession = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(_library, "WintunEndSession");

        fixed (char* pName = name)
        fixed (char* pType = "O2CTest")
        {
            _adapter = create(pName, pType, null);
        }
        if (_adapter == IntPtr.Zero)
            throw new InvalidOperationException($"WintunCreateAdapter: {Marshal.GetLastPInvokeError()}");

        ulong luid;
        getLuid(_adapter, &luid);
        Luid = luid;

        // Сессия поднимает адаптер (media connected), как это делает openconnect.
        _session = startSession(_adapter, 0x400000);
        if (_session == IntPtr.Zero)
            throw new InvalidOperationException("WintunStartSession failed");
    }

    public void Dispose()
    {
        if (_session != IntPtr.Zero)
            _endSession(_session);
        // Адаптер, созданный WintunCreateAdapter, удаляется при закрытии.
        _closeAdapter(_adapter);
        NativeLibrary.Free(_library);
    }
}
