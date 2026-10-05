#pragma once

#ifndef DIRECTINPUT_VERSION
#define DIRECTINPUT_VERSION 0x0800
#endif

#include <windows.h>
#include <dinput.h>
#include <math.h>

#define CONSTANT_FORCE  0x00
#define RAMP_FORCE      0x01
#define SQUARE          0x02
#define SINE            0x03
#define TRIANGLE        0x04
#define SAWTOOTH_UP     0x05
#define SAWTOOTH_DOWN   0x06
#define SPRING          0x07
#define DAMPER          0x08
#define INERTIA         0x09
#define FRICTION        0x0A
#define CUSTOM_FORCE    0x0B

class CEffect
{
public:
	CEffect();

	// Accumulate signed wheel torque in DI units (-10000..10000).
	// axisPos / axisVel are DI-scaled (-10000..10000) for condition effects.
	VOID CalcTorque(LONG* Torque, LONG AxisPos, LONG AxisVel);

	DWORD Type;
	DIEFFECT DiEffect;
	DIENVELOPE DiEnvelope;
	DICONSTANTFORCE DiConstantForce;
	DIPERIODIC DiPeriodic;
	DIRAMPFORCE DiRampforce;
	DICONDITION DiCondition;

	DWORD Handle;
	DWORD Status;
	DWORD PlayCount;
	DWORD StartTime;

	LONG DirectionSign;
	BOOL HasEnvelope;

	// Per-handle log rate limiting (DownloadEffect stream lines).
	DWORD LastLogTick;
	LONG LastLoggedExtra;
	DWORD LastReportedStatus;

	// Emulator mix options (updated each mixer tick from shared memory).
	static volatile LONG s_InvertConstantForce;   // 0/1
	static volatile LONG s_DamperVelScale;         // 10000 = 1.0
	static volatile LONG s_DamperDeadbandScale;    // 10000 = 1.0

private:
	// Duration 0 = infinite. CurrentPos = ms into the current iteration.
	LONG ApplyEnvelope(LONG Magnitude, ULONG Duration, ULONG CurrentPos) const;
	VOID CalcForce(ULONG Duration, ULONG CurrentPos, LONG AxisPos, LONG AxisVel, LONG* NormalLevel);
	static LONG EvalCondition(const DICONDITION& Cond, LONG Metric);
};
