using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ServerList;

public record ServerQueryResult(bool IsOnline, string Map, int Players, int MaxPlayers, string? Error = null, bool Retryable = true);

public static class A2SQuery
{
    private static readonly byte[] InfoRequest =
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0x54,
        .. Encoding.ASCII.GetBytes("Source Engine Query\0")
    ];

    public static async Task<ServerQueryResult> QueryAsync(string address, CancellationToken ct, int timeoutMs = 3000)
    {
        var first = await QueryOnceAsync(address, ct, timeoutMs);
        if (first.IsOnline || !first.Retryable || ct.IsCancellationRequested)
            return first;

        // Retry once on transient UDP packet loss or timeout
        return await QueryOnceAsync(address, ct, timeoutMs);
    }

    private static async Task<ServerQueryResult> QueryOnceAsync(string address, CancellationToken ct, int timeoutMs)
    {
        try
        {
            var parts = address.Trim().Split(':');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
                return new ServerQueryResult(false, "", 0, 0, "Invalid address format", Retryable: false);

            if (!IPAddress.TryParse(parts[0], out var ip))
                return new ServerQueryResult(false, "", 0, 0, "Host must be an IP address", Retryable: false);

            if (!int.TryParse(parts[1], out int port) || port is < 1 or > 65535)
                return new ServerQueryResult(false, "", 0, 0, "Invalid port", Retryable: false);

            using var udp = new UdpClient(ip.AddressFamily);
            udp.Connect(ip, port);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            var token = timeoutCts.Token;

            await udp.SendAsync(InfoRequest, token);
            var response = (await udp.ReceiveAsync(token)).Buffer;

            // Handle CS2 A2S Challenge (0x41) up to 3 rounds with per-packet timeout
            int challengeAttempts = 0;
            while (response.Length >= 9 && response[4] == 0x41 && challengeAttempts++ < 3)
            {
                timeoutCts.CancelAfter(timeoutMs);
                byte[] challengeRequest = [.. InfoRequest, response[5], response[6], response[7], response[8]];
                await udp.SendAsync(challengeRequest, token);
                response = (await udp.ReceiveAsync(token)).Buffer;
            }

            if (response.Length >= 5 && response[4] == 0x41)
                return new ServerQueryResult(false, "", 0, 0, "Challenge not accepted", Retryable: true);

            // Detect multi-packet split response (0xFFFFFFFE)
            if (response.Length >= 4 && response[0] == 0xFE && response[1] == 0xFF && response[2] == 0xFF && response[3] == 0xFF)
                return new ServerQueryResult(false, "", 0, 0, "Split A2S response, unsupported", Retryable: false);

            // Parse A2S_INFO Response (0x49)
            if (response.Length < 7 || response[4] != 0x49)
                return new ServerQueryResult(false, "", 0, 0, "Invalid A2S_INFO response", Retryable: true);

            int offset = 6; // Skip 4x 0xFF, 0x49 header, and 1-byte protocol
            if (!TryReadString(response, ref offset, out _) ||         // Server Name
                !TryReadString(response, ref offset, out string map) || // Map
                !TryReadString(response, ref offset, out _) ||         // Folder
                !TryReadString(response, ref offset, out _))           // Game
            {
                return new ServerQueryResult(false, "", 0, 0, "Malformed A2S_INFO payload", Retryable: false);
            }

            offset += 2; // Skip 2-byte Steam App ID

            if (offset + 3 > response.Length)
                return new ServerQueryResult(false, "", 0, 0, "Truncated A2S_INFO payload", Retryable: true);

            int totalPlayers = response[offset++];
            int maxPlayers = response[offset++];
            int bots = response[offset++];

            int humanPlayers = Math.Max(0, totalPlayers - bots);
            return new ServerQueryResult(true, map, humanPlayers, maxPlayers);
        }
        catch (OperationCanceledException)
        {
            return new ServerQueryResult(false, "", 0, 0, "Timeout or cancelled", Retryable: true);
        }
        catch (SocketException ex)
        {
            bool retryable = ex.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.ConnectionRefused);
            return new ServerQueryResult(false, "", 0, 0, ex.Message, Retryable: retryable);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return new ServerQueryResult(false, "", 0, 0, ex.Message, Retryable: false);
        }
        catch (Exception ex)
        {
            return new ServerQueryResult(false, "", 0, 0, ex.Message, Retryable: true);
        }
    }

    private static bool TryReadString(byte[] buffer, ref int offset, out string value)
    {
        value = "";
        if (offset < 0 || offset >= buffer.Length)
            return false;

        int start = offset;
        while (offset < buffer.Length && buffer[offset] != 0x00)
            offset++;

        if (offset >= buffer.Length)
            return false; // Unterminated string

        value = Encoding.UTF8.GetString(buffer, start, offset - start);
        offset++; // Skip null terminator
        return true;
    }
}