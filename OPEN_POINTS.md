# Punti aperti

## Da capire

- **Sensore porta/finestra Aqara muto dopo il pairing.** Si è abbinato (due volte, con indirizzi diversi: `0xF6A5`, `0xBF5E`), ha mandato modello e report batteria, poi nessun messaggio all'apertura/chiusura, nemmeno a livello di callback grezzi. Non è chiaro se sia il sensore (pairing incompleto, ripetuto join/leave) o qualcosa nello stack. Da riprovare con `--verbose`.
- **Interview dei dispositivi a batteria.** Le richieste ZDO subito dopo il join falliscono con `EmberStatus 0x66`. Il retry al messaggio successivo è implementato ma **non provato**.

## Da implementare

- **Bind e configure reporting.** Senza, i dispositivi che non riportano da soli (per esempio molti sensori di temperatura) non mandano dati.
- **Decodifica del report Aqara solo su un pacchetto di esempio.** Mai provata su dati reali del sensore: i byte reali non sono stati salvati. Le tag diverse da batteria e temperatura interna non sono interpretate.
- **Dispositivo che rientra con un nuovo indirizzo breve** (leave poi join): la demo tiene entrambi gli indirizzi, non li collega via IEEE.
- **Elenco dispositivi persistente:** `list` mostra solo quelli annunciati nella sessione corrente.
- **Misure di potenza** (cluster `0x0B04`, `0x0702`, o `0xEF00` Tuya) e divisori: non gestiti.

## Da verificare

- **Policy di sicurezza del trust center**: gli identificatori di decisione (`setPolicy`) e la maschera di `setInitialSecurityState` sono scritti a memoria. Il join funziona con i due Aqara, ma non con altri tipi (Zigbee 3.0 con install code, ecc.).
- **`setConcentrator`**: eventuale errore viene solo loggato.
- **Percentuale batteria**: stima lineare 2,85–3,00 V.
- **Compatibilità con altre versioni EZSP**: solo v8 (EmberZNet 6.10.3). Un aggiornamento del firmware cambierebbe i formati.

## Dispositivi

| Dispositivo | Stato |
|---|---|
| Aqara porta/finestra (MCCGQ11LM) | Si abbina, poi silenzioso (vedi sopra) |
| Aqara vibrazione (DJT11LM) | Si abbina, evento vibrazione ricevuto |
| Presa Tuya LSC con misuratore | Non provata (serve sapere se è Zigbee o Wi-Fi) |
| Shelly 1 Gen4 | Dall'utente risulta solo Matter: da non considerare Zigbee finché non confermato |
| Shelly H&T | Solo Matter (Wi-Fi): non utilizzabile con questo dongle |

## Qualità

- Nessun test automatico: la verifica è stata fatta solo col dongle reale.
- Nessuna gestione della disconnessione del dongle a runtime (USB scollegato).
- Nessun controllo di concorrenza tra `PermitJoinAsync` e comandi di invio oltre al blocco dei comandi EZSP.
