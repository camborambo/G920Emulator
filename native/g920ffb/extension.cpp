#define INITGUID
#include <initguid.h>
#include <dinput.h>
#include <dinputd.h>
#include "extension.h"
#include "com.h"

CRITICAL_SECTION CriticalSection;

BOOL WINAPI DllMain(HINSTANCE Instance, DWORD Reason, LPVOID Reserved)
{
	UNREFERENCED_PARAMETER(Instance);
	UNREFERENCED_PARAMETER(Reserved);
	switch (Reason)
	{
	case DLL_PROCESS_ATTACH:
		InitializeCriticalSection(&CriticalSection);
		break;
	case DLL_PROCESS_DETACH:
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
	return S_OK;
}
