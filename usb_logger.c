#include <linux/module.h>
#include <linux/kernel.h>
#include <linux/usb.h>
#include <linux/ktime.h>
#include <linux/timekeeping.h>

static int usb_event_notify(struct notifier_block *nb,
                            unsigned long action, void *data)
{
    struct usb_device *udev = data;
    struct timespec64 ts;

    if (!udev)
        return NOTIFY_OK;

    ktime_get_real_ts64(&ts);

    if (action == USB_DEVICE_ADD) {
        pr_info("USB_MON: ADD VID=%04x PID=%04x TIME=%lld\n",
                le16_to_cpu(udev->descriptor.idVendor),
                le16_to_cpu(udev->descriptor.idProduct),
                (long long)ts.tv_sec);
    } else if (action == USB_DEVICE_REMOVE) {
        pr_info("USB_MON: REMOVE VID=%04x PID=%04x TIME=%lld\n",
                le16_to_cpu(udev->descriptor.idVendor),
                le16_to_cpu(udev->descriptor.idProduct),
                (long long)ts.tv_sec);
    }

    return NOTIFY_OK;
}

static struct notifier_block usb_nb = {
    .notifier_call = usb_event_notify,
};

static int __init usb_logger_init(void)
{
    int ret = usb_register_notify(&usb_nb);

    if (ret) {
        pr_err("USB_MON: notifier registration failed\n");
        return ret;
    }

    pr_info("USB_MON: module loaded\n");
    return 0;
}

static void __exit usb_logger_exit(void)
{
    usb_unregister_notify(&usb_nb);
    pr_info("USB_MON: module unloaded\n");
}

module_init(usb_logger_init);
module_exit(usb_logger_exit);

MODULE_LICENSE("GPL");
MODULE_AUTHOR("OS Project Team");
MODULE_DESCRIPTION("USB event logger using Linux USB notifier");
