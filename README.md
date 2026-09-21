# USB Kernel Module Security Monitor

This implementation follows the project design: a Linux Kernel Module detects
USB add/remove events, logs VID/PID and timestamps, a user-space logger stores
events, and a guard script compares ADD events against a whitelist.

## Files

- usb_logger.c - kernel module
- Makefile - kernel module build file
- usb_events_logger.sh - kernel-log capture script
- usb_guard.sh - whitelist/alert monitor
- usb_whitelist.conf.example - whitelist template
- usb-guard.service - systemd service template

## Environment

The report specifies Ubuntu 20.04+ in VirtualBox, with gcc, make, kernel
headers, and root/admin privileges.

## Build

sudo apt update
sudo apt install build-essential linux-headers-$(uname -r)

make
sudo insmod usb_logger.ko

Check:
dmesg | tail
journalctl -k | grep USB_MON

Unload:
sudo rmmod usb_logger

## Logging

sudo mkdir -p /var/log
sudo touch /var/log/usb_events.log /var/log/usb_alerts.log
sudo chmod 600 /var/log/usb_events.log /var/log/usb_alerts.log

Run:
sudo bash usb_events_logger.sh

Keep that terminal running.

## Whitelist

sudo cp usb_whitelist.conf.example /etc/usb_whitelist.conf
sudo chmod 600 /etc/usb_whitelist.conf

Add approved VID:PID pairs, one per line.

## Guard

sudo cp usb_guard.sh /usr/local/bin/usb_guard.sh
sudo chmod 700 /usr/local/bin/usb_guard.sh

Run:
sudo /usr/local/bin/usb_guard.sh

## Optional systemd setup

sudo cp usb-guard.service /etc/systemd/system/usb-guard.service
sudo systemctl daemon-reload
sudo systemctl enable --now usb-guard.service
sudo systemctl status usb-guard.service

For a classroom demo, keep automatic unbinding/blocking disabled. This version
only records an alert for an unauthorized device.
