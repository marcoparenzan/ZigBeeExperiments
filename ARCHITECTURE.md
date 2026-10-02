# Architettura

Il dongle esegue tutto lo stack Zigbee (radio, rete, sicurezza). Il PC lo comanda con un protocollo seriale a livelli.

```
Programma (demo / tuo server)
        │  eventi e metodi async
ZigbeeCoordinator        formazione rete, ZDO, messaggi APS, policy
        │  comandi e callback EZSP
EzspClient               frame EZSP, versione, abbinamento comando/risposta per seq
        │  frame dati
AshPort                  framing seriale affidabile: CRC, randomizzazione, ack, ritrasmissioni
        │
SerialPort (COM5, 115200 8N1, DTR/RTS spenti)  ──  ZBDongle-E (EmberZNet 6.10.3)
```

## File

| File | Ruolo |
|---|---|
| `Ezsp/AshPort.cs` | ASH: delimitatore `0x7E`, byte stuffing, CRC-16 CCITT, randomizzazione dati (LFSR seme 0x42), numeri di sequenza e ack, reset (RST/RSTACK) |
| `Ezsp/EzspClient.cs` | EZSP: negozia la versione (primo `version` in formato legacy, poi esteso), serializza i comandi, separa risposte e callback |
| `ZigbeeCoordinator.cs` | API pubblica: `StartAsync`, `PermitJoinAsync`, `SendAsync`, `OnAsync`/`OffAsync`/`ToggleAsync`, `ReadAttributeAsync`, interview ZDO. Eventi: `DeviceAnnounced`, `DeviceJoined`, `MessageReceived` |
| `Zcl.cs` | Costruzione e parsing frame ZCL e attributi tipizzati |
| `Describe.cs` | Decodifica report Aqara `0xFF01` e testo leggibile per i messaggi |
| `ZigbeeModels.cs` | Configurazione di rete persistente e record dei dati |
| `SonoffZigbee.Demo/Program.cs` | App console con i comandi |

## Flusso di avvio (`StartAsync`)

1. Apre la porta, reset ASH, `version` → EZSP v8.
2. Legge l'EUI64 del dongle.
3. Imposta configurazione dello stack e policy di sicurezza (devono precedere l'avvio della rete).
4. Registra l'endpoint 1 (profilo HA).
5. `networkInit`: se il dongle ha già una rete, la confronta con `network.json`. Se coincide la riusa; altrimenti la abbandona e forma una nuova. Se non c'è rete, la forma (stato di sicurezza con chiave well-known `ZigBeeAlliance09` e chiave di rete da config, poi `formNetwork`).
6. Attende `stackStatus = NETWORK_UP (0x90)`, poi abilita il routing many-to-one.

## Scelte di progetto

- **Un comando alla volta** su EZSP; le risposte si abbinano per numero di sequenza.
- **Callback asincroni** (`stackStatusHandler`, `incomingMessageHandler`, `trustCenterJoinHandler`, `messageSentHandler`) arrivano sul thread di lettura seriale: i gestori non devono bloccare. Le attese interne usano `Expect(...)`, registrate **prima** di inviare il comando che le provoca.
- **Messaggi ZDO** (profilo 0) restano interni, salvo l'annuncio di dispositivo; gli altri diventano `MessageReceived`.
- **Solo EZSP v8**: i formati dei frame cambiano nelle versioni successive. Con altre versioni avvisa e potrebbe non funzionare.
