#!/bin/bash

EVENT_LOG="/var/log/usb_events.log"
ALERT_LOG="/var/log/usb_alerts.log"
WHITELIST="/etc/usb_whitelist.conf"

touch "$ALERT_LOG"
chmod 600 "$ALERT_LOG"

tail -F "$EVENT_LOG" | while IFS= read -r line
do
    if echo "$line" | grep -q "USB_MON: ADD"
    then
        VID=$(echo "$line" | sed -n 's/.*VID=\([0-9a-fA-F]*\).*/\1/p')
        PID=$(echo "$line" | sed -n 's/.*PID=\([0-9a-fA-F]*\).*/\1/p')

        if ! grep -Eiq "^[[:space:]]*${VID}:${PID}[[:space:]]*$" "$WHITELIST" 2>/dev/null
        then
            echo "$(date '+%Y-%m-%d %H:%M:%S') UNAUTHORIZED USB VID=$VID PID=$PID" >> "$ALERT_LOG"
            logger -p auth.warning "USB_MON: unauthorized device VID=$VID PID=$PID"
        fi
    fi
done
