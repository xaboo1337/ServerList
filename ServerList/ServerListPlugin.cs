using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Plugins;

namespace ServerList;

public sealed class ServerEntry
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
}

public sealed class ServerListConfig
{
    public string CurrentServerAddress { get; set; } = string.Empty;
    public int RefreshIntervalSeconds { get; set; } = 15;
    public int CommandCooldownSeconds { get; set; } = 3;
    public List<ServerEntry> Servers { get; set; } = new();
}

public sealed record ServerStatus(
    bool IsOnline,
    int Players,
    int MaxPlayers,
    string Map
);

[PluginMetadata(Id = "ServerList", Version = "1.0.0", Name = "ServerList", Author = "- xaboo.")]
public sealed class ServerList(ISwiftlyCore core) : BasePlugin(core)
{
    private readonly ConcurrentDictionary<string, ServerStatus> _statusCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, DateTime> _cooldowns = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    private ServerListConfig _config = new();
    private CancellationTokenSource? _cts;

    public override void Load(bool hotReload)
    {
        Core.Configuration.InitializeJsonWithModel<ServerListConfig>("config.json", "ServerList");
        Core.Configuration.Configure(builder =>
        {
            builder.AddJsonFile("config.json", optional: false, reloadOnChange: true);
        });

        ReloadConfig();

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public override void Unload()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        _statusCache.Clear();
        _cooldowns.Clear();
    }

    private void ReloadConfig()
    {
        var freshConfig = new ServerListConfig();
        Core.Configuration.Manager.GetSection("ServerList").Bind(freshConfig);
        _config = freshConfig;
    }

    [Command("serv")]
    public void OnServCommand(ICommandContext context)
    {
        if (!context.IsSentByPlayer || context.Sender is not { } player)
            return;

        var now = DateTime.UtcNow;
        var cooldownSeconds = Math.Max(0, _config.CommandCooldownSeconds);

        if (cooldownSeconds > 0 && _cooldowns.TryGetValue(player.SteamID, out var lastUsed))
        {
            var elapsed = (now - lastUsed).TotalSeconds;
            if (elapsed < cooldownSeconds)
            {
                var remaining = Math.Ceiling(cooldownSeconds - elapsed);
                var cdLoc = Core.Translation.GetPlayerLocalizer(player);
                player.SendChat(cdLoc["serverlist.cooldown", remaining]);
                return;
            }
        }

        _cooldowns[player.SteamID] = now;

        var localizer = Core.Translation.GetPlayerLocalizer(player);
        var currentAddr = _config.CurrentServerAddress.Trim();

        player.SendChat(localizer["serverlist.header"]);

        foreach (var server in _config.Servers)
        {
            var addr = server.Address.Trim();
            if (string.IsNullOrEmpty(addr))
                continue;

            var isCurrent = !string.IsNullOrEmpty(currentAddr) &&
                            string.Equals(addr, currentAddr, StringComparison.OrdinalIgnoreCase);

            var currentTag = isCurrent ? $" {localizer["serverlist.current_tag"]}" : string.Empty;

            if (_statusCache.TryGetValue(addr, out var status) && status.IsOnline)
            {
                player.SendChat(localizer[
                    "serverlist.entry_online",
                    server.Name,
                    status.Players,
                    status.MaxPlayers,
                    status.Map,
                    currentTag
                ]);
            }
            else
            {
                player.SendChat(localizer[
                    "serverlist.entry_offline",
                    server.Name,
                    currentTag
                ]);
            }

            player.SendChat(localizer["serverlist.entry_ip", addr]);
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await RefreshAllServersAsync(token);

            var delaySeconds = Math.Max(5, _config.RefreshIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAllServersAsync(CancellationToken token)
    {
        if (!await _pollLock.WaitAsync(0, token))
            return;

        try
        {
            ReloadConfig();

            var tasks = _config.Servers
                .Select(s => s.Address.Trim())
                .Where(addr => !string.IsNullOrEmpty(addr))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(async addr =>
                {
                    var status = await QueryA2SInfoAsync(addr, token);
                    _statusCache[addr] = status;
                });

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Plugin unloading
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "Error while polling A2S server statuses.");
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private static async Task<ServerStatus> QueryA2SInfoAsync(string address, CancellationToken token)
    {
        try
        {
            var parts = address.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
                return new ServerStatus(false, 0, 0, "unknown");

            var addresses = await Dns.GetHostAddressesAsync(parts[0], token);
            var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip is null)
                return new ServerStatus(false, 0, 0, "unknown");

            var endpoint = new IPEndPoint(ip, port);

            using var udp = new UdpClient();
            udp.Client.SendTimeout = 2000;
            udp.Client.ReceiveTimeout = 2000;

            // Standard A2S_INFO payload: 0xFFFFFFFF + 'T' + "Source Engine Query\0"
            byte[] header = [0xFF, 0xFF, 0xFF, 0xFF, 0x54];
            byte[] queryText = "Source Engine Query\0"u8.ToArray();
            byte[] request = [.. header, .. queryText];

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            await udp.SendAsync(request, endpoint, timeoutCts.Token);
            var result = await udp.ReceiveAsync(timeoutCts.Token);
            var buffer = result.Buffer;

            // Handle Steam A2S challenge response (0x41 = 'A')
            if (buffer.Length >= 9 && buffer[4] == 0x41)
            {
                byte[] challenge = buffer[5..9];
                byte[] challengeRequest = [.. request, .. challenge];

                await udp.SendAsync(challengeRequest, endpoint, timeoutCts.Token);
                result = await udp.ReceiveAsync(timeoutCts.Token);
                buffer = result.Buffer;
            }

            // Valid A2S_INFO response starts with 0xFFFFFFFF + 'I' (0x49)
            if (buffer.Length < 10 || buffer[4] != 0x49)
                return new ServerStatus(false, 0, 0, "unknown");

            int offset = 6; // Skip 4-byte header, 'I' (0x49), and Protocol byte
            _ = ReadNullTerminatedString(buffer, ref offset); // Server Name
            string map = ReadNullTerminatedString(buffer, ref offset); // Map Name
            _ = ReadNullTerminatedString(buffer, ref offset); // Folder
            _ = ReadNullTerminatedString(buffer, ref offset); // Game

            offset += 2; // Skip Steam AppID (short)

            if (offset + 3 > buffer.Length)
                return new ServerStatus(false, 0, 0, map);

            int players = buffer[offset];
            int maxPlayers = buffer[offset + 1];
            int bots = buffer[offset + 2];
            int humanPlayers = Math.Max(0, players - bots);

            return new ServerStatus(true, humanPlayers, maxPlayers, map);
        }
        catch
        {
            return new ServerStatus(false, 0, 0, "unknown");
        }
    }

    private static string ReadNullTerminatedString(byte[] buffer, ref int offset)
    {
        int start = offset;
        while (offset < buffer.Length && buffer[offset] != 0x00)
        {
            offset++;
        }

        string value = Encoding.UTF8.GetString(buffer, start, offset - start);
        if (offset < buffer.Length)
            offset++; // Skip null terminator

        return value;
    }
}