#pragma once
#include <windows.h>
#include <objbase.h>

class CClassFactory : public IClassFactory
{
public:
	CClassFactory(VOID);
	HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, PVOID*);
	ULONG STDMETHODCALLTYPE AddRef(VOID);
	ULONG STDMETHODCALLTYPE Release(VOID);
	HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown*, REFIID, PVOID*);
	HRESULT STDMETHODCALLTYPE LockServer(BOOL);

private:
	LONG ReferenceCount;
};
