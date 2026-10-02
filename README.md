# ZigBeeExperiments

Coordinatore Zigbee in C# per **Windows**, senza Home Assistant né Zigbee2MQTT. Parla direttamente con il dongle **Sonoff ZBDongle-E** (Silicon Labs EFR32MG21, firmware EmberZNet NCP) via porta seriale.

## Stato

Sperimentale. Verificato sul dongle reale: avvio, formazione e ripristino della rete, pairing di due sensori Aqara, ricezione di un evento di vibrazione. Vedi [OPEN_POINTS.md](OPEN_POINTS.md) per quello che manca o non è provato.

## Hardware supportato

| | |
|---|---|
| Dongle | Sonoff ZBDongle-E (CH9102, USB VID 1A86 / PID 55D4) |
| Firmware | EmberZNet 6.10.3, EZSP v8, 115200 baud |
| Non supportato | ZBDongle-P (TI CC2652P, protocollo ZNP): un altro protocollo, non implementato |

## Avvio rapido

```
dotnet run --project src/SonoffZigbee.Demo -- COM5
> join 120
```

Metti poi il dispositivo in pairing. Dettagli in [USAGE.md](USAGE.md).

## Contenuto

- [src/SonoffZigbee/](src/SonoffZigbee/): la libreria (ASH, EZSP, coordinatore, ZCL).
- [src/SonoffZigbee.Demo/](src/SonoffZigbee.Demo/): app console di prova.
- [USAGE.md](USAGE.md): come usarla.
- [ARCHITECTURE.md](ARCHITECTURE.md): come è fatta.
- [CHANGELOG.md](CHANGELOG.md): cosa è cambiato.
- [OPEN_POINTS.md](OPEN_POINTS.md): cosa resta da fare o da verificare.

## Perché con altri strumenti non andava

Sul chip CH9102 il segnale DTR tiene il dongle nel **Gecko Bootloader**: molti programmi seriali lo attivano di default e il dongle non risponde mai come Zigbee. La libreria tiene DTR e RTS spenti.
