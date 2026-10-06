#define INITGUID
#include <initguid.h>
#include <dinput.h>
#include <dinputd.h>
#include "extension.h"
#include "com.h"

CRITICAL_SECTION CriticalSection;
LONG g_cObjects = 0;
LONG g_cLocks = 0;

void G920FfbAddObject(void)
{
	InterlockedIncrement(&g_cObjects);
}

void G920FfbReleaseObject(void)
{
	InterlockedDecrement(&g_cObjects);
}

BOOL WINAPI DllMain(HINSTANCE Instance, DWORD Reason, LPVOID Reserved)
{
	UNREFERENCED_PARAMETER(Reserved);
	switch (Reason)
	{
	case DLL_PROCESS_ATTACH:
		DisableThreadLibraryCalls(Instance);
		InitializeCriticalSection(&CriticalSection);
		// Pin so CoFreeUnusedLibraries / FreeLibrary cannot unload us while
		// Steam (or DI) still holds vtable pointers into this module.
		// Without this, WER reports: steam.exe faulting in g920ffb.dll_unloaded (0xc0000005).
		{
			HMODULE pinned = nullptr;
			GetModuleHandleExW(
				GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
				reinterpret_cast<LPCWSTR>(Instance),
				&pinned);
		}
		break;
	case DLL_PROCESS_DETACH:
		// Only runs on process exit when PIN is set (or if pin failed).
		DeleteCriticalSection(&CriticalSection);
		break;
	}
	return TRUE;
}

STDAPI DllGetClassObject(REFCLSID ClassID, REFIID InterfaceID, LPVOID* Interface)
{
	if (!IsEqualGUID(ClassID, ClassID_G920FFB))
		return CLASS_E_CLASSNOTAVAILABLE;

	CClassFactory* Factory = new CClassFactory();
	if (!Factory)
		return E_OUTOFMEMORY;

	HRESULT Result = Factory->QueryInterface(InterfaceID, Interface);
	Factory->Release();
	return Result;
}

STDAPI DllCanUnloadNow(VOID)
{
	// Never allow COM to unload this inproc server for the process lifetime.
	// Steam enumerates the virtual G920, CoCreates our IDirectInputEffectDriver,
	// then CoFreeUnusedLibraries — with a naive S_OK here the DLL was freed while
	// DI still called into it (crash: g920ffb.dll_unloaded / ACCESS_VIOLATION).
	if (g_cObjects != 0 || g_cLocks != 0)
		return S_FALSE;
	return S_FALSE;
}
