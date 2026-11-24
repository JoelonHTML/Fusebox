# Translation glossary: Fusebox for Windows

Same house style as the Mac glossary (`docs/translation-glossary.md`): nl "je", de "du", fr "vous", es "tú". Terms follow the localized Windows 11 UI. Windows-only wording lives in `windows/Strings/<lang>.strings` and wins over the Mac strings with the same English key.

Check: from the `windows` folder run `python3 tools/strings-check.py` (CI runs it as "Translations complete"). It exits 1 if a used English key has no translation in a language, or a placeholder (%@, %lld, %.1f, %1$@) does not match.

| English | nl | de | fr | es | Note |
|---|---|---|---|---|---|
| Settings | Instellingen | Einstellungen | Paramètres | Configuración | Mac used Réglages / Ajustes |
| Task Manager | Taakbeheer | Task-Manager | Gestionnaire des tâches | Administrador de tareas | |
| system tray | systeemvak | Infobereich | zone de notification | área de notificación | |
| Startup apps | Opstart-apps | Autostart-Apps | Applications de démarrage | Aplicaciones de arranque | |
| Startup & storage | Opstart en opslag | Autostart & Speicher | Démarrage et stockage | Arranque y almacenamiento | |
| Efficiency mode | Efficiëntiemodus | Effizienzmodus | mode efficacité | modo de eficiencia | |
| Energy saver | Energiebesparing | Energiesparmodus | économiseur d'énergie | Ahorro de energía | |
| Focus assist | Focusassistent | Benachrichtigungsassistent | assistant de concentration | asistente de concentración | |
| Location | Locatie | Standort | Localisation | Ubicación | Mac fr used Emplacement |
| Notifications | Meldingen | Benachrichtigungen | Notifications | Notificaciones | Mac de used Mitteilungen |
| Windows Update | Windows Update | Windows Update | Windows Update | Windows Update | |
| PC | pc | PC | PC | PC | nl lowercase |
| Battery | Batterij | Batterie | Batterie | Batería | nl not "accu" |
| Wi-Fi | wifi | WLAN | Wi-Fi | Wi-Fi | |
| Drive (letter) | Station | Laufwerk | Lecteur | Unidad | C:, D: |
| Disk | Schijf | Datenträger | Disque | Disco | |
| Blue screen | Blauw scherm | Bluescreen | Écran bleu | Pantalla azul | |
| Driver | stuurprogramma | Treiber | pilote | controlador | |
| Commit (memory) | Vastgelegd | Zugesichert | Dédiée | Confirmada | Task Manager / Resource Monitor wording |
| Reliability index | stabiliteitsindex | Stabilitätsindex | indice de fiabilité | índice de confiabilidad | |
| Restart | Opnieuw opstarten | Neu starten | Redémarrer | Reiniciar | Start menu power button |
| Explorer | Verkenner | Explorer | Explorateur | Explorador | |
| Setup | Instelhulp | Einrichtung | Configuration | Configuración inicial | Fusebox's own onboarding |
| Graph | grafiek | Diagramm | graphique | gráfica | as in the Mac glossary |
| Low impact | Zuinig | Sparsam | Faible impact | Bajo impacto | as in the Mac glossary |
