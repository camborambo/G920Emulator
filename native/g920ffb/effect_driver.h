#pragma once

#ifndef DIRECTINPUT_VERSION
#define DIRECTINPUT_VERSION 0x0800
#endif

#include <windows.h>
#include <objbase.h>
#include <dinput.h>
#include <dinputd.h>

#include "extension.h"
#include "effect.h"

class CEffectDriver : public IDirectInputEffectDriver
{
public:
	CEffectDriver(VOID);

	HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, PVOID*);
	ULONG STDMETHODCALLTYPE AddRef(VOID);
	ULONG STDMETHODCALLTYPE Release(VOID);

	HRESULT STDMETHODCALLTYPE DeviceID(DWORD, DWORD, DWORD, DWORD, LPVOID);
	HRESULT STDMETHODCALLTYPE GetVersions(LPDIDRIVERVERSIONS);
	HRESULT STDMETHODCALLTYPE Escape(DWORD, DWORD, LPDIEFFESCAPE);
	HRESULT STDMETHODCALLTYPE SetGain(DWORD, DWORD);
	HRESULT STDMETHODCALLTYPE SendForceFeedbackCommand(DWORD, DWORD);
	HRESULT STDMETHODCALLTYPE GetForceFeedbackState(DWORD, LPDIDEVICESTATE);
	HRESULT STDMETHODCALLTYPE DownloadEffect(DWORD, DWORD, LPDWORD, LPCDIEFFECT, DWORD);
	HRESULT STDMETHODCALLTYPE DestroyEffect(DWORD, DWORD);
	HRESULT STDMETHODCALLTYPE StartEffect(DWORD, DWORD, DWORD, DWORD);
	HRESULT STDMETHODCALLTYPE StopEffect(DWORD, DWORD);
	HRESULT STDMETHODCALLTYPE GetEffectStatus(DWORD, DWORD, LPDWORD);

	LONG EffectCount;
	CEffect** EffectList;
	DWORD Gain;
	BOOL Actuator;
	BOOL Quit;
	BOOL Paused;

	// Probe stats for the bridge UI / log.
	UINT32 TypesSeen;
	UINT32 DownloadCount;
	UINT32 LastEffectType;
	UINT32 LastFlags;

private:
	LONG ReferenceCount;
	HANDLE WorkerThread;
	LONG EffectIndex;
	BOOL Stopped;
	LONG PausedTime;
};

STDAPI_(DWORD) WINAPI EffectProc(LPVOID);
