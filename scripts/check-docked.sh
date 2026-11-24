#!/bin/zsh
# Controle met een dock, hub of netwerkkabel aan de Mac: de ruwe waarden uit macOS en wat Fusebox ervan maakt.
# Alleen lezen, geen sudo, geen venster. Serienummers en MAC-adressen worden niet getoond.
# Gebruik: scripts/check-docked.sh [pad naar Fusebox-binary]   (standaard: de geïnstalleerde app)
BIN="${1:-/Applications/Fusebox.app/Contents/MacOS/Fusebox}"

echo "== USB: raw power keys (ioreg) =="
# Apparaten, hubs, poorten en controllers met de sleutels uit IOUSBHostFamilyDefinitions.h
/usr/sbin/ioreg -l -w0 | /usr/bin/grep -E '\+-o .*<class (IOUSBHostDevice|AppleUSB[A-Za-z0-9]*Hub|AppleUSB[A-Za-z0-9]*Port|Apple[A-Za-z0-9]*XHCI[A-Za-z0-9]*),|"(USB Product Name|bDeviceClass|UsbLinkSpeed|UsbPowerSinkAllocation|UsbPowerSinkCapability|kUSBBusCurrentAllocation|kUSBFailedRequestedPower|kUSBWakePortCurrentLimit|kUSBWakePowerSupply|UsbHostPortOvercurrent)" =' \
  | /usr/bin/sed -E 's/^[ |]*//; s/, id 0x[0-9a-f]+.*$/>/' | /usr/bin/awk '/^\+-o/ { head = $0; next } { if (head != "") { print head; head = "" } print "    " $0 }'

echo
echo "== USB: System Information =="
/usr/sbin/system_profiler SPUSBHostDataType 2>/dev/null | /usr/bin/grep -v -i "serial" || echo "(nothing)"

echo
echo "== Network adapters: media (ifconfig -m, without addresses) =="
for i in $(/sbin/ifconfig -l); do
  case $i in en*) /sbin/ifconfig -m "$i" | /usr/bin/grep -E "^$i|media|status" ;; esac
done

echo
if [ -x "$BIN" ]; then
  "$BIN" --check-docked
else
  echo "Fusebox binary not found at $BIN (pass the path as the first argument)."
fi
