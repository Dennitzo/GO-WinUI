using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runtime;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GoAi.Server.Core.Status;

public sealed class ReadinessService
{
    private readonly GoAiServerOptions _options;
    private readonly LmStudioClient _lmStudio;
    private readonly ServerRuntimeState _runtime;

    public ReadinessService(
        IOptions<GoAiServerOptions> options,
        LmStudioClient lmStudio,
        ServerRuntimeState runtime)
    {
        _options = options.Value;
        _lmStudio = lmStudio;
        _runtime = runtime;
    }

    public async Task<HealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var modelStatus = await _lmStudio.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return await GetSnapshotAsync(modelStatus, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HealthSnapshot> GetSnapshotAsync(
        ModelStatusSnapshot modelStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStatus);
        if (!IPAddress.TryParse(_options.ExpectedLanIp, out var expectedIp)
            || expectedIp.AddressFamily != AddressFamily.InterNetwork)
        {
            return NotReady(
                "Die konfigurierte LAN-IP ist ungültig.",
                "ExpectedLanIp in der Serverkonfiguration korrigieren.");
        }

        if (!GetActiveIpv4Addresses().Contains(expectedIp))
        {
            var current = string.Join(", ", GetActiveIpv4Addresses().Select(static address => address.ToString()));
            return NotReady(
                $"DHCP-IP geändert: erwartet {_options.ExpectedLanIp}, aktiv {current}.",
                "Routerreservierung setzen oder ExpectedLanIp, Caddy-Zertifikat und Client-Verbindungspaket neu erzeugen.");
        }

        if (!modelStatus.ProviderReachable)
        {
            return NotReady(
                $"LM Studio ist über {_options.LmStudioUri} nicht erreichbar.",
                "LM Studio starten und 'Serve on Local Network' auf Port 1234 aktivieren.");
        }

        if (_options.RequireLmStudioAuthentication
            && !await _lmStudio.HasConfiguredTokenAsync(cancellationToken).ConfigureAwait(false))
        {
            return NotReady(
                "LM-Studio-Authentifizierung ist noch nicht eingerichtet.",
                "In LM Studio 'Require Authentication' aktivieren und den Token in der Serverkonsole geschützt speichern.");
        }

        var requiredModelIds = new HashSet<string>(
            [_options.GeneralModelId, _options.CodeModelId],
            StringComparer.OrdinalIgnoreCase);
        var missingRequired = modelStatus.Models
            .Where(model => requiredModelIds.Contains(model.Id))
            .Where(static model => !model.Downloaded)
            .Select(static model => model.Id)
            .ToArray();
        if (missingRequired.Length > 0)
        {
            return NotReady(
                "Erforderliche Modelle fehlen: " + string.Join(", ", missingRequired),
                "Die Modelle in LM Studio herunterladen oder die Modell-IDs korrigieren.");
        }

        var ready = new HealthSnapshot("ready", GoAiProtocol.Version, DateTimeOffset.UtcNow);
        _runtime.SetGatewayState("Bereit", "Gateway, Netzwerk und LM Studio sind bereit.");
        return ready;
    }

    public static IReadOnlyList<IPAddress> GetActiveIpv4Addresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up
                || network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address.Address))
                {
                    addresses.Add(address.Address);
                }
            }
        }

        return addresses.Distinct().ToArray();
    }

    private HealthSnapshot NotReady(string reason, string repair)
    {
        _runtime.SetGatewayState("Nicht bereit", reason);
        return new HealthSnapshot("notReady", GoAiProtocol.Version, DateTimeOffset.UtcNow, reason, repair);
    }
}
