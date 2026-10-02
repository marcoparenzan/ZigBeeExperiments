# Uso

## Requisiti

- Windows, .NET SDK 10 (`dotnet --list-sdks`).
- Dongle ZBDongle-E collegato. Controlla la porta COM in Gestione dispositivi (finora: `COM5`).

## Avvio

```
dotnet run --project src/SonoffZigbee.Demo -- COM5
dotnet run --project src/SonoffZigbee.Demo -- COM5 --verbose   # mostra anche i frame ASH e i callback grezzi
```

La porta di default è `COM5`. Ricorda che una sola applicazione alla volta può aprire la porta.

## Comandi della demo

| Comando | Effetto |
|---|---|
| `join [secondi]` | Apre la rete al pairing (default 60, massimo 254) |
| `list` | Dispositivi annunciati da quando è partita la demo |
| `on` / `off` / `toggle <addr> [ep]` | Cluster On/Off. `addr` è l'indirizzo breve in esadecimale, `ep` default 1 |
| `read <addr> <ep> <cluster> <attr>` | Legge un attributo (tutto in esadecimale). La risposta arriva come `[msg]` |
| `quit` | Esce |

## Pairing

1. Avvia la demo e scrivi `join 120`.
2. Metti il dispositivo in pairing, **vicino al dongle**.
3. Compaiono `[tc]` (join visto dal trust center) e `[join]` (annuncio del dispositivo).

Note per dispositivo:
- **Sensori Aqara:** reset di circa 3 secondi finché il LED lampeggia. Spesso servono più tentativi. Dormono quasi sempre: i dati arrivano come report, non su richiesta.
- **Prese e lampadine:** supportano `on`/`off`/`toggle`.
- **Dispositivi solo Wi-Fi, Matter o Bluetooth** (per esempio Shelly H&T Gen3 o Shelly BLU) non si abbinano a Zigbee.

## Rete e chiave

La configurazione (PAN id, canale 20, chiave di rete) sta in `network.json` accanto all'eseguibile (`src/SonoffZigbee.Demo/bin/Debug/net10.0/`). Viene creata al primo avvio e non va committata (è nel `.gitignore`).

Se perdi `network.json` la rete memorizzata nel dongle non corrisponde più: la libreria la abbandona e ne forma una nuova. I dispositivi già abbinati vanno ripetuti.

## Uso come libreria

```csharp
var cfg = ZigbeeNetworkConfig.LoadOrCreate("network.json");
using var zb = new ZigbeeCoordinator("COM5", cfg);
zb.DeviceAnnounced += a => Console.WriteLine($"{a.NetworkAddress:X4} {a.IeeeString}");
zb.MessageReceived += m => Console.WriteLine(Describe.Message(null, m));
await zb.StartAsync();
await zb.PermitJoinAsync(120);
await zb.ToggleAsync(0x1234, 1);
```

## Problemi comuni

| Sintomo | Causa probabile |
|---|---|
| Nessuna risposta dal dongle | DTR attivo (dongle nel bootloader), baud rate diverso da 115200, o firmware non EZSP |
| Porta occupata | Un'altra istanza della demo o un altro programma ha aperto la COM |
| `Delivery failed: EmberStatus 0x66` | Dispositivo a batteria che dorme: normale, riprova quando è sveglio |
| Il dispositivo non si abbina | Più vicino al dongle, ripeti il reset, riavvia con `--verbose` e guarda i log |
