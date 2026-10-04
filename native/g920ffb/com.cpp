#include "com.h"
#include "effect_driver.h"

CClassFactory::CClassFactory(VOID)
{
	ReferenceCount = 1;
}

HRESULT STDMETHODCALLTYPE CClassFactory::QueryInterface(REFIID InterfaceID, PVOID* Interface)
{
	if (IsEqualIID(InterfaceID, IID_IUnknown) || IsEqualIID(InterfaceID, IID_IClassFactory))
	{
		AddRef();
		*Interface = this;
		return S_OK;
	}
	*Interface = NULL;
	return E_NOINTERFACE;
}

ULONG STDMETHODCALLTYPE CClassFactory::AddRef(VOID)
{
	return InterlockedIncrement(&ReferenceCount);
}

ULONG STDMETHODCALLTYPE CClassFactory::Release(VOID)
{
	if (InterlockedDecrement(&ReferenceCount) == 0)
	{
		delete this;
		return 0;
	}
	return ReferenceCount;
}

HRESULT STDMETHODCALLTYPE CClassFactory::CreateInstance(IUnknown* UnknownInterface, REFIID InterfaceID, PVOID* Interface)
{
	UNREFERENCED_PARAMETER(UnknownInterface);
	CEffectDriver* EffectDriver = new CEffectDriver();
	if (!EffectDriver)
		return E_OUTOFMEMORY;
	HRESULT Result = EffectDriver->QueryInterface(InterfaceID, Interface);
	EffectDriver->Release();
	return Result;
}

HRESULT STDMETHODCALLTYPE CClassFactory::LockServer(BOOL Lock)
{
	UNREFERENCED_PARAMETER(Lock);
	return S_OK;
}
