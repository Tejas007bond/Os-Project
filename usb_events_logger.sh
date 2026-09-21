#!/bin/bash

LOG_FILE="/var/log/usb_events.log"

touch "$LOG_FILE"
chmod 600 "$LOG_FILE"

journalctl -k -f -o cat | while IFS= read -r line
do
    if echo "$line" | grep -q "USB_MON:"
    then
        echo "$(date '+%Y-%m-%d %H:%M:%S') $line" >> "$LOG_FILE"
    fi
done
