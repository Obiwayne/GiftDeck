using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace GiftDeck.Services;

// One Source RCON packet: [size int32][id int32][type int32][body bytes][0][0], little-endian.
// size counts everything after itself (id + type + body + the two zero bytes).
public readonly record struct RconPacket(int Id, int Type, string Body)
{
    public const int TypeResponse = 0;     // SERVERDATA_RESPONSE_VALUE
    public const int TypeCommand = 2;      // SERVERDATA_EXECCOMMAND (also the auth reply type)
    public const int TypeAuth = 3;         // SERVERDATA_AUTH
    public const int MaxPacketSize = 4096 + 14; // Minecraft reads at most 4096 + header; bigger replies come split

    public byte[] Encode()
    {
        var body = Encoding.UTF8.GetBytes(Body ?? "");
        var buf = new byte[4 + 4 + 4 + body.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0), buf.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), Id);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), Type);
        body.CopyTo(buf, 12);
        return buf; // the last two bytes are already 0
    }

    // Reads one packet from the start of data. Returns the bytes used, or 0 when data doesn't hold a whole packet yet.
    public static int TryDecode(ReadOnlySpan<byte> data, out RconPacket packet)
    {
        packet = default;
        if (data.Length < 4) return 0;
        int size = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (size < 10 || size > 1024 * 1024) throw new InvalidDataException($"Bad RCON packet size {size}");
        if (data.Length < 4 + size) return 0;
        int id = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4));
        int type = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8));
        var bodyLen = size - 10;
        // Some servers send one trailing zero instead of two; trim any zeros at the end of the body.
        var body = data.Slice(12, bodyLen);
        while (body.Length > 0 && body[^1] == 0) body = body[..^1];
        packet = new RconPacket(id, type, Encoding.UTF8.GetString(body));
        return 4 + size;
    }
}

// A Source RCON client (what Minecraft servers speak on rcon.port). One command at a time per connection.
public sealed class RconClient : IDisposable
{
    TcpClient _tcp;
    NetworkStream _stream;
    readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    readonly byte[] _readBuf = new byte[64 * 1024];
    int _readLen;
    int _nextId = 1;
    // Minecraft sends at most 4096 characters per reply packet; anything close to that may have more after it.
    const int ChunkChars = 4000;

    public bool IsConnected => _tcp?.Connected == true && _stream != null;

    // Connects and logs in. Throws RconAuthException for a wrong password, other exceptions if it can't connect.
    public async Task ConnectAsync(string host, int port, string password, TimeSpan timeout, CancellationToken ct = default)
    {
        Dispose();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        _tcp = new TcpClient { NoDelay = true };
        try
        {
            await _tcp.ConnectAsync(host, port, cts.Token);
            _stream = _tcp.GetStream();
            _readLen = 0;
            int id = NextId();
            await SendAsync(new RconPacket(id, RconPacket.TypeAuth, password ?? ""), cts.Token);
            while (true)
            {
                var p = await ReadPacketAsync(cts.Token);
                if (p.Type == RconPacket.TypeResponse) continue; // Source servers send an empty value first; Minecraft doesn't
                if (p.Type != RconPacket.TypeCommand) continue;
                if (p.Id == -1) throw new RconAuthException("The RCON password was refused");
                if (p.Id == id) return;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Runs one command and returns the whole reply. Minecraft splits long replies into 4096-character packets.
    // A shorter packet is the last one. After a full-size packet we can't tell, so we send an empty packet of
    // another type: the server answers packets in order, so its reply ("Unknown request 0") marks the end.
    // Never send two packets at once: Minecraft reads one packet per read and drops the connection when a read
    // holds more than one.
    public async Task<string> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsConnected) throw new IOException("Not connected");
        await _lock.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            int id = NextId(), endId = 0;
            await SendAsync(new RconPacket(id, RconPacket.TypeCommand, command), cts.Token);
            var sb = new StringBuilder();
            while (true)
            {
                var p = await ReadPacketAsync(cts.Token);
                if (endId != 0 && p.Id == endId) break;
                if (p.Id != id) continue;
                sb.Append(p.Body);
                if (p.Body.Length < ChunkChars) { if (endId == 0) break; }
                else if (endId == 0)
                {
                    endId = NextId();
                    await SendAsync(new RconPacket(endId, RconPacket.TypeResponse, ""), cts.Token);
                }
            }
            return sb.ToString();
        }
        catch
        {
            Dispose(); // a half-read reply would confuse the next command
            throw;
        }
        finally { _lock.Release(); }
    }

    int NextId()
    {
        if (_nextId >= int.MaxValue - 2) _nextId = 1;
        return _nextId++;
    }

    async Task SendAsync(RconPacket p, CancellationToken ct)
    {
        var bytes = p.Encode();
        await _stream.WriteAsync(bytes, ct);
        await _stream.FlushAsync(ct);
    }

    async Task<RconPacket> ReadPacketAsync(CancellationToken ct)
    {
        while (true)
        {
            int used = RconPacket.TryDecode(_readBuf.AsSpan(0, _readLen), out var p);
            if (used > 0)
            {
                Buffer.BlockCopy(_readBuf, used, _readBuf, 0, _readLen - used);
                _readLen -= used;
                return p;
            }
            if (_readLen == _readBuf.Length) throw new InvalidDataException("RCON packet too big");
            int n = await _stream.ReadAsync(_readBuf.AsMemory(_readLen), ct);
            if (n == 0) throw new IOException("The server closed the RCON connection");
            _readLen += n;
        }
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
        _readLen = 0;
    }
}

public class RconAuthException : Exception
{
    public RconAuthException(string message) : base(message) { }
}
