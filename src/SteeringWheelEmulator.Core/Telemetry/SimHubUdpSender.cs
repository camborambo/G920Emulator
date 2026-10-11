using System.Net;
using System.Net.Sockets;

namespace SteeringWheelEmulator.Core.Telemetry;

public sealed class SimHubUdpSender : IDisposable
{
    private readonly object _gate = new();
    private UdpClient? _udp;
    private IPEndPoint? _endPoint;
    private string _host = SimHubPacket.DefaultHost;
    private int _port = SimHubPacket.DefaultPort;
    private string? _error;
    private long _sent;
    private long _windowStart = Environment.TickCount64;
    private int _windowCount;
    private double _packetsPerSec;
    private bool _disposed;

    public string? LastError { get { lock (_gate) return _error; } }
    public double PacketsPerSecond { get { lock (_gate) return _packetsPerSec; } }
    public long PacketsSent { get { lock (_gate) return _sent; } }

    public void Configure(string host, int port)
    {
        host = string.IsNullOrWhiteSpace(host) ? SimHubPacket.DefaultHost : host.Trim();
        port = port is < 1 or > 65535 ? SimHubPacket.DefaultPort : port;
        lock (_gate)
        {
            if (_host == host && _port == port && _udp is not null)
                return;
            _host = host;
            _port = port;
            Recreate_NoLock();
        }
    }

    public bool TrySend(byte[] packet)
    {
        UdpClient? udp;
        IPEndPoint? ep;
        lock (_gate)
        {
            if (_disposed) return false;
            if (_udp is null)
                Recreate_NoLock();
            udp = _udp;
            ep = _endPoint;
        }

        if (udp is null || ep is null)
            return false;

        try
        {
            // Synchronous localhost UDP is fine at ≤60 Hz; avoid BeginSend allocs.
            udp.Send(packet, packet.Length, ep);
            NoteSent();
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _error = ex.Message;
                Close_NoLock();
            }
            return false;
        }
    }

    public void ResetStats()
    {
        lock (_gate)
        {
            _sent = 0;
            _windowCount = 0;
            _packetsPerSec = 0;
            _windowStart = Environment.TickCount64;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Close_NoLock();
        }
    }

    private void Recreate_NoLock()
    {
        Close_NoLock();
        try
        {
            if (!IPAddress.TryParse(_host, out var ip))
            {
                var addrs = Dns.GetHostAddresses(_host);
                ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? throw new InvalidOperationException("no IPv4 address");
            }
            _endPoint = new IPEndPoint(ip, _port);
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _udp = null;
            _endPoint = null;
        }
    }

    private void NoteSent()
    {
        lock (_gate)
        {
            _sent++;
            _windowCount++;
            _error = null;
            var now = Environment.TickCount64;
            var elapsed = now - _windowStart;
            if (elapsed >= 500)
            {
                _packetsPerSec = _windowCount * 1000.0 / Math.Max(1, elapsed);
                _windowCount = 0;
                _windowStart = now;
            }
        }
    }

    private void Close_NoLock()
    {
        try { _udp?.Dispose(); } catch { /* ignore */ }
        _udp = null;
    }
}
