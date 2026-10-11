#pragma once

#include <ntddk.h>
#include <kbdmou.h>
#include <ntddkbd.h>
#include <ntddmou.h>
#include <ntstrsafe.h>
#include <wdf.h>

#include "duoseat_shared.h"

#define DUOSEAT_RING_SIZE 2048

typedef struct _DEVICE_EXTENSION
{
    CONNECT_DATA  UpperConnectData;                    // class driver's callback, saved at CONNECT time
    WDFDEVICE     Device;
    ULONG         DeviceId;
    volatile LONG Type;                                // DUOSEAT_TYPE_*
    volatile LONG Station;                             // DUOSEAT_STATION_*
    LONG          Slot;                                // index in g_Devices, -1 if none
    WCHAR         InstanceId[DUOSEAT_MAX_ID_CHARS];
} DEVICE_EXTENSION, *PDEVICE_EXTENSION;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_EXTENSION, FilterGetData)

typedef struct _DUOSEAT_GLOBALS
{
    WDFSPINLOCK       Lock;                            // protects Devices[], Ring, Head, Count, Dropped
    WDFQUEUE          PendingReads;                    // manual queue: parked IOCTL_DUOSEAT_READ_EVENTS
    PDEVICE_EXTENSION Devices[DUOSEAT_MAX_DEVICES];
    DUOSEAT_EVENT     Ring[DUOSEAT_RING_SIZE];
    ULONG             Head;
    ULONG             Count;
    ULONG             Dropped;
    volatile LONG     Routing;                         // 0 = everything passes through
    volatile LONG     OpenHandles;
    volatile LONG     NextId;
} DUOSEAT_GLOBALS;
