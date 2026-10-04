#include "effect_driver.h"

CEffectDriver::CEffectDriver(VOID)
{
	ReferenceCount = 1;
	WorkerThread = NULL;
	EffectIndex = 1;
	EffectCount = 0;
	EffectList = NULL;
	Stopped = TRUE;
	Paused = FALSE;
	PausedTime = 0;
	Gain = 10000;
	Actuator = TRUE;
	Quit = FALSE;
	TypesSeen = 0;
	DownloadCount = 0;
	LastEffectType = 0;
	LastFlags = 0;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::QueryInterface(REFIID InterfaceID, PVOID* Interface)
{
	if (IsEqualIID(InterfaceID, IID_IUnknown) || IsEqualIID(InterfaceID, IID_IDirectInputEffectDriver))
	{
		AddRef();
		*Interface = this;
		return S_OK;
	}
	*Interface = NULL;
	return E_NOINTERFACE;
}

ULONG STDMETHODCALLTYPE CEffectDriver::AddRef(VOID)
{
	return InterlockedIncrement(&ReferenceCount);
}

ULONG STDMETHODCALLTYPE CEffectDriver::Release(VOID)
{
	if (InterlockedDecrement(&ReferenceCount) == 0)
	{
		Quit = TRUE;
		if (WorkerThread)
		{
			WaitForSingleObject(WorkerThread, 2000);
			CloseHandle(WorkerThread);
			WorkerThread = NULL;
		}
		for (LONG i = 0; i < EffectCount; i++)
			delete EffectList[i];
		free(EffectList);
		delete this;
		return 0;
	}
	return ReferenceCount;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::DeviceID(DWORD, DWORD, DWORD, DWORD, LPVOID)
{
	DWORD ThreadID = 0;
	WorkerThread = CreateThread(NULL, 0, EffectProc, (LPVOID)this, 0, &ThreadID);
	return WorkerThread ? S_OK : E_FAIL;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::GetVersions(LPDIDRIVERVERSIONS DriverVersions)
{
	if (!DriverVersions || DriverVersions->dwSize != sizeof(DIDRIVERVERSIONS))
		return E_INVALIDARG;
	DriverVersions->dwFirmwareRevision = 1;
	DriverVersions->dwHardwareRevision = 1;
	DriverVersions->dwFFDriverVersion = 1;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::Escape(DWORD, DWORD, LPDIEFFESCAPE)
{
	return E_NOTIMPL;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::SetGain(DWORD, DWORD NewGain)
{
	EnterCriticalSection(&CriticalSection);
	if (NewGain < 1) NewGain = 1;
	if (NewGain > 10000) NewGain = 10000;
	Gain = NewGain;
	LeaveCriticalSection(&CriticalSection);
	return S_OK;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::SendForceFeedbackCommand(DWORD, DWORD Command)
{
	EnterCriticalSection(&CriticalSection);
	HRESULT Result = S_OK;
	switch (Command)
	{
	case DISFFC_RESET:
		for (LONG i = 0; i < EffectCount; i++)
			delete EffectList[i];
		EffectCount = 0;
		free(EffectList);
		EffectList = NULL;
		Stopped = TRUE;
		Paused = FALSE;
		break;
	case DISFFC_STOPALL:
		for (LONG i = 0; i < EffectCount; i++)
			EffectList[i]->Status = 0;
		Stopped = TRUE;
		Paused = FALSE;
		break;
	case DISFFC_PAUSE:
		Paused = TRUE;
		PausedTime = GetTickCount();
		break;
	case DISFFC_CONTINUE:
		for (LONG i = 0; i < EffectCount; i++)
			EffectList[i]->StartTime += (GetTickCount() - PausedTime);
		Paused = FALSE;
		break;
	case DISFFC_SETACTUATORSON:
		Actuator = TRUE;
		break;
	case DISFFC_SETACTUATORSOFF:
		Actuator = FALSE;
		break;
	default:
		Result = E_NOTIMPL;
		break;
	}
	LeaveCriticalSection(&CriticalSection);
	return Result;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::GetForceFeedbackState(DWORD, LPDIDEVICESTATE DeviceState)
{
	if (!DeviceState || DeviceState->dwSize != sizeof(DIDEVICESTATE))
		return E_INVALIDARG;

	EnterCriticalSection(&CriticalSection);
	DeviceState->dwState = DIGFFS_POWERON | DIGFFS_SAFETYSWITCHOFF | DIGFFS_USERFFSWITCHON;
	if (EffectCount == 0) DeviceState->dwState |= DIGFFS_EMPTY;
	if (Stopped) DeviceState->dwState |= DIGFFS_STOPPED;
	if (Paused) DeviceState->dwState |= DIGFFS_PAUSED;
	DeviceState->dwState |= Actuator ? DIGFFS_ACTUATORSON : DIGFFS_ACTUATORSOFF;
	DeviceState->dwLoad = 0;
	LeaveCriticalSection(&CriticalSection);
	return S_OK;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::DownloadEffect(
	DWORD, DWORD EffectType, LPDWORD EffectHandle, LPCDIEFFECT DiEffect, DWORD Flags)
{
	if (Flags & DIEP_NODOWNLOAD)
		return S_OK;
	if (!EffectHandle || !DiEffect)
		return E_POINTER;

	EnterCriticalSection(&CriticalSection);

	CEffect* Effect = NULL;
	if (*EffectHandle == 0)
	{
		Effect = new CEffect();
		Effect->Handle = (DWORD)(EffectIndex++);
		EffectCount++;
		CEffect** NewList = (CEffect**)realloc(EffectList, sizeof(CEffect*) * EffectCount);
		if (!NewList)
		{
			delete Effect;
			EffectCount--;
			LeaveCriticalSection(&CriticalSection);
			return E_OUTOFMEMORY;
		}
		EffectList = NewList;
		EffectList[EffectCount - 1] = Effect;
		*EffectHandle = Effect->Handle;
	}
	else
	{
		for (LONG i = 0; i < EffectCount; i++)
		{
			if (EffectList[i]->Handle == *EffectHandle)
			{
				Effect = EffectList[i];
				break;
			}
		}
		if (!Effect)
		{
			LeaveCriticalSection(&CriticalSection);
			return E_HANDLE;
		}
	}

	Effect->Type = EffectType;
	Effect->DiEffect.dwFlags = DiEffect->dwFlags;

	if (Flags & DIEP_DURATION) Effect->DiEffect.dwDuration = DiEffect->dwDuration;
	if (Flags & DIEP_SAMPLEPERIOD) Effect->DiEffect.dwSamplePeriod = DiEffect->dwSamplePeriod;
	if (Flags & DIEP_GAIN) Effect->DiEffect.dwGain = DiEffect->dwGain;
	if (Flags & DIEP_TRIGGERBUTTON) Effect->DiEffect.dwTriggerButton = DiEffect->dwTriggerButton;
	if (Flags & DIEP_TRIGGERREPEATINTERVAL) Effect->DiEffect.dwTriggerRepeatInterval = DiEffect->dwTriggerRepeatInterval;
	if (Flags & DIEP_STARTDELAY) Effect->DiEffect.dwStartDelay = DiEffect->dwStartDelay;

	if ((Flags & DIEP_ENVELOPE) && DiEffect->lpEnvelope)
		CopyMemory(&Effect->DiEnvelope, DiEffect->lpEnvelope, sizeof(DIENVELOPE));

	if (Flags & DIEP_DIRECTION)
	{
		Effect->DirectionSign = 1;
		if (DiEffect->cAxes > 0 && DiEffect->rglDirection)
		{
			LONG d = DiEffect->rglDirection[0];
			if (DiEffect->dwFlags & DIEFF_CARTESIAN)
			{
				if (d < 0) Effect->DirectionSign = -1;
			}
			else
			{
				// Polar / spherical: 0=north ?c 9000=+X ?c 18000=south ?c 27000=-X
				LONG deg = ((d % 36000) + 36000) % 36000;
				if (deg > 9000 && deg < 27000)
					Effect->DirectionSign = -1;
			}
		}
	}

	if ((Flags & DIEP_TYPESPECIFICPARAMS) && DiEffect->lpvTypeSpecificParams)
	{
		switch (EffectType)
		{
		case CONSTANT_FORCE:
			CopyMemory(&Effect->DiConstantForce, DiEffect->lpvTypeSpecificParams, sizeof(DICONSTANTFORCE));
			break;
		case RAMP_FORCE:
			CopyMemory(&Effect->DiRampforce, DiEffect->lpvTypeSpecificParams, sizeof(DIRAMPFORCE));
			break;
		case SQUARE:
		case SINE:
		case TRIANGLE:
		case SAWTOOTH_UP:
		case SAWTOOTH_DOWN:
			CopyMemory(&Effect->DiPeriodic, DiEffect->lpvTypeSpecificParams, sizeof(DIPERIODIC));
			break;
		case SPRING:
		case DAMPER:
		case INERTIA:
		case FRICTION:
			// Prefer the first condition (X / wheel axis). Games may pass one per axis.
			ZeroMemory(&Effect->DiCondition, sizeof(DICONDITION));
			if (DiEffect->cbTypeSpecificParams >= sizeof(DICONDITION))
				CopyMemory(&Effect->DiCondition, DiEffect->lpvTypeSpecificParams, sizeof(DICONDITION));
			break;
		default:
			break;
		}
	}

	// DIEP_START: start as part of DownloadEffect (normal for continuous constant force).
	// Crash/impulse effects often use StartEffect(); road CF often only sets DIEP_START.
	if (Flags & DIEP_START)
	{
		Effect->Status = DIEGES_PLAYING;
		Effect->PlayCount = (DWORD)-1;
		Effect->StartTime = GetTickCount();
		Stopped = FALSE;
	}

	// Continuous constant-force updates: keep/start playing whenever magnitude is set.
	// Matches real Logitech HID++ behavior (download implies active CF slot).
	if (Effect->Type == CONSTANT_FORCE && (Flags & DIEP_TYPESPECIFICPARAMS))
	{
		if (Effect->DiConstantForce.lMagnitude != 0)
		{
			Effect->Status = DIEGES_PLAYING;
			if (Effect->PlayCount == 0)
				Effect->PlayCount = (DWORD)-1;
			if (Effect->StartTime == 0)
				Effect->StartTime = GetTickCount();
			Stopped = FALSE;
		}
		else if (!(Flags & DIEP_START))
		{
			// Magnitude cleared ? silence this slot without requiring StopEffect.
			Effect->Status = 0;
		}
	}

	// Condition effects (spring/damper/?c): download implies active for most titles.
	if ((Effect->Type == SPRING || Effect->Type == DAMPER ||
		 Effect->Type == INERTIA || Effect->Type == FRICTION) &&
		(Flags & DIEP_TYPESPECIFICPARAMS))
	{
		Effect->Status = DIEGES_PLAYING;
		if (Effect->PlayCount == 0)
			Effect->PlayCount = (DWORD)-1;
		if (Effect->StartTime == 0)
			Effect->StartTime = GetTickCount();
		Stopped = FALSE;
	}

	// Periodic / ramp: stay live when params arrive with non-zero magnitude (rumble).
	if ((Effect->Type == SQUARE || Effect->Type == SINE || Effect->Type == TRIANGLE ||
		 Effect->Type == SAWTOOTH_UP || Effect->Type == SAWTOOTH_DOWN) &&
		(Flags & DIEP_TYPESPECIFICPARAMS) && Effect->DiPeriodic.dwMagnitude != 0)
	{
		Effect->Status = DIEGES_PLAYING;
		if (Effect->PlayCount == 0)
			Effect->PlayCount = (DWORD)-1;
		if (Effect->StartTime == 0)
			Effect->StartTime = GetTickCount();
		Stopped = FALSE;
	}

	LONG extra = 0;
	if (Effect->Type == CONSTANT_FORCE) extra = Effect->DiConstantForce.lMagnitude;
	else if (Effect->Type == SPRING || Effect->Type == DAMPER)
		extra = Effect->DiCondition.lPositiveCoefficient;
	else if (Effect->Type >= SQUARE && Effect->Type <= SAWTOOTH_DOWN)
		extra = (LONG)Effect->DiPeriodic.dwMagnitude;

	if (Effect->Type < 32)
		TypesSeen |= (1u << Effect->Type);
	DownloadCount++;
	LastEffectType = Effect->Type;
	LastFlags = Flags;

	G920FfbLogEffect(Effect->Type, Flags, Effect->Handle, extra);

	LeaveCriticalSection(&CriticalSection);
	return S_OK;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::DestroyEffect(DWORD, DWORD EffectHandle)
{
	EnterCriticalSection(&CriticalSection);
	for (LONG i = 0; i < EffectCount; i++)
	{
		if (EffectList[i]->Handle == EffectHandle)
		{
			delete EffectList[i];
			for (LONG j = i; j < EffectCount - 1; j++)
				EffectList[j] = EffectList[j + 1];
			EffectCount--;
			break;
		}
	}
	LeaveCriticalSection(&CriticalSection);
	return S_OK;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::StartEffect(DWORD, DWORD EffectHandle, DWORD Mode, DWORD Count)
{
	UNREFERENCED_PARAMETER(Mode);
	EnterCriticalSection(&CriticalSection);
	for (LONG i = 0; i < EffectCount; i++)
	{
		if (EffectList[i]->Handle == EffectHandle)
		{
			EffectList[i]->Status = DIEGES_PLAYING;
			EffectList[i]->PlayCount = Count;
			EffectList[i]->StartTime = GetTickCount();
			Stopped = FALSE;
			break;
		}
	}
	LeaveCriticalSection(&CriticalSection);
	return S_OK;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::StopEffect(DWORD, DWORD EffectHandle)
{
	EnterCriticalSection(&CriticalSection);
	for (LONG i = 0; i < EffectCount; i++)
	{
		if (EffectList[i]->Handle == EffectHandle)
		{
			EffectList[i]->Status = 0;
			break;
		}
	}
	LeaveCriticalSection(&CriticalSection);
	return S_OK;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::GetEffectStatus(DWORD, DWORD EffectHandle, LPDWORD Status)
{
	if (!Status) return E_POINTER;
	*Status = 0;
	EnterCriticalSection(&CriticalSection);
	for (LONG i = 0; i < EffectCount; i++)
	{
		if (EffectList[i]->Handle == EffectHandle)
		{
			*Status = EffectList[i]->Status;
			break;
		}
	}
	LeaveCriticalSection(&CriticalSection);
	return S_OK;
}

STDAPI_(DWORD) WINAPI EffectProc(LPVOID EffectDriverInterface)
{
	CEffectDriver* Driver = (CEffectDriver*)EffectDriverInterface;

	HANDLE Mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE,
		0, sizeof(G920FfbSharedState), G920FFB_SHM_NAME);
	if (!Mapping)
		return 0;

	G920FfbSharedState* Shared = (G920FfbSharedState*)MapViewOfFile(Mapping, FILE_MAP_WRITE, 0, 0, sizeof(G920FfbSharedState));
	if (!Shared)
	{
		CloseHandle(Mapping);
		return 0;
	}

	ZeroMemory(Shared, sizeof(G920FfbSharedState));
	Shared->Magic = G920FFB_MAGIC;
	Shared->Version = G920FFB_VERSION;

	UINT32 Seq = 0;
	while (!Driver->Quit)
	{
		LONG TorqueDi = 0;
		DWORD Gain = 10000;
		BOOL Playing = FALSE;

		// Axis feedback from the emulator (virtual G920 steering) ? required for
		// spring/damper. No extra scaling; DI condition math uses these units.
		LONG AxisPos = (LONG)(Shared->Steering * 10000.0f);
		LONG AxisVel = (LONG)(Shared->SteeringVel * 10000.0f);
		if (AxisPos > 10000) AxisPos = 10000;
		if (AxisPos < -10000) AxisPos = -10000;
		if (AxisVel > 10000) AxisVel = 10000;
		if (AxisVel < -10000) AxisVel = -10000;

		UINT32 typesPlaying = 0;
		UINT32 typesSeen = 0;
		UINT32 downloadCount = 0;
		UINT32 lastType = 0;
		UINT32 lastFlags = 0;

		EnterCriticalSection(&CriticalSection);
		Gain = Driver->Gain;
		typesSeen = Driver->TypesSeen;
		downloadCount = Driver->DownloadCount;
		lastType = Driver->LastEffectType;
		lastFlags = Driver->LastFlags;
		if (Driver->Actuator && !Driver->Paused)
		{
			for (LONG i = 0; i < Driver->EffectCount; i++)
			{
				CEffect* e = Driver->EffectList[i];
				if (e->Status == DIEGES_PLAYING)
				{
					Playing = TRUE;
					if (e->Type < 32)
						typesPlaying |= (1u << e->Type);
				}
				e->CalcTorque(&TorqueDi, AxisPos, AxisVel);
			}
		}
		LeaveCriticalSection(&CriticalSection);

		// Device gain only (IDirectInputEffectDriver::SetGain).
		float Torque = (TorqueDi / 10000.0f) * (Gain / 10000.0f);
		if (Torque > 1.0f) Torque = 1.0f;
		if (Torque < -1.0f) Torque = -1.0f;

		Shared->Torque = Torque;
		Shared->Playing = Playing ? 1u : 0u;
		Shared->TickMs = GetTickCount64();
		Shared->Sequence = ++Seq;
		Shared->TypesSeen = typesSeen;
		Shared->TypesPlaying = typesPlaying;
		Shared->DownloadCount = downloadCount;
		Shared->LastEffectType = lastType;
		Shared->LastFlags = lastFlags;

		Sleep(2);
	}

	UnmapViewOfFile(Shared);
	CloseHandle(Mapping);
	return 0;
}
