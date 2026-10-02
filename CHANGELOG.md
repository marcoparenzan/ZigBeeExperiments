# Changelog

## 2026-10-02

### Aggiunto

- Libreria `SonoffZigbee` e demo console `SonoffZigbee.Demo`, soluzione `SonoffZigbee.slnx`.
- Livello **ASH** e **EZSP** per ZBDongle-E (EmberZNet 6.10.3, EZSP v8): avvio, formazione e ripristino della rete, permit join, interview ZDO, invio unicast/broadcast.
- Cluster On/Off (`on`/`off`/`toggle`), lettura attributi, parsing report ZCL.
- Decodifica del report proprietario Aqara `0xFF01` (batteria in mV, % stimata, temperatura interna).
- Descrizioni leggibili per i messaggi (porta aperta/chiusa, vibrazione/inclinazione/caduta, temperatura, umidità).
- Interview dei dispositivi a batteria rimandata al loro messaggio successivo (massimo 3 tentativi).
- Log di ogni callback EZSP grezzo (visibile con `--verbose`).
- `.gitignore` (bin, obj, `network.json`).

### Documentazione

- Aggiunti `README.md`, `USAGE.md`, `ARCHITECTURE.md`, `CHANGELOG.md`, `OPEN_POINTS.md`.
- Tutto il codice commentato in inglese (commenti XML sulle API pubbliche e spiegazioni nei punti non ovvi). Nessuna modifica al comportamento. I testi che il programma stampa a video restano in italiano.

### Cambiato

- Cartella del progetto rinominata da `SonoffZigBeeExperiments` a `ZigBeeExperiments`. Anche la soluzione è stata rinominata, da `SonoffZigbee.slnx` a `ZigbeeExperiments.slnx`. I nomi dei progetti (`SonoffZigbee`, `SonoffZigbee.Demo`) non sono cambiati.
- Il progetto era partito per il protocollo ZNP (ZBDongle-P). Si è scoperto che il dongle è un **ZBDongle-E** (risposta del Gecko Bootloader con DTR attivo): livello ZNP eliminato e sostituito con ASH/EZSP.

### Rimosso

- Due valori di configurazione dello stack che il dongle rifiutava: `APS_UNICAST_MESSAGE_COUNT` (0x0B, stato 0x37) e `SOURCE_ROUTE_TABLE_SIZE` (0x1E, stato 0x36). Nessun effetto sull'avvio.

### Verificato sull'hardware reale

- Reset ASH e `version` (EZSP v8, stack 6.10.3).
- Formazione della rete (PAN 0x1A62, canale 20) e ripristino al riavvio successivo.
- Pairing di un sensore porta/finestra Aqara (`lumi.sensor_magnet.aq2`) e di un sensore di vibrazione (`lumi.vibration.aq1`); ricezione di un evento di vibrazione.
