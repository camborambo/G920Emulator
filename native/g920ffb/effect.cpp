#include "effect.h"

CEffect::CEffect()
{
	Type = 0;
	ZeroMemory(&DiEffect, sizeof(DIEFFECT));
	ZeroMemory(&DiEnvelope, sizeof(DIENVELOPE));
	ZeroMemory(&DiConstantForce, sizeof(DICONSTANTFORCE));
	ZeroMemory(&DiPeriodic, sizeof(DIPERIODIC));
	ZeroMemory(&DiRampforce, sizeof(DIRAMPFORCE));
	ZeroMemory(&DiCondition, sizeof(DICONDITION));
	Handle = 0;
	Status = 0;
	PlayCount = 0;
	StartTime = 0;
	DirectionSign = 1;
}

LONG CEffect::EvalCondition(const DICONDITION& Cond, LONG Metric)
{
	// Standard DirectInput condition evaluation (Wine / PID-FF style).
	const LONG x = Metric - Cond.lOffset;
	const LONG dead = Cond.lDeadBand;

	LONG coeff = 0;
	LONG sat = 10000;
	LONG axis = 0;

	if (x > dead)
	{
		coeff = Cond.lPositiveCoefficient;
		sat = (LONG)Cond.dwPositiveSaturation;
		axis = x - dead;
	}
	else if (x < -dead)
	{
		coeff = Cond.lNegativeCoefficient;
		sat = (LONG)Cond.dwNegativeSaturation;
		axis = x + dead;
	}
	else
	{
		return 0;
	}

	if (sat <= 0) sat = 10000;

	LONGLONG force = -((LONGLONG)coeff * (LONGLONG)axis) / 10000;
	if (force > sat) force = sat;
	if (force < -sat) force = -sat;
	return (LONG)force;
}

VOID CEffect::CalcTorque(LONG* Torque, LONG AxisPos, LONG AxisVel)
{
	ULONG Duration = max(1UL, DiEffect.dwDuration / 1000);
	ULONG BeginTime = StartTime + (DiEffect.dwStartDelay / 1000);
	ULONG EndTime = 0xFFFFFFFF;
	if (PlayCount != (DWORD)-1)
		EndTime = BeginTime + Duration * PlayCount;
	ULONG CurrentTime = GetTickCount();

	if (Status != DIEGES_PLAYING || CurrentTime < BeginTime || CurrentTime > EndTime)
		return;

	LONG NormalRate, AttackLevel, FadeLevel;
	CalcEnvelope(Duration, (CurrentTime - BeginTime) % Duration, &NormalRate, &AttackLevel, &FadeLevel);

	LONG NormalLevel = 0;
	CalcForce(Duration, (CurrentTime - BeginTime) % Duration, NormalRate, AttackLevel, FadeLevel,
		AxisPos, AxisVel, &NormalLevel);

	// Per-effect gain (DIEFFECT.dwGain, 0..10000) ? game-authored, not an emulator tweak.
	LONG effectGain = DiEffect.dwGain;
	if (effectGain <= 0 || effectGain > 10000)
		effectGain = 10000;
	NormalLevel = (LONG)(((LONGLONG)NormalLevel * effectGain) / 10000);

	*Torque += NormalLevel * DirectionSign;
}

VOID CEffect::CalcEnvelope(ULONG Duration, ULONG CurrentPos, LONG* NormalRate, LONG* AttackLevel, LONG* FadeLevel)
{
	if ((DiEffect.dwFlags & DIEP_ENVELOPE) && DiEffect.lpEnvelope != NULL)
	{
		LONG AttackRate = 0;
		ULONG AttackTime = max(1UL, DiEnvelope.dwAttackTime / 1000);
		if (CurrentPos < AttackTime)
			AttackRate = (LONG)((AttackTime - CurrentPos) * 100 / AttackTime);

		LONG FadeRate = 0;
		ULONG FadeTime = max(1UL, DiEnvelope.dwFadeTime / 1000);
		ULONG FadePos = Duration - FadeTime;
		if (FadePos < CurrentPos)
			FadeRate = (LONG)((CurrentPos - FadePos) * 100 / FadeTime);

		*NormalRate = 100 - AttackRate - FadeRate;
		*AttackLevel = DiEnvelope.dwAttackLevel * AttackRate;
		*FadeLevel = DiEnvelope.dwFadeLevel * FadeRate;
	}
	else
	{
		*NormalRate = 100;
		*AttackLevel = 0;
		*FadeLevel = 0;
	}
}

VOID CEffect::CalcForce(ULONG Duration, ULONG CurrentPos, LONG NormalRate, LONG AttackLevel, LONG FadeLevel,
	LONG AxisPos, LONG AxisVel, LONG* NormalLevel)
{
	LONG Magnitude = 0;
	LONG Period;
	LONG R;

	switch (Type)
	{
	case SPRING:
		Magnitude = EvalCondition(DiCondition, AxisPos);
		Magnitude = (Magnitude * NormalRate + AttackLevel + FadeLevel) / 100;
		break;

	case DAMPER:
		Magnitude = EvalCondition(DiCondition, AxisVel);
		Magnitude = (Magnitude * NormalRate + AttackLevel + FadeLevel) / 100;
		break;

	case INERTIA:
		// Approximate accel with velocity metric when accel is unavailable.
		Magnitude = EvalCondition(DiCondition, AxisVel);
		Magnitude = (Magnitude * NormalRate + AttackLevel + FadeLevel) / 100;
		break;

	case FRICTION:
	{
		// Friction opposes motion: sign(velocity) * coefficient (via condition deadband=0).
		LONG metric = 0;
		if (AxisVel > 0) metric = 10000;
		else if (AxisVel < 0) metric = -10000;
		Magnitude = EvalCondition(DiCondition, metric);
		Magnitude = (Magnitude * NormalRate + AttackLevel + FadeLevel) / 100;
		break;
	}

	case CONSTANT_FORCE:
		Magnitude = DiConstantForce.lMagnitude;
		Magnitude = (Magnitude * NormalRate + AttackLevel + FadeLevel) / 100;
		break;

	case RAMP_FORCE:
	{
		LONG begin = DiRampforce.lStart;
		LONG end = DiRampforce.lEnd;
		Magnitude = begin + (end - begin) * (LONG)CurrentPos / (LONG)Duration;
		Magnitude = (Magnitude * NormalRate + AttackLevel + FadeLevel) / 100;
		break;
	}

	case CUSTOM_FORCE:
		break;

	case SQUARE:
		Period = max(1L, (LONG)(DiPeriodic.dwPeriod / 1000));
		R = ((LONG)CurrentPos % Period) * 360 / Period;
		R = (R + (LONG)(DiPeriodic.dwPhase / 100)) % 360;
		Magnitude = (LONG)DiPeriodic.dwMagnitude;
		Magnitude = (Magnitude * NormalRate + AttackLevel + FadeLevel) / 100;
		if (180 <= R) Magnitude = -Magnitude;
		Magnitude += DiPeriodic.lOffset;
		break;

	case SINE:
		Period = max(1L, (LONG)(DiPeriodic.dwPeriod / 1000));
		R = ((LONG)CurrentPos % Period) * 360 / Period;
		R = (R + (LONG)(DiPeriodic.dwPhase / 100)) % 360;
		Magnitude = (LONG)DiPeriodic.dwMagnitude;
		Magnitude = (Magnitude * NormalRate + AttackLevel + FadeLevel) / 100;
		Magnitude = (LONG)(Magnitude * sin(R * 3.14159265 / 180.0));
		Magnitude += DiPeriodic.lOffset;
		break;

	case TRIANGLE:
	case SAWTOOTH_UP:
	case SAWTOOTH_DOWN:
		Period = max(1L, (LONG)(DiPeriodic.dwPeriod / 1000));
		R = ((LONG)CurrentPos % Period) * 360 / Period;
		R = (R + (LONG)(DiPeriodic.dwPhase / 100)) % 360;
		Magnitude = (LONG)DiPeriodic.dwMagnitude;
		Magnitude = (Magnitude * NormalRate + AttackLevel + FadeLevel) / 100;
		if (Type == SAWTOOTH_UP)
			Magnitude = -Magnitude + (Magnitude * 2 * R / 360);
		else if (Type == SAWTOOTH_DOWN)
			Magnitude = Magnitude - (Magnitude * 2 * R / 360);
		else
		{
			if (R < 180) Magnitude = -Magnitude + (Magnitude * 2 * R / 180);
			else Magnitude = Magnitude - (Magnitude * 2 * (R - 180) / 180);
		}
		Magnitude += DiPeriodic.lOffset;
		break;

	default:
		break;
	}

	*NormalLevel = Magnitude;
}
