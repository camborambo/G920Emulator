#include "effect_driver.h"
#include <stdlib.h>

// One mixer thread per process. Unbound constructs multiple IDirectInputEffectDriver
// instances; DownloadEffect hits one while a per-instance worker can still point at
// an empty instance (SHM downloads=0 / torque=0 while effects.log grows).
// Mix every registered live driver.
static volatile LONG g_MixerRunning = 0;
static HANDLE g_MixerThread = nullptr;

static const int kMaxDrivers = 16;
static CEffectDriver* g_Drivers[kMaxDrivers];
static int g_DriverCount = 0;

// Virtual G920 OEM contract: the game targets our device; we forward to any base.
// Steam / overlay / our app also CoCreate the OEM driver while enumerating.
// Keep DI fully functional for those hosts (a hard stub broke Unbound race rumble).
// Game process publishes Torque.*; non-game may only publish Aux* so it cannot
// wipe the game channel with an empty mixer.
static LONG g_HostKind = -1; // -1 unknown, 0 game, 1 non-game
static volatile LONG g_NonGameHostLogged = 0;
static volatile LONG g_AuxPublishLogged = 0;

// CF + periodics — road/rumble types Unbound streams in-race.
static const UINT32 kRumbleTypeMask =
	(1u << CONSTANT_FORCE) | (1u << SQUARE) | (1u << SINE) | (1u << TRIANGLE) |
	(1u << SAWTOOTH_UP) | (1u << SAWTOOTH_DOWN);

STDAPI_(DWORD) WINAPI EffectProc(LPVOID);

static bool IsNonGameFfbHostProcess()
{
	LONG kind = InterlockedCompareExchange(&g_HostKind, -1, -1);
	if (kind == 0) return false;
	if (kind == 1) return true;

	wchar_t path[MAX_PATH] = {};
	if (!GetModuleFileNameW(nullptr, path, MAX_PATH))
	{
		InterlockedExchange(&g_HostKind, 0);
		return false;
	}
	const wchar_t* base = wcsrchr(path, L'\\');
	base = base ? base + 1 : path;
	const bool nonGame =
		_wcsicmp(base, L"steam.exe") == 0 ||
		_wcsicmp(base, L"steamwebhelper.exe") == 0 ||
		_wcsicmp(base, L"gameoverlayui.exe") == 0 ||
		_wcsicmp(base, L"gameoverlayui64.exe") == 0 ||
		_wcsicmp(base, L"G920Emulator.exe") == 0;
	InterlockedExchange(&g_HostKind, nonGame ? 1 : 0);
	return nonGame;
}

static void LogNonGameHostOnce()
{
	if (InterlockedCompareExchange(&g_NonGameHostLogged, 1, 0) != 0)
		return;
	wchar_t path[MAX_PATH] = L"?";
	GetModuleFileNameW(nullptr, path, MAX_PATH);
	const wchar_t* base = wcsrchr(path, L'\\');
	base = base ? base + 1 : path;
	G920FfbLogCall(
		"SESSION HOST pid=%lu exe=%ls (DI enabled; publishes AuxTorque only — game owns Torque)",
		(unsigned long)GetCurrentProcessId(), base);
}

static void TryStartMixer()
{
	// Non-game hosts still get a mixer so GetEffectStatus / effect state stay coherent
	// for Steam Input, but EffectProc will not publish to SHM.
	if (g_MixerThread)
		return;
	// In-process only. v5 SHM isolates us from older DLL writers.
	if (InterlockedCompareExchange(&g_MixerRunning, 1, 0) != 0)
		return;

	DWORD threadId = 0;
	HANDLE t = CreateThread(nullptr, 0, EffectProc, nullptr, 0, &threadId);
	if (!t)
	{
		InterlockedExchange(&g_MixerRunning, 0);
		return;
	}
	g_MixerThread = t;
}

static void RegisterDriver(CEffectDriver* driver)
{
	if (!driver) return;
	EnterCriticalSection(&CriticalSection);
	for (int i = 0; i < g_DriverCount; i++)
	{
		if (g_Drivers[i] == driver)
		{
			LeaveCriticalSection(&CriticalSection);
			return;
		}
	}
	if (g_DriverCount < kMaxDrivers)
		g_Drivers[g_DriverCount++] = driver;
	LeaveCriticalSection(&CriticalSection);
}

static void UnregisterDriver(CEffectDriver* driver)
{
	if (!driver) return;
	EnterCriticalSection(&CriticalSection);
	for (int i = 0; i < g_DriverCount; i++)
	{
		if (g_Drivers[i] == driver)
		{
			g_Drivers[i] = g_Drivers[g_DriverCount - 1];
			g_Drivers[g_DriverCount - 1] = nullptr;
			g_DriverCount--;
			break;
		}
	}
	LeaveCriticalSection(&CriticalSection);
}

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
	LastReportedState = 0;
	LastUnknownStatusLogTick = 0;
	G920FfbAddObject();
	RegisterDriver(this);
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
		UnregisterDriver(this);
		// Do not tear down the process-wide mixer — other CEffectDriver instances
		// may still be live (Unbound opens the OEM driver more than once).
		WorkerThread = NULL;
		for (LONG i = 0; i < EffectCount; i++)
			delete EffectList[i];
		free(EffectList);
		G920FfbReleaseObject();
		delete this;
		return 0;
	}
	return ReferenceCount;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::DeviceID(DWORD, DWORD External, DWORD Begin, DWORD, LPVOID)
{
	RegisterDriver(this);
	if (IsNonGameFfbHostProcess())
		LogNonGameHostOnce();
	if (!g_MixerThread)
		G920FfbLogSession();
	G920FfbLogCall("CALL DeviceID drv=%p external=%lu begin=%lu",
		(void*)this, (unsigned long)External, (unsigned long)Begin);
	TryStartMixer();
	WorkerThread = g_MixerThread;
	return S_OK;
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

HRESULT STDMETHODCALLTYPE CEffectDriver::Escape(DWORD, DWORD, LPDIEFFESCAPE Escape)
{
	G920FfbLogCall("CALL Escape drv=%p cmd=0x%08lX",
		(void*)this, Escape ? (unsigned long)Escape->dwCommand : 0ul);
	return E_NOTIMPL;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::SetGain(DWORD, DWORD NewGain)
{
	const DWORD requested = NewGain;
	EnterCriticalSection(&CriticalSection);
	if (NewGain < 1) NewGain = 1;
	if (NewGain > 10000) NewGain = 10000;
	const BOOL changed = Gain != NewGain;
	Gain = NewGain;
	LeaveCriticalSection(&CriticalSection);
	if (changed)
		G920FfbLogCall("CALL SetGain drv=%p gain=%lu", (void*)this, (unsigned long)requested);
	return S_OK;
}

static const char* FfbCommandName(DWORD command)
{
	switch (command)
	{
	case DISFFC_RESET: return "RESET";
	case DISFFC_STOPALL: return "STOPALL";
	case DISFFC_PAUSE: return "PAUSE";
	case DISFFC_CONTINUE: return "CONTINUE";
	case DISFFC_SETACTUATORSON: return "ACTUATORSON";
	case DISFFC_SETACTUATORSOFF: return "ACTUATORSOFF";
	default: return "UNKNOWN";
	}
}

HRESULT STDMETHODCALLTYPE CEffectDriver::SendForceFeedbackCommand(DWORD, DWORD Command)
{
	G920FfbLogCall("CALL SendForceFeedbackCommand drv=%p cmd=%s(0x%lX)",
		(void*)this, FfbCommandName(Command), (unsigned long)Command);
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

	// SAFETYSWITCHON = the device can operate. SAFETYSWITCHOFF tells the game the wheel
	// cannot play force feedback, and some titles then never start their in-race effects.
	EnterCriticalSection(&CriticalSection);
	DeviceState->dwState = DIGFFS_POWERON | DIGFFS_SAFETYSWITCHON | DIGFFS_USERFFSWITCHON;
	if (EffectCount == 0) DeviceState->dwState |= DIGFFS_EMPTY;
	if (Stopped) DeviceState->dwState |= DIGFFS_STOPPED;
	if (Paused) DeviceState->dwState |= DIGFFS_PAUSED;
	DeviceState->dwState |= Actuator ? DIGFFS_ACTUATORSON : DIGFFS_ACTUATORSOFF;
	DeviceState->dwLoad = 0;
	const DWORD state = DeviceState->dwState;
	const BOOL changed = state != LastReportedState;
	LastReportedState = state;
	LeaveCriticalSection(&CriticalSection);
	if (changed)
		G920FfbLogCall("CALL GetForceFeedbackState drv=%p state=0x%08lX", (void*)this, (unsigned long)state);
	return S_OK;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::DownloadEffect(
	DWORD, DWORD EffectType, LPDWORD EffectHandle, LPCDIEFFECT DiEffect, DWORD Flags)
{
	if (Flags & DIEP_NODOWNLOAD)
	{
		G920FfbLogCall("CALL DownloadEffect NODOWNLOAD drv=%p type=%lu handle=%lu flags=0x%08lX",
			(void*)this, (unsigned long)EffectType,
			EffectHandle ? (unsigned long)*EffectHandle : 0ul, (unsigned long)Flags);
		return S_OK;
	}
	if (!EffectHandle || !DiEffect)
	{
		G920FfbLogCall("CALL DownloadEffect E_POINTER drv=%p type=%lu flags=0x%08lX",
			(void*)this, (unsigned long)EffectType, (unsigned long)Flags);
		return E_POINTER;
	}

	EnterCriticalSection(&CriticalSection);
	// Forza / Steam Input sometimes leave actuators off or paused; a download means they want force.
	Actuator = TRUE;
	Paused = FALSE;

	CEffect* Effect = NULL;
	BOOL isNewEffect = FALSE;
	if (*EffectHandle == 0)
	{
		isNewEffect = TRUE;
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
			const DWORD missing = *EffectHandle;
			LeaveCriticalSection(&CriticalSection);
			G920FfbLogCall("CALL DownloadEffect E_HANDLE drv=%p type=%lu handle=%lu flags=0x%08lX",
				(void*)this, (unsigned long)EffectType, (unsigned long)missing, (unsigned long)Flags);
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

	if (Flags & DIEP_ENVELOPE)
	{
		Effect->HasEnvelope = DiEffect->lpEnvelope != NULL;
		if (DiEffect->lpEnvelope)
			CopyMemory(&Effect->DiEnvelope, DiEffect->lpEnvelope, sizeof(DIENVELOPE));
	}

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
				// Polar: single-axis devices use 0 = +axis, 18000 = -axis (MSDN).
				// Treat the southern semicircle as negative so 18000 flips sign.
				LONG deg = ((d % 36000) + 36000) % 36000;
				if (deg > 9000 && deg < 27000)
					Effect->DirectionSign = -1;
			}
		}
	}

	DWORD condCount = 0;
	DWORD condPick = 0;
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
		{
			// Games often pass one DICONDITION per axis. Prefer the strongest
			// (largest |coeff|) so a zeroed first axis does not mute the wheel.
			ZeroMemory(&Effect->DiCondition, sizeof(DICONDITION));
			if (DiEffect->cbTypeSpecificParams >= sizeof(DICONDITION) && DiEffect->lpvTypeSpecificParams)
			{
				condCount = DiEffect->cbTypeSpecificParams / sizeof(DICONDITION);
				const DICONDITION* conds = (const DICONDITION*)DiEffect->lpvTypeSpecificParams;
				LONGLONG bestScore = -1;
				for (DWORD ci = 0; ci < condCount; ci++)
				{
					LONGLONG score =
						llabs((LONGLONG)conds[ci].lPositiveCoefficient) +
						llabs((LONGLONG)conds[ci].lNegativeCoefficient);
					if (score > bestScore)
					{
						bestScore = score;
						condPick = ci;
					}
				}
				CopyMemory(&Effect->DiCondition, &conds[condPick], sizeof(DICONDITION));
			}
			break;
		}
		default:
			break;
		}
	}

	// DIEP_START: start as part of DownloadEffect (normal for continuous constant force).
	if (Flags & DIEP_START)
	{
		Effect->Status = DIEGES_PLAYING;
		Effect->PlayCount = (DWORD)-1;
		Effect->StartTime = GetTickCount();
		Stopped = FALSE;
	}

	// Keep every DI effect type available. Real DI does not play until DIEP_START /
	// StartEffect. Unbound/Heat often skip Start and only stream TYPEPARAMS (0x100).
	// Forza re-downloads DIEP_ALL (0x3FF) every frame — sometimes as updates on the same
	// handle, sometimes as destroy+create (handle 0 each time). Treat both as live once
	// we are past the first few boot downloads.
	// Do NOT arm on the very first full creates alone: Unbound creates CF at 5000 and
	// Sine at 10000 as placeholders; arming that would hold a constant pull / rumble.
	if (Flags & DIEP_TYPESPECIFICPARAMS)
	{
		const BOOL hasStart = (Flags & DIEP_START) != 0;
		const BOOL paramStream =
			(Flags & ~(DIEP_TYPESPECIFICPARAMS | DIEP_NORESTART | DIEP_NODOWNLOAD)) == 0;
		// Update on an existing handle (Forza 0x3FF stream without destroy).
		const BOOL paramUpdate = !isNewEffect && !paramStream;
		// Past boot placeholders — Forza may recreate effects every frame (always "new").
		const BOOL pastBoot = DownloadCount >= 24;
		const BOOL alreadyPlaying = Effect->Status == DIEGES_PLAYING;

		BOOL arm = FALSE;
		if (Effect->Type == CONSTANT_FORCE)
		{
			if (Effect->DiConstantForce.lMagnitude == 0 && !hasStart)
				Effect->Status = 0;
			else if (hasStart || alreadyPlaying ||
					 (paramStream && Effect->DiConstantForce.lMagnitude != 0) ||
					 (paramUpdate && Effect->DiConstantForce.lMagnitude != 0) ||
					 (isNewEffect && pastBoot && Effect->DiConstantForce.lMagnitude != 0))
				arm = TRUE;
		}
		else if (Effect->Type == SPRING || Effect->Type == DAMPER ||
				 Effect->Type == INERTIA || Effect->Type == FRICTION)
		{
			// Conditions: create + stream both arm (arcade spring idles at coeff 0).
			arm = TRUE;
		}
		else if (Effect->Type == SQUARE || Effect->Type == SINE || Effect->Type == TRIANGLE ||
				 Effect->Type == SAWTOOTH_UP || Effect->Type == SAWTOOTH_DOWN)
		{
			// Periodics: Start, ongoing play, param stream, handle updates, or post-boot creates.
			// Early full create (0x3FF) with mag 10000 is Unbound boot rumble — stay quiet.
			if (hasStart || alreadyPlaying ||
				(paramStream && Effect->DiPeriodic.dwMagnitude != 0) ||
				(paramUpdate && Effect->DiPeriodic.dwMagnitude != 0) ||
				(isNewEffect && pastBoot && Effect->DiPeriodic.dwMagnitude != 0))
				arm = TRUE;
		}
		else if (Effect->Type == RAMP_FORCE)
		{
			if (hasStart || alreadyPlaying || paramStream || paramUpdate ||
				(isNewEffect && pastBoot))
				arm = TRUE;
		}

		if (arm)
		{
			const BOOL wasPlaying = alreadyPlaying;
			Effect->Status = DIEGES_PLAYING;
			if (Effect->PlayCount == 0)
				Effect->PlayCount = (DWORD)-1;
			// Do not refresh StartTime every download — that reset periodic phase
			// / envelopes every ~16ms and felt like grind on DD bases.
			if (!wasPlaying || Effect->StartTime == 0)
				Effect->StartTime = GetTickCount();
			Stopped = FALSE;
		}
	}

	LONG extra = 0;
	if (Effect->Type == CONSTANT_FORCE) extra = Effect->DiConstantForce.lMagnitude;
	else if (Effect->Type == SPRING)
		extra = Effect->DiCondition.lOffset;
	else if (Effect->Type == DAMPER)
	{
		LONG pos = Effect->DiCondition.lPositiveCoefficient;
		LONG neg = Effect->DiCondition.lNegativeCoefficient;
		extra = (llabs((LONGLONG)pos) >= llabs((LONGLONG)neg)) ? pos : neg;
	}
	else if (Effect->Type >= SQUARE && Effect->Type <= SAWTOOTH_DOWN)
		extra = (LONG)Effect->DiPeriodic.dwMagnitude;

	if (Effect->Type < 32)
		TypesSeen |= (1u << Effect->Type);
	DownloadCount++;
	LastEffectType = Effect->Type;
	LastFlags = Flags;
	Effect->LastDownloadTick = GetTickCount();

	// Games stream parameter-only updates every frame. Rate-limit per effect handle so
	// every type stays visible: log on a zero/non-zero transition, on a value change
	// (at most every 100 ms), and as a 1 s heartbeat.
	const DWORD streamFlags = DIEP_TYPESPECIFICPARAMS | DIEP_START | DIEP_NORESTART;
	const DWORD now = GetTickCount();
	BOOL logIt = (Flags & ~streamFlags) != 0;
	if (!logIt)
	{
		const DWORD since = now - Effect->LastLogTick;
		const BOOL crossedZero = (extra == 0) != (Effect->LastLoggedExtra == 0);
		if (crossedZero || since >= 1000 ||
			(extra != Effect->LastLoggedExtra && since >= 100))
			logIt = TRUE;
	}
	if (logIt)
	{
		Effect->LastLogTick = now;
		Effect->LastLoggedExtra = extra;
	}
	const DWORD logType = Effect->Type;
	const DWORD logHandle = Effect->Handle;
	const DICONDITION logCond = Effect->DiCondition;
	const LONG logDir = Effect->DirectionSign;

	LeaveCriticalSection(&CriticalSection);

	if (logIt)
	{
		G920FfbLogEffect(logType, Flags, logHandle, extra);
		if (logType == SPRING)
		{
			G920FfbLogSpringDetail(logHandle, Flags, condCount, condPick,
				logCond.lOffset, logCond.lPositiveCoefficient, logCond.lNegativeCoefficient,
				logCond.dwPositiveSaturation, logCond.dwNegativeSaturation,
				logCond.lDeadBand, logDir);
		}
	}

	TryStartMixer(); // claim mixer after Steam/idle hosts if DeviceID lost the race
	return S_OK;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::DestroyEffect(DWORD, DWORD EffectHandle)
{
	G920FfbLogCall("CALL DestroyEffect drv=%p handle=%lu", (void*)this, (unsigned long)EffectHandle);
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
	EnterCriticalSection(&CriticalSection);
	if (Mode & DIES_SOLO)
	{
		for (LONG i = 0; i < EffectCount; i++)
			if (EffectList[i]->Handle != EffectHandle)
				EffectList[i]->Status = 0;
	}
	BOOL found = FALSE;
	for (LONG i = 0; i < EffectCount; i++)
	{
		if (EffectList[i]->Handle == EffectHandle)
		{
			found = TRUE;
			EffectList[i]->Status = DIEGES_PLAYING;
			// DI: 0 iterations means forever for continuous effects (spring/CF).
			EffectList[i]->PlayCount = (Count == 0) ? (DWORD)-1 : Count;
			EffectList[i]->StartTime = GetTickCount();
			Stopped = FALSE;
			G920FfbLogEffect(EffectList[i]->Type, 0x80000000u /* Start */, EffectHandle, (LONG)Count);
			break;
		}
	}
	LeaveCriticalSection(&CriticalSection);
	if (!found)
		G920FfbLogCall("CALL StartEffect unknown drv=%p handle=%lu mode=0x%lX count=%lu",
			(void*)this, (unsigned long)EffectHandle, (unsigned long)Mode, (unsigned long)Count);
	TryStartMixer();
	return S_OK;
}

HRESULT STDMETHODCALLTYPE CEffectDriver::StopEffect(DWORD, DWORD EffectHandle)
{
	G920FfbLogCall("CALL StopEffect drv=%p handle=%lu", (void*)this, (unsigned long)EffectHandle);
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
	BOOL logIt = FALSE;
	BOOL found = FALSE;
	EnterCriticalSection(&CriticalSection);
	for (LONG i = 0; i < EffectCount; i++)
	{
		if (EffectList[i]->Handle == EffectHandle)
		{
			found = TRUE;
			*Status = EffectList[i]->Status;
			if (EffectList[i]->LastReportedStatus != *Status)
			{
				EffectList[i]->LastReportedStatus = *Status;
				logIt = TRUE;
			}
			break;
		}
	}
	const DWORD now = GetTickCount();
	if (!found && now - LastUnknownStatusLogTick >= 1000)
	{
		LastUnknownStatusLogTick = now;
		logIt = TRUE;
	}
	LeaveCriticalSection(&CriticalSection);
	if (logIt)
		G920FfbLogCall("CALL GetEffectStatus drv=%p handle=%lu status=0x%lX%s",
			(void*)this, (unsigned long)EffectHandle, (unsigned long)*Status, found ? "" : " (unknown)");
	return S_OK;
}

STDAPI_(DWORD) WINAPI EffectProc(LPVOID)
{
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

	// Never wipe Steering / SteeringVel / TypeGain — the emulator writes those.
	if (Shared->Magic != G920FFB_MAGIC || Shared->Version != G920FFB_VERSION)
	{
		Shared->Magic = G920FFB_MAGIC;
		Shared->Version = G920FFB_VERSION;
		Shared->Torque = 0;
		Shared->Playing = 0;
		Shared->Sequence = 0;
		Shared->TickMs = 0;
		Shared->TypesSeen = 0;
		Shared->TypesPlaying = 0;
		Shared->DownloadCount = 0;
		Shared->LastEffectType = 0;
		Shared->LastFlags = 0;
		for (int g = 0; g < G920FFB_TYPE_GAIN_COUNT; g++)
			Shared->TypeGain[g] = 10000;
		Shared->AuxTorque = 0;
		Shared->AuxPlaying = 0;
		Shared->AuxTypesPlaying = 0;
		Shared->AuxTickMs = 0;
		Shared->MixFlags = 0;
		Shared->DamperVelScale = 10000;
		Shared->DamperDeadbandScale = 10000;
		for (int g = 0; g < G920FFB_TYPE_GAIN_COUNT; g++)
		{
			Shared->TypeTorque[g] = 0;
			Shared->AuxTypeTorque[g] = 0;
		}
		Shared->GamePid = 0;
		Shared->AuxPid = 0;
	}
	else
	{
		Shared->Magic = G920FFB_MAGIC;
		Shared->Version = G920FFB_VERSION;
		// In-place grow: zero scales mean "unset".
		if (Shared->DamperVelScale == 0)
			Shared->DamperVelScale = 10000;
		if (Shared->DamperDeadbandScale == 0)
			Shared->DamperDeadbandScale = 10000;
	}

	UINT32 Seq = Shared->Sequence;
	DWORD lastMixLogMs = 0;
	for (;;)
	{
		LONG TorqueDi = 0;
		DWORD Gain = 10000;
		BOOL Playing = FALSE;
		UINT32 typesPlaying = 0;
		UINT32 typesSeen = 0;
		UINT32 downloadCount = 0;
		UINT32 lastType = 0;
		UINT32 lastFlags = 0;
		UINT32 bestDownloads = 0;
		LONG typeTorque[G920FFB_TYPE_GAIN_COUNT] = {};

		LONG AxisPos = (LONG)(Shared->Steering * 10000.0f);
		LONG AxisVel = (LONG)(Shared->SteeringVel * 10000.0f);
		if (AxisPos > 10000) AxisPos = 10000;
		if (AxisPos < -10000) AxisPos = -10000;
		if (AxisVel > 10000) AxisVel = 10000;
		if (AxisVel < -10000) AxisVel = -10000;

		UINT16 typeGain[G920FFB_TYPE_GAIN_COUNT];
		for (int g = 0; g < G920FFB_TYPE_GAIN_COUNT; g++)
		{
			UINT16 v = Shared->TypeGain[g];
			// 10000 = 100%; allow up to 200%. Unset 0 → 100%.
			if (v == 0) v = 10000;
			if (v > 20000) v = 20000;
			typeGain[g] = v;
		}

		CEffect::s_InvertConstantForce =
			(Shared->MixFlags & G920FFB_MIX_INVERT_CONSTANT) ? 1 : 0;
		{
			UINT16 vs = Shared->DamperVelScale;
			if (vs == 0) vs = 10000;
			if (vs > 40000) vs = 40000;
			CEffect::s_DamperVelScale = (LONG)vs;
			UINT16 ds = Shared->DamperDeadbandScale;
			if (ds == 0) ds = 10000;
			if (ds > 10000) ds = 10000;
			CEffect::s_DamperDeadbandScale = (LONG)ds;
		}

		EnterCriticalSection(&CriticalSection);
		for (int di = 0; di < g_DriverCount; di++)
		{
			CEffectDriver* Driver = g_Drivers[di];
			if (!Driver || Driver->Quit)
				continue;

			typesSeen |= Driver->TypesSeen;
			if (Driver->DownloadCount >= bestDownloads)
			{
				bestDownloads = Driver->DownloadCount;
				downloadCount = Driver->DownloadCount;
				Gain = Driver->Gain;
				lastType = Driver->LastEffectType;
				lastFlags = Driver->LastFlags;
			}

			if (!Driver->Actuator || Driver->Paused)
				continue;

			const DWORD mixNow = GetTickCount();
			for (LONG i = 0; i < Driver->EffectCount; i++)
			{
				CEffect* e = Driver->EffectList[i];
				// Forza often DownloadEffect then StopEffect/STOPALL before the next frame.
				// If params were streamed in the last 100 ms, keep outputting them.
				const BOOL recentlyDownloaded =
					e->LastDownloadTick != 0 && (mixNow - e->LastDownloadTick) < 100;
				BOOL live = (e->Status == DIEGES_PLAYING);
				if (!live && recentlyDownloaded)
				{
					if (e->Type == CONSTANT_FORCE && e->DiConstantForce.lMagnitude != 0)
						live = TRUE;
					else if (e->Type == SPRING || e->Type == DAMPER ||
							 e->Type == INERTIA || e->Type == FRICTION)
						live = TRUE;
					else if ((e->Type == SQUARE || e->Type == SINE || e->Type == TRIANGLE ||
							  e->Type == SAWTOOTH_UP || e->Type == SAWTOOTH_DOWN) &&
							 e->DiPeriodic.dwMagnitude != 0)
						live = TRUE;
					else if (e->Type == RAMP_FORCE)
						live = TRUE;
				}
				if (!live)
					continue;

				const DWORD savedStatus = e->Status;
				e->Status = DIEGES_PLAYING;
				LONG before = TorqueDi;
				Playing = TRUE;
				if (e->Type < 32)
					typesPlaying |= (1u << e->Type);
				e->CalcTorque(&TorqueDi, AxisPos, AxisVel);
				e->Status = savedStatus;
				LONG delta = TorqueDi - before;
				if (delta != 0 && e->Type < G920FFB_TYPE_GAIN_COUNT)
				{
					TorqueDi = before + (LONG)(((LONGLONG)delta * typeGain[e->Type]) / 10000);
					delta = TorqueDi - before;
				}
				if (e->Type < G920FFB_TYPE_GAIN_COUNT)
					typeTorque[e->Type] += delta;
			}
		}
		LeaveCriticalSection(&CriticalSection);

		DWORD nowMs = GetTickCount();
		if (Playing && (nowMs - lastMixLogMs) >= 1000)
		{
			lastMixLogMs = nowMs;
			const LONG periodic = typeTorque[SQUARE] + typeTorque[SINE] + typeTorque[TRIANGLE] +
				typeTorque[SAWTOOTH_UP] + typeTorque[SAWTOOTH_DOWN];
			const LONG other = typeTorque[RAMP_FORCE] + typeTorque[INERTIA] + typeTorque[FRICTION];
			G920FfbLogMix(AxisPos, AxisVel, typeTorque[CONSTANT_FORCE], periodic,
				typeTorque[SPRING], typeTorque[DAMPER], other, TorqueDi, typesPlaying);
		}

		float Torque = (TorqueDi / 10000.0f) * (Gain / 10000.0f);
		if (Torque > 1.0f) Torque = 1.0f;
		if (Torque < -1.0f) Torque = -1.0f;

		const UINT64 nowTick = GetTickCount64();
		if (!IsNonGameFfbHostProcess())
		{
			// Game (or any non-Steam host): primary OEM channel.
			Shared->Torque = Torque;
			Shared->Playing = Playing ? 1u : 0u;
			Shared->TickMs = nowTick;
			Shared->Sequence = ++Seq;
			Shared->TypesSeen = typesSeen;
			Shared->TypesPlaying = typesPlaying;
			Shared->DownloadCount = downloadCount;
			Shared->LastEffectType = lastType;
			Shared->LastFlags = lastFlags;
			for (int g = 0; g < G920FFB_TYPE_GAIN_COUNT; g++)
				Shared->TypeTorque[g] = typeTorque[g];
			Shared->GamePid = GetCurrentProcessId();
		}
		else
		{
			// Non-game (Steam / overlay): never write Torque (game owns that channel).
			// Layer onto Aux. If the game is not publishing (Forza via Steam Input is
			// often the sole OEM host), publish the full mix — not only "rumble" bits.
			const BOOL hasRumble = (typesPlaying & kRumbleTypeMask) != 0;
			// Require Playing — a quiet game mixer still refreshes TickMs every loop.
			const BOOL gameTorqueLive =
				Shared->Playing != 0 && (nowTick - Shared->TickMs) < 250;
			const BOOL publishAux = Playing && (hasRumble || !gameTorqueLive);
			if (publishAux)
			{
				Shared->AuxTorque = Torque;
				Shared->AuxPlaying = 1u;
				Shared->AuxTypesPlaying = typesPlaying;
				Shared->AuxTickMs = nowTick;
				for (int g = 0; g < G920FFB_TYPE_GAIN_COUNT; g++)
					Shared->AuxTypeTorque[g] = typeTorque[g];
				Shared->AuxPid = GetCurrentProcessId();
				if (InterlockedCompareExchange(&g_AuxPublishLogged, 1, 0) == 0)
					G920FfbLogCall(
						"AUX PUBLISH pid=%lu types=0x%X (%s)",
						(unsigned long)GetCurrentProcessId(),
						(unsigned)typesPlaying,
						gameTorqueLive ? "layered under game Torque" : "sole OEM host — full mix");
			}
			else if (Shared->AuxTickMs != 0 && nowTick - Shared->AuxTickMs > 250)
			{
				Shared->AuxTorque = 0;
				Shared->AuxPlaying = 0;
				Shared->AuxTypesPlaying = 0;
				Shared->AuxTickMs = 0;
				for (int g = 0; g < G920FFB_TYPE_GAIN_COUNT; g++)
					Shared->AuxTypeTorque[g] = 0;
			}
		}

		Sleep(2);
	}
}
