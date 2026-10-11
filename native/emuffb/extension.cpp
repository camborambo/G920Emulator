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
		// Keep this module mapped for the process lifetime. Steam/DirectInput
		// CoCreates our IDirectInputEffectDriver then CoFreeUnusedLibraries /
		// FreeLibrary — without a pin, WER reports steam.exe faulting in
		// emuffb.dll_unloaded (0xc0000005) on the leftover vtable.
		{
			HMODULE pinned = nullptr;
			if (!GetModuleHandleExW(
				GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
				reinterpret_cast<LPCWSTR>(Instance),
				&pinned))
			{
				// PIN failed — hold an extra LoadLibrary ref that we never free.
				wchar_t path[MAX_PATH];
				if (GetModuleFileNameW(Instance, path, MAX_PATH) > 0)
					LoadLibraryW(path);
			}
			// Also bump COM lock count so DllCanUnloadNow stays S_FALSE even if
			// a future edit makes the object-count path fallible.
			InterlockedIncrement(&g_cLocks);
		}
		break;
	case DLL_PROCESS_DETACH:
		// With PIN (or an unreclaimed LoadLibrary), this normally only runs
		// on process exit — safe to tear down the CS then.
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
	return S_FALSE;
}
