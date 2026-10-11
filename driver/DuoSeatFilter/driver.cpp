/*
 * DuoSeatFilter - KMDF upper filter driver for keyboard and mouse class devices.
 *
 * Installed as a class-level upper filter placed BELOW kbdclass / mouclass
 * (UpperFilters = "DuoSeatFilter kbdclass"). When the class driver sends
 * IOCTL_INTERNAL_KEYBOARD_CONNECT / IOCTL_INTERNAL_MOUSE_CONNECT down the stack, we save its
 * callback and substitute our own (same technique as the WDK kbfiltr / moufiltr samples).
 * Every packet of input from that physical device then passes through us, so we know exactly
 * which device produced it, and we may:
 *   - forward it to the class driver (station 1, default), or
 *   - swallow it and queue it for the user-mode service (station 2), or
 *   - do both (monitor mode, used to identify which device is which).
 *
 * A control device \\.\DuoSeatCtl lets the user-mode service enumerate devices, assign stations
 * and receive captured events through an "inverted call" (a pended IOCTL).
 *
 * Safety: capturing happens only while routing is enabled AND a control handle is open.
 * Closing the last handle (or killing the service) switches routing off, so every device
 * falls back to normal Windows input.
 */
#include "driver.h"

#include <initguid.h>
#include <devpkey.h>

static DUOSEAT_GLOBALS g;

extern "C" DRIVER_INITIALIZE DriverEntry;

EVT_WDF_DRIVER_DEVICE_ADD            DuoEvtDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP       DuoEvtDeviceCleanup;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL DuoEvtInternalIoctl;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL   DuoEvtControlIoctl;
EVT_WDF_DEVICE_FILE_CREATE           DuoEvtFileCreate;
EVT_WDF_FILE_CLOSE                   DuoEvtFileClose;

static VOID DuoKbdServiceCallback(PDEVICE_OBJECT DeviceObject, PKEYBOARD_INPUT_DATA Start, PKEYBOARD_INPUT_DATA End, PULONG Consumed);
static VOID DuoMouServiceCallback(PDEVICE_OBJECT DeviceObject, PMOUSE_INPUT_DATA Start, PMOUSE_INPUT_DATA End, PULONG Consumed);

// ---------------------------------------------------------------------------------------------
// Event ring (spin lock held by caller)
// ---------------------------------------------------------------------------------------------

static VOID RingPushLocked(const DUOSEAT_EVENT* Event)
{
    if (g.Count >= DUOSEAT_RING_SIZE)
    {
        g.Dropped++;
        return;
    }
    ULONG index = (g.Head + g.Count) % DUOSEAT_RING_SIZE;
    g.Ring[index] = *Event;
    g.Count++;
}

static ULONG RingPopLocked(DUOSEAT_EVENT* Out, ULONG Max)
{
    ULONG n = 0;
    while (n < Max && g.Count > 0)
    {
        Out[n++] = g.Ring[g.Head];
        g.Head = (g.Head + 1) % DUOSEAT_RING_SIZE;
        g.Count--;
    }
    return n;
}

// Completes parked READ_EVENTS requests while there is data. Safe at IRQL <= DISPATCH_LEVEL.
static VOID DuoPump(VOID)
{
    for (;;)
    {
        WdfSpinLockAcquire(g.Lock);
        BOOLEAN hasData = (g.Count != 0);
        WdfSpinLockRelease(g.Lock);
        if (!hasData) return;

        WDFREQUEST request;
        NTSTATUS status = WdfIoQueueRetrieveNextRequest(g.PendingReads, &request);
        if (!NT_SUCCESS(status)) return;                       // nobody is waiting

        PVOID  buffer = NULL;
        size_t length = 0;
        status = WdfRequestRetrieveOutputBuffer(request, sizeof(DUOSEAT_EVENT), &buffer, &length);
        if (!NT_SUCCESS(status))
        {
            WdfRequestComplete(request, status);
            continue;
        }

        ULONG max = (ULONG)(length / sizeof(DUOSEAT_EVENT));
        WdfSpinLockAcquire(g.Lock);
        ULONG n = RingPopLocked((DUOSEAT_EVENT*)buffer, max);
        WdfSpinLockRelease(g.Lock);

        if (n == 0)
        {
            status = WdfRequestRequeue(request);               // lost a race with another pump
            if (!NT_SUCCESS(status)) WdfRequestComplete(request, status);
            return;
        }
        WdfRequestCompleteWithInformation(request, STATUS_SUCCESS, (ULONG_PTR)n * sizeof(DUOSEAT_EVENT));
    }
}

// ---------------------------------------------------------------------------------------------
// Class-driver callbacks (run at DISPATCH_LEVEL)
// ---------------------------------------------------------------------------------------------

static VOID DuoKbdServiceCallback(PDEVICE_OBJECT DeviceObject, PKEYBOARD_INPUT_DATA Start, PKEYBOARD_INPUT_DATA End, PULONG Consumed)
{
    PDEVICE_EXTENSION ext = FilterGetData(WdfWdmDeviceGetWdfDeviceHandle(DeviceObject));
    LONG station = ext->Station;

    if (g.Routing != 0 && station != DUOSEAT_STATION_1)
    {
        WdfSpinLockAcquire(g.Lock);
        for (PKEYBOARD_INPUT_DATA p = Start; p < End; p++)
        {
            DUOSEAT_EVENT ev;
            RtlZeroMemory(&ev, sizeof(ev));
            ev.DeviceId = ext->DeviceId;
            ev.Type     = DUOSEAT_TYPE_KEYBOARD;
            ev.MakeCode = p->MakeCode;
            ev.KeyFlags = p->Flags;
            RingPushLocked(&ev);
        }
        WdfSpinLockRelease(g.Lock);
        DuoPump();

        if (station == DUOSEAT_STATION_2)
        {
            *Consumed = (ULONG)(End - Start);                  // swallowed: Windows never sees it
            return;
        }
    }

    (*reinterpret_cast<PSERVICE_CALLBACK_ROUTINE>(ext->UpperConnectData.ClassService))(
        ext->UpperConnectData.ClassDeviceObject, Start, End, Consumed);
}

static VOID DuoMouServiceCallback(PDEVICE_OBJECT DeviceObject, PMOUSE_INPUT_DATA Start, PMOUSE_INPUT_DATA End, PULONG Consumed)
{
    PDEVICE_EXTENSION ext = FilterGetData(WdfWdmDeviceGetWdfDeviceHandle(DeviceObject));
    LONG station = ext->Station;

    if (g.Routing != 0 && station != DUOSEAT_STATION_1)
    {
        WdfSpinLockAcquire(g.Lock);
        for (PMOUSE_INPUT_DATA p = Start; p < End; p++)
        {
            DUOSEAT_EVENT ev;
            RtlZeroMemory(&ev, sizeof(ev));
            ev.DeviceId    = ext->DeviceId;
            ev.Type        = DUOSEAT_TYPE_MOUSE;
            ev.MouseFlags  = p->Flags;
            ev.ButtonFlags = p->ButtonFlags;
            ev.ButtonData  = p->ButtonData;
            ev.LastX       = p->LastX;
            ev.LastY       = p->LastY;
            RingPushLocked(&ev);
        }
        WdfSpinLockRelease(g.Lock);
        DuoPump();

        if (station == DUOSEAT_STATION_2)
        {
            *Consumed = (ULONG)(End - Start);
            return;
        }
    }

    (*reinterpret_cast<PSERVICE_CALLBACK_ROUTINE>(ext->UpperConnectData.ClassService))(
        ext->UpperConnectData.ClassDeviceObject, Start, End, Consumed);
}

// ---------------------------------------------------------------------------------------------
// Filter device: internal IOCTLs coming from the class driver
// ---------------------------------------------------------------------------------------------

static VOID DuoForwardRequest(WDFDEVICE Device, WDFREQUEST Request)
{
    WDF_REQUEST_SEND_OPTIONS options;
    WdfRequestFormatRequestUsingCurrentType(Request);
    WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    if (!WdfRequestSend(Request, WdfDeviceGetIoTarget(Device), &options))
    {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}

VOID DuoEvtInternalIoctl(WDFQUEUE Queue, WDFREQUEST Request, size_t OutputBufferLength, size_t InputBufferLength, ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    WDFDEVICE         device = WdfIoQueueGetDevice(Queue);
    PDEVICE_EXTENSION ext    = FilterGetData(device);
    NTSTATUS          status = STATUS_SUCCESS;
    PCONNECT_DATA     connectData = NULL;
    size_t            length = 0;

    switch (IoControlCode)
    {
    case IOCTL_INTERNAL_KEYBOARD_CONNECT:
        if (ext->UpperConnectData.ClassService != NULL) { status = STATUS_SHARING_VIOLATION; break; }
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(CONNECT_DATA), (PVOID*)&connectData, &length);
        if (!NT_SUCCESS(status)) break;
        ext->UpperConnectData = *connectData;
        ext->Type = DUOSEAT_TYPE_KEYBOARD;
        connectData->ClassDeviceObject = WdfDeviceWdmGetDeviceObject(device);
        connectData->ClassService      = reinterpret_cast<PVOID>(&DuoKbdServiceCallback);
        break;

    case IOCTL_INTERNAL_MOUSE_CONNECT:
        if (ext->UpperConnectData.ClassService != NULL) { status = STATUS_SHARING_VIOLATION; break; }
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(CONNECT_DATA), (PVOID*)&connectData, &length);
        if (!NT_SUCCESS(status)) break;
        ext->UpperConnectData = *connectData;
        ext->Type = DUOSEAT_TYPE_MOUSE;
        connectData->ClassDeviceObject = WdfDeviceWdmGetDeviceObject(device);
        connectData->ClassService      = reinterpret_cast<PVOID>(&DuoMouServiceCallback);
        break;

    case IOCTL_INTERNAL_KEYBOARD_DISCONNECT:
    case IOCTL_INTERNAL_MOUSE_DISCONNECT:
        status = STATUS_NOT_IMPLEMENTED;                       // same as the WDK samples
        break;

    default:
        break;                                                 // everything else is passed down untouched
    }

    if (!NT_SUCCESS(status))
    {
        WdfRequestComplete(Request, status);
        return;
    }
    DuoForwardRequest(device, Request);
}

// ---------------------------------------------------------------------------------------------
// AddDevice / cleanup
// ---------------------------------------------------------------------------------------------

static VOID DuoQueryInstanceId(WDFDEVICE Device, PDEVICE_EXTENSION Ext)
{
    WDF_DEVICE_PROPERTY_DATA propertyData;
    WDF_DEVICE_PROPERTY_DATA_INIT(&propertyData, &DEVPKEY_Device_InstanceId);

    WDFMEMORY  memory = NULL;
    DEVPROPTYPE type  = 0;
    NTSTATUS status = WdfDeviceAllocAndQueryPropertyEx(Device, &propertyData, PagedPool, WDF_NO_OBJECT_ATTRIBUTES, &memory, &type);
    if (!NT_SUCCESS(status)) return;                           // not fatal: the id just stays empty

    size_t bytes = 0;
    PVOID  buffer = WdfMemoryGetBuffer(memory, &bytes);
    if (type == DEVPROP_TYPE_STRING && buffer != NULL && bytes >= sizeof(WCHAR))
    {
        size_t chars = bytes / sizeof(WCHAR);
        if (chars > DUOSEAT_MAX_ID_CHARS - 1) chars = DUOSEAT_MAX_ID_CHARS - 1;
        RtlCopyMemory(Ext->InstanceId, buffer, chars * sizeof(WCHAR));
        Ext->InstanceId[chars] = L'\0';
    }
    WdfObjectDelete(memory);
}

NTSTATUS DuoEvtDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT DeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);

    WdfFdoInitSetFilter(DeviceInit);

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_EXTENSION);
    attributes.EvtCleanupCallback = DuoEvtDeviceCleanup;

    WDFDEVICE device;
    NTSTATUS status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    PDEVICE_EXTENSION ext = FilterGetData(device);
    ext->Device   = device;
    ext->Station  = DUOSEAT_STATION_1;
    ext->Type     = DUOSEAT_TYPE_UNKNOWN;
    ext->Slot     = -1;
    ext->DeviceId = (ULONG)InterlockedIncrement(&g.NextId);
    DuoQueryInstanceId(device, ext);

    WdfSpinLockAcquire(g.Lock);
    for (LONG i = 0; i < DUOSEAT_MAX_DEVICES; i++)
    {
        if (g.Devices[i] == NULL)
        {
            g.Devices[i] = ext;
            ext->Slot = i;
            break;
        }
    }
    WdfSpinLockRelease(g.Lock);                                // no free slot: still works as pure pass-through

    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoInternalDeviceControl = DuoEvtInternalIoctl;
    return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
}

VOID DuoEvtDeviceCleanup(WDFOBJECT Object)
{
    PDEVICE_EXTENSION ext = FilterGetData((WDFDEVICE)Object);
    WdfSpinLockAcquire(g.Lock);
    if (ext->Slot >= 0 && ext->Slot < DUOSEAT_MAX_DEVICES && g.Devices[ext->Slot] == ext)
    {
        g.Devices[ext->Slot] = NULL;
    }
    ext->Slot = -1;
    WdfSpinLockRelease(g.Lock);
}

// ---------------------------------------------------------------------------------------------
// Control device (\\.\DuoSeatCtl)
// ---------------------------------------------------------------------------------------------

VOID DuoEvtFileCreate(WDFDEVICE Device, WDFREQUEST Request, WDFFILEOBJECT FileObject)
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(FileObject);
    InterlockedIncrement(&g.OpenHandles);
    WdfRequestComplete(Request, STATUS_SUCCESS);
}

VOID DuoEvtFileClose(WDFFILEOBJECT FileObject)
{
    UNREFERENCED_PARAMETER(FileObject);
    if (InterlockedDecrement(&g.OpenHandles) <= 0)
    {
        InterlockedExchange(&g.OpenHandles, 0);
        InterlockedExchange(&g.Routing, 0);                    // fail-safe: nobody is listening any more
        WdfSpinLockAcquire(g.Lock);
        g.Count = 0;
        g.Head  = 0;
        WdfSpinLockRelease(g.Lock);
    }
}

VOID DuoEvtControlIoctl(WDFQUEUE Queue, WDFREQUEST Request, size_t OutputBufferLength, size_t InputBufferLength, ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(Queue);

    NTSTATUS  status = STATUS_SUCCESS;
    ULONG_PTR info   = 0;

    switch (IoControlCode)
    {
    case IOCTL_DUOSEAT_GET_VERSION:
    {
        PULONG out = NULL;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), (PVOID*)&out, NULL);
        if (NT_SUCCESS(status)) { *out = DUOSEAT_PROTOCOL_VERSION; info = sizeof(ULONG); }
        break;
    }

    case IOCTL_DUOSEAT_GET_DEVICES:
    {
        PDUOSEAT_DEVICE_INFO out = NULL;
        size_t length = 0;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(DUOSEAT_DEVICE_INFO), (PVOID*)&out, &length);
        if (!NT_SUCCESS(status)) break;

        ULONG max = (ULONG)(length / sizeof(DUOSEAT_DEVICE_INFO));
        ULONG n = 0;
        WdfSpinLockAcquire(g.Lock);
        for (ULONG i = 0; i < DUOSEAT_MAX_DEVICES && n < max; i++)
        {
            PDEVICE_EXTENSION ext = g.Devices[i];
            if (ext == NULL) continue;
            RtlZeroMemory(&out[n], sizeof(DUOSEAT_DEVICE_INFO));
            out[n].DeviceId = ext->DeviceId;
            out[n].Type     = (ULONG)ext->Type;
            out[n].Station  = (ULONG)ext->Station;
            RtlCopyMemory(out[n].InstanceId, ext->InstanceId, sizeof(out[n].InstanceId));
            n++;
        }
        WdfSpinLockRelease(g.Lock);
        info = (ULONG_PTR)n * sizeof(DUOSEAT_DEVICE_INFO);
        break;
    }

    case IOCTL_DUOSEAT_SET_STATION:
    {
        PDUOSEAT_SET_STATION in = NULL;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(DUOSEAT_SET_STATION), (PVOID*)&in, NULL);
        if (!NT_SUCCESS(status)) break;
        if (in->Station < DUOSEAT_STATION_1 || in->Station > DUOSEAT_STATION_MONITOR) { status = STATUS_INVALID_PARAMETER; break; }

        status = STATUS_NOT_FOUND;
        WdfSpinLockAcquire(g.Lock);
        for (ULONG i = 0; i < DUOSEAT_MAX_DEVICES; i++)
        {
            PDEVICE_EXTENSION ext = g.Devices[i];
            if (ext != NULL && ext->DeviceId == in->DeviceId)
            {
                ext->Station = (LONG)in->Station;
                status = STATUS_SUCCESS;
                break;
            }
        }
        WdfSpinLockRelease(g.Lock);
        break;
    }

    case IOCTL_DUOSEAT_SET_ROUTING:
    {
        PULONG in = NULL;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(ULONG), (PVOID*)&in, NULL);
        if (!NT_SUCCESS(status)) break;
        InterlockedExchange(&g.Routing, (*in != 0) ? 1 : 0);
        if (*in == 0)
        {
            WdfSpinLockAcquire(g.Lock);
            g.Count = 0;
            g.Head  = 0;
            WdfSpinLockRelease(g.Lock);
        }
        break;
    }

    case IOCTL_DUOSEAT_READ_EVENTS:
        if (OutputBufferLength < sizeof(DUOSEAT_EVENT))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        status = WdfRequestForwardToIoQueue(Request, g.PendingReads);
        if (NT_SUCCESS(status))
        {
            DuoPump();                                         // data may already be waiting
            return;                                            // request is parked; do not complete it here
        }
        break;

    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    UNREFERENCED_PARAMETER(InputBufferLength);
    WdfRequestCompleteWithInformation(Request, status, info);
}

static NTSTATUS DuoCreateControlDevice(WDFDRIVER Driver)
{
    DECLARE_CONST_UNICODE_STRING(sddl,    L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");   // SYSTEM + Administrators only
    DECLARE_CONST_UNICODE_STRING(ntName,  DUOSEAT_CONTROL_NT_NAME);
    DECLARE_CONST_UNICODE_STRING(dosName, DUOSEAT_CONTROL_DOS_NAME);

    PWDFDEVICE_INIT init = WdfControlDeviceInitAllocate(Driver, &sddl);
    if (init == NULL) return STATUS_INSUFFICIENT_RESOURCES;

    WDFDEVICE control = NULL;
    NTSTATUS status = WdfDeviceInitAssignName(init, &ntName);
    if (NT_SUCCESS(status))
    {
        WdfDeviceInitSetExclusive(init, FALSE);

        WDF_FILEOBJECT_CONFIG fileConfig;
        WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, DuoEvtFileCreate, DuoEvtFileClose, WDF_NO_EVENT_CALLBACK);
        WdfDeviceInitSetFileObjectConfig(init, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

        status = WdfDeviceCreate(&init, WDF_NO_OBJECT_ATTRIBUTES, &control);   // consumes (and NULLs) init
    }
    if (!NT_SUCCESS(status))
    {
        if (init != NULL) WdfDeviceInitFree(init);
        return status;
    }

    status = WdfDeviceCreateSymbolicLink(control, &dosName);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = DuoEvtControlIoctl;
    status = WdfIoQueueCreate(control, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG pendingConfig;
    WDF_IO_QUEUE_CONFIG_INIT(&pendingConfig, WdfIoQueueDispatchManual);
    status = WdfIoQueueCreate(control, &pendingConfig, WDF_NO_OBJECT_ATTRIBUTES, &g.PendingReads);
    if (!NT_SUCCESS(status)) return status;

    WdfControlFinishInitializing(control);
    return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------------------------

extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, DuoEvtDeviceAdd);

    WDFDRIVER driver;
    NTSTATUS status = WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, &driver);
    if (!NT_SUCCESS(status)) return status;

    WDF_OBJECT_ATTRIBUTES lockAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = driver;
    status = WdfSpinLockCreate(&lockAttributes, &g.Lock);
    if (!NT_SUCCESS(status)) return status;

    return DuoCreateControlDevice(driver);
}
