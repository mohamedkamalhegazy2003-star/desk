/*
 * duoseat_shared.h
 *
 * Contract between the DuoSeatFilter kernel driver and user-mode code.
 * Included by the driver (after <ntddk.h>) and may be included by C/C++ user-mode tools
 * (it pulls in <windows.h>/<winioctl.h> itself in that case).
 *
 * The C# service mirrors these structures by hand (service/DuoSeat.Service/DriverClient.cs):
 * if you change anything here, change it there too and bump DUOSEAT_PROTOCOL_VERSION.
 */
#ifndef DUOSEAT_SHARED_H
#define DUOSEAT_SHARED_H

#if !defined(_NTDDK_) && !defined(_WDMDDK_)
#include <windows.h>
#include <winioctl.h>
#endif

#define DUOSEAT_PROTOCOL_VERSION   1

#define DUOSEAT_CONTROL_NT_NAME    L"\\Device\\DuoSeatCtl"
#define DUOSEAT_CONTROL_DOS_NAME   L"\\DosDevices\\DuoSeatCtl"
#define DUOSEAT_CONTROL_USER_PATH  L"\\\\.\\DuoSeatCtl"

#define DUOSEAT_DEVICE_TYPE        0x8000

#define IOCTL_DUOSEAT_GET_VERSION  CTL_CODE(DUOSEAT_DEVICE_TYPE, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS) /* out: ULONG            */
#define IOCTL_DUOSEAT_GET_DEVICES  CTL_CODE(DUOSEAT_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS) /* out: DUOSEAT_DEVICE_INFO[] */
#define IOCTL_DUOSEAT_SET_STATION  CTL_CODE(DUOSEAT_DEVICE_TYPE, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS) /* in : DUOSEAT_SET_STATION */
#define IOCTL_DUOSEAT_SET_ROUTING  CTL_CODE(DUOSEAT_DEVICE_TYPE, 0x803, METHOD_BUFFERED, FILE_ANY_ACCESS) /* in : ULONG (0/1)      */
#define IOCTL_DUOSEAT_READ_EVENTS  CTL_CODE(DUOSEAT_DEVICE_TYPE, 0x804, METHOD_BUFFERED, FILE_ANY_ACCESS) /* out: DUOSEAT_EVENT[] (pends until data) */

/* Device kinds */
#define DUOSEAT_TYPE_UNKNOWN       0
#define DUOSEAT_TYPE_KEYBOARD      1
#define DUOSEAT_TYPE_MOUSE         2

/*
 * Station of a device:
 *   1 = pass-through (default). Windows receives the input normally.
 *   2 = captured. Input is NOT delivered to Windows; it is queued for the service instead.
 *   3 = monitor. Input is copied to the service AND delivered to Windows (used to identify devices).
 * Nothing is captured or copied unless routing is enabled (IOCTL_DUOSEAT_SET_ROUTING) and a
 * control handle is open. Closing the last control handle disables routing automatically.
 */
#define DUOSEAT_STATION_1          1
#define DUOSEAT_STATION_2          2
#define DUOSEAT_STATION_MONITOR    3

#define DUOSEAT_MAX_DEVICES        32
#define DUOSEAT_MAX_ID_CHARS       128

typedef struct _DUOSEAT_DEVICE_INFO {
    ULONG DeviceId;                              /* driver-assigned, valid until the device is removed */
    ULONG Type;                                  /* DUOSEAT_TYPE_*   */
    ULONG Station;                               /* DUOSEAT_STATION_* */
    ULONG Reserved;
    WCHAR InstanceId[DUOSEAT_MAX_ID_CHARS];      /* PnP device instance id (stable across reboots) */
} DUOSEAT_DEVICE_INFO, *PDUOSEAT_DEVICE_INFO;    /* 272 bytes */

typedef struct _DUOSEAT_SET_STATION {
    ULONG DeviceId;
    ULONG Station;
} DUOSEAT_SET_STATION, *PDUOSEAT_SET_STATION;    /* 8 bytes */

typedef struct _DUOSEAT_EVENT {
    ULONG  DeviceId;
    ULONG  Type;            /* DUOSEAT_TYPE_KEYBOARD or DUOSEAT_TYPE_MOUSE */
    /* keyboard (KEYBOARD_INPUT_DATA): */
    USHORT MakeCode;
    USHORT KeyFlags;        /* KEY_BREAK=1, KEY_E0=2, KEY_E1=4 */
    /* mouse (MOUSE_INPUT_DATA): */
    USHORT MouseFlags;      /* MOUSE_MOVE_ABSOLUTE=1, ... */
    USHORT ButtonFlags;     /* MOUSE_LEFT_BUTTON_DOWN=1, _UP=2, RIGHT 4/8, MIDDLE 0x10/0x20, WHEEL 0x400, HWHEEL 0x800 */
    USHORT ButtonData;      /* wheel delta (signed 16-bit) */
    USHORT Reserved;
    LONG   LastX;
    LONG   LastY;
} DUOSEAT_EVENT, *PDUOSEAT_EVENT;                /* 28 bytes */

C_ASSERT(sizeof(DUOSEAT_DEVICE_INFO) == 272);
C_ASSERT(sizeof(DUOSEAT_SET_STATION) == 8);
C_ASSERT(sizeof(DUOSEAT_EVENT) == 28);

#endif /* DUOSEAT_SHARED_H */
